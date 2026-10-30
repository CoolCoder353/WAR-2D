using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Audio;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>
    /// The polish effects (docs/polish.md) that follow what the client already knows, owned by <see cref="ClientWorld"/>:
    /// smoke and fire on damaged buildings, a glint and <c>gem_mined</c> on the gem tile a Miner works, the construction loop
    /// under the player's own blueprints, <c>wall_hit</c> when a seen wall loses health, <c>unit_produced</c> for a new own
    /// unit beside its producer, scorch where a unit died, tread marks behind vehicles, and building deaths sized to the
    /// building with rubble left behind. It reads only replicated state, so it shows nothing a client couldn't already see.
    /// </summary>
    public sealed class WorldEffects
    {
        /// <summary>Seconds between smoke puffs from a building under <see cref="SmokeBelow"/> of its health.</summary>
        public const float SmokeSeconds = 0.45f;

        /// <summary>Seconds between flames on a building under <see cref="FireBelow"/> of its health.</summary>
        public const float FireSeconds = 0.3f;

        /// <summary>Health fractions below which a finished building smokes, and below which it also burns.</summary>
        public const float SmokeBelow = 0.5f, FireBelow = 0.25f;

        /// <summary>Seconds between a working Miner's glints: its payout interval (<c>ResourceSystem</c> pays once a second).</summary>
        public const float GlintSeconds = 1f;

        /// <summary>Fewest seconds between two <c>gem_mined</c> sounds, however many Miners are on screen.</summary>
        public const float GemSoundSeconds = 0.4f;

        /// <summary>
        /// <c>gem_mined</c> plays only zoomed right in, at <see cref="GemSoundScale"/> screen pixels per art pixel or more, for a
        /// Miner within <see cref="GemSoundReach"/> tiles of the screen's centre, at <see cref="GemSoundGain"/> of its volume
        /// (playtest 2026-10-30: a base full of Miners ticking was maddening).
        /// </summary>
        public const float GemSoundScale = 2f, GemSoundReach = 3f, GemSoundGain = 0.35f;

        /// <summary>Seconds after a blueprint's last progress record that it still counts as being built (the loop plays).</summary>
        public const float BuildingWindowSeconds = 3f;

        /// <summary>Tiles a vehicle moves between tread marks (each mark is half a tile long).</summary>
        public const float TrackSpacing = 0.5f;

        /// <summary>Seconds after the first batch before a new own unit counts as produced (a join or rejoin brings them all at once).</summary>
        public const float ProducedQuietSeconds = 2f;

        /// <summary>Tiles from a producer's footprint edge within which a new own unit counts as made by it.</summary>
        public const float ProducedReach = 2.5f;

        private readonly Dictionary<int, (float next, int count)> smoke = new Dictionary<int, (float, int)>();
        private readonly Dictionary<int, (float next, int count)> fire = new Dictionary<int, (float, int)>();
        private readonly Dictionary<int, float> glint = new Dictionary<int, float>();
        private readonly Dictionary<int, float> progressAt = new Dictionary<int, float>();
        private readonly Dictionary<int, byte> wallHealth = new Dictionary<int, byte>();
        private readonly List<int> stale = new List<int>();
        private readonly float2[] lastTrack;
        private readonly int[] trackId;
        private ClientBuildings buildingsSeen;
        private ClientTerrain terrainSeen;
        private float lastGemSound = float.NegativeInfinity;

        /// <param name="capacity">The client's unit id capacity (track state is kept by id index).</param>
        public WorldEffects(int capacity)
        {
            lastTrack = new float2[capacity];
            trackId = new int[capacity];
        }

        /// <summary>The local player's owner id, or 0 (the menu battle, a spectator before it has a player).</summary>
        public static int LocalOwner => DefenceView.Local is ClientPlayer p ? BuildingData.UIntToInt(p.netId) : 0;

        /// <summary>The camera's view in world units, grown by <paramref name="margin"/> tiles on each side.</summary>
        public static Rect ViewOf(Camera cam, float margin = 1f)
        {
            if (cam == null) return Rect.zero;
            float h = cam.orthographicSize * 2f, w = h * cam.aspect;
            return new Rect((Vector2)cam.transform.position - new Vector2(w, h) * 0.5f - Vector2.one * margin, new Vector2(w, h) + Vector2.one * margin * 2f);
        }

        /// <summary>
        /// Death explosions the server filtered to this player's view (<c>ClientPlayer.TargetPlayExplosions</c>). One on the
        /// anchor of a finished building the client saw is that building's: a collapse sized to its footprint and rubble over it.
        /// Anything else (a unit, a blueprint, a bomb) keeps the unit explosion.
        /// </summary>
        public static void Explosions(Vector2[] positions)
        {
            ClientBuildings known = ClientBuildings.Current;
            float now = Time.time;
            foreach (Vector2 p in positions)
            {
                if (known.TryTakeDying((int2)math.round((float2)p), now, out ClientBuildings.Entry e)
                    && e.Progress >= 100 && e.Data.buildingType != BuildingType.Bomb)
                {
                    int2 size = WorldStateManager.GetBuildingSize(e.Data.buildingType);
                    float2 centre = Footprint.VisualCenter((int2)math.round(e.Data.position), size);
                    Effects.BuildingCollapse(centre, size.x);
                    Decals.Rubble(centre, size.x);
                }
                else Effects.Explosion(p);
            }
        }

        /// <summary>A known unit died at <paramref name="position"/>: the scorch it leaves (the larger one under a 2-tile unit).</summary>
        public void UnitDied(Vector2 position, bool large) => Decals.Scorch(position, large);

        /// <summary>
        /// A unit became known at <paramref name="position"/>. A new own unit beside one of the player's finished producers was
        /// just made there: <c>unit_produced</c>. Quiet for <see cref="ProducedQuietSeconds"/> after the first batch.
        /// </summary>
        public void UnitAppeared(Vector2 position, int ownerId, float sinceFirstBatch)
        {
            int local = LocalOwner;
            if (local == 0 || ownerId != local || sinceFirstBatch < ProducedQuietSeconds) return;
            foreach (ClientBuildings.Entry e in ClientBuildings.Current.Entries.Values)
            {
                if (e.Data.ownerId != local || e.Ghost || e.Progress < 100 || !SpawnerRules.IsProducer(e.Data.buildingType)) continue;
                int2 size = WorldStateManager.GetBuildingSize(e.Data.buildingType);
                float2 centre = Footprint.VisualCenter((int2)math.round(e.Data.position), size);
                float2 d = math.abs((float2)position - centre) - (float2)size * 0.5f;
                if (math.cmax(d) > ProducedReach) continue;
                AudioService.Sfx("unit_produced", position);
                return;
            }
        }

        /// <summary>True for the unit types that leave tread marks.</summary>
        public static bool LeavesTracks(byte type) => type == (byte)UnitType.Tank || type == (byte)UnitType.Heavy || type == (byte)UnitType.Artillery;

        /// <summary>
        /// A known unit drawn at <paramref name="position"/> this frame: a vehicle that has moved <see cref="TrackSpacing"/> since its
        /// last mark lays the next one between them (on screen only). A jump of more than two tiles (a correction) lays none.
        /// </summary>
        public void Moved(int index, int id, byte type, float2 position, Rect view)
        {
            if (!LeavesTracks(type) || (uint)index >= (uint)trackId.Length) return;
            if (trackId[index] != id)
            {
                trackId[index] = id;
                lastTrack[index] = position;
                return;
            }
            float2 d = position - lastTrack[index];
            float lsq = math.lengthsq(d);
            if (lsq < TrackSpacing * TrackSpacing) return;
            float2 mid = (position + lastTrack[index]) * 0.5f;
            lastTrack[index] = position;
            if (lsq > 4f || !view.Contains((Vector2)mid)) return;
            Decals.Track(mid, math.atan2(d.y, d.x));
        }

        /// <summary>
        /// Once a frame, after the buildings are drawn: smoke and fire on damaged buildings and Miner glints on screen, the
        /// construction loop, and the wall-hit sounds.
        /// </summary>
        public void Buildings(ClientBuildings known, Rect view, float now)
        {
            Follow(known);
            int local = LocalOwner;
            Vector2? loopAt = null;
            float loopDistance = float.MaxValue;
            foreach (ClientBuildings.Entry e in known.Entries.Values)
            {
                if (e.Ghost) continue;
                int id = e.Data.id;
                BuildingType type = e.Data.buildingType;
                int2 size = WorldStateManager.GetBuildingSize(type);
                float2 centre = Footprint.VisualCenter((int2)math.round(e.Data.position), size);
                if (!view.Overlaps(new Rect((Vector2)(centre - (float2)size * 0.5f), (Vector2)(float2)size))) continue;

                if (e.Progress < 100)
                {
                    if (e.Data.ownerId != local || !progressAt.TryGetValue(id, out float at) || now - at > BuildingWindowSeconds) continue;
                    float distance = math.lengthsq(centre - (float2)view.center);
                    if (distance < loopDistance) { loopDistance = distance; loopAt = (Vector2)centre; }
                    continue;
                }
                if (type == BuildingType.Bomb) continue;
                float health = e.Health.maxHealth > 0f ? e.Health.currentHealth / e.Health.maxHealth : 1f;
                if (health < SmokeBelow) Emit(smoke, id, now, SmokeSeconds, centre, size, true);
                if (health < FireBelow) Emit(fire, id, now, FireSeconds, centre, size, false);
                if (type == BuildingType.Miner) Glint(e, id, now);
            }
            AudioService.Loop("construct_loop", loopAt);
            Forget(known);
        }

        /// <summary>A smoke puff or flame on one of the building's spots when it is due (spots alternate across the roof).</summary>
        private static void Emit(Dictionary<int, (float next, int count)> timers, int id, float now, float interval, float2 centre, int2 size, bool isSmoke)
        {
            if (!timers.TryGetValue(id, out var t))
            {
                timers[id] = (now + Hash01(id, isSmoke ? 1 : 2) * interval, 0); // staggered, so a row of buildings doesn't puff in step
                return;
            }
            if (now < t.next) return;
            int spots = size.x >= 5 ? 3 : size.x >= 3 ? 2 : 1;
            int spot = t.count % spots;
            // Somewhere on the upper part of the sprite (the roof in the 3/4 view), fixed per building and spot.
            float2 at = centre + new float2((Hash01(id, 10 + spot) - 0.5f) * 0.6f * size.x, (0.05f + Hash01(id, 20 + spot) * 0.35f) * size.y);
            if (isSmoke) Effects.Smoke((Vector2)at);
            else Effects.Fire((Vector2)at);
            timers[id] = (now + interval, t.count + 1);
        }

        /// <summary>A finished Miner facing a gem tile the client knows of glints on it once per payout, with <c>gem_mined</c> (rate-limited).</summary>
        private void Glint(ClientBuildings.Entry e, int id, float now)
        {
            if (!glint.TryGetValue(id, out float next))
            {
                glint[id] = now + Hash01(id, 3) * GlintSeconds;
                return;
            }
            if (now < next) return;
            glint[id] = now + GlintSeconds;
            int2 tile = (int2)math.round(e.Data.position) + MinerRules.FacingOffset(e.Data.rotation);
            ClientTerrain terrain = ClientTerrain.Current;
            int width = terrain.Width;
            if (width <= 0 || tile.x < 0 || tile.y < 0 || tile.x >= width) return;
            int index = tile.y * width + tile.x;
            if (index >= terrain.Tiles.Length || terrain.Tiles[index] != (byte)TileType.Gem) return;
            var at = new Vector2(tile.x + 0.5f, tile.y + 0.5f);
            Effects.GemSparkle(at);
            if (now - lastGemSound < GemSoundSeconds || !GemSoundAudible(AudioService.View != null ? AudioService.View : Camera.main, at)) return;
            lastGemSound = now;
            AudioService.Sfx("gem_mined", at, GemSoundGain);
        }

        /// <summary>True when a Miner's <c>gem_mined</c> at <paramref name="at"/> can be heard: zoomed right in and close to the screen's centre.</summary>
        public static bool GemSoundAudible(Camera cam, Vector2 at)
        {
            if (cam == null || CameraZoom.Scale(cam.orthographicSize, WAR2D.Art.ArtStyle.PixelsPerUnit, Screen.height) < GemSoundScale - 1e-3f) return false;
            return ((Vector2)cam.transform.position - at).sqrMagnitude <= GemSoundReach * GemSoundReach;
        }

        /// <summary>Subscribes to the stores in use now (each match makes new ones).</summary>
        private void Follow(ClientBuildings known)
        {
            if (buildingsSeen != known)
            {
                if (buildingsSeen != null) buildingsSeen.ProgressChanged -= OnProgress;
                buildingsSeen = known;
                known.ProgressChanged += OnProgress;
                progressAt.Clear();
            }
            ClientTerrain terrain = ClientTerrain.Current;
            if (terrainSeen != terrain)
            {
                if (terrainSeen != null) terrainSeen.Changed -= OnTerrain;
                terrainSeen = terrain;
                terrain.Changed += OnTerrain;
                wallHealth.Clear();
            }
        }

        private void OnProgress(int id, byte progress) => progressAt[id] = Time.time;

        /// <summary>A seen wall that lost health was hit: <c>wall_hit</c> on it. A wall first seen, or gone, plays nothing here.</summary>
        private void OnTerrain(IReadOnlyList<int2> tiles)
        {
            ClientTerrain terrain = terrainSeen;
            if (terrain == null || terrain.Width <= 0) return;
            foreach (int2 t in tiles)
            {
                int index = t.y * terrain.Width + t.x;
                int health = terrain.WallHealth(index);
                if (health <= 0)
                {
                    wallHealth.Remove(index);
                    continue;
                }
                if (wallHealth.TryGetValue(index, out byte before) && health < before) AudioService.Sfx("wall_hit", new Vector2(t.x + 0.5f, t.y + 0.5f));
                wallHealth[index] = (byte)health;
            }
        }

        /// <summary>Drops the timers of buildings the client no longer knows.</summary>
        private void Forget(ClientBuildings known)
        {
            if (smoke.Count + fire.Count + glint.Count + progressAt.Count == 0) return;
            stale.Clear();
            foreach (int id in smoke.Keys) if (!known.Entries.ContainsKey(id)) stale.Add(id);
            foreach (int id in fire.Keys) if (!known.Entries.ContainsKey(id)) stale.Add(id);
            foreach (int id in glint.Keys) if (!known.Entries.ContainsKey(id)) stale.Add(id);
            foreach (int id in progressAt.Keys) if (!known.Entries.ContainsKey(id)) stale.Add(id);
            foreach (int id in stale)
            {
                smoke.Remove(id);
                fire.Remove(id);
                glint.Remove(id);
                progressAt.Remove(id);
            }
        }

        /// <summary>Unsubscribes from the stores (the match ended).</summary>
        public void Dispose()
        {
            if (buildingsSeen != null) buildingsSeen.ProgressChanged -= OnProgress;
            if (terrainSeen != null) terrainSeen.Changed -= OnTerrain;
            buildingsSeen = null;
            terrainSeen = null;
        }

        /// <summary>A fixed value in [0, 1) for a building id and a salt (spot positions and stagger; no randomness per frame).</summary>
        public static float Hash01(int id, int salt)
        {
            uint h = (uint)id * 0x9E3779B1u ^ (uint)salt * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
