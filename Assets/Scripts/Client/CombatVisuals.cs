using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Style;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;
using WAR2D.Sim;

namespace WAR2D.Client
{
    /// <summary>
    /// v0.20 Onslaught's drawing of the combat news (spec §8.2, Figma B1), from what the client is sent and nothing else: Bombers
    /// in the air on their arc over a shadow, fire on burning units and buildings (at most <c>Effects/MaxFires</c>, nearest the view's
    /// centre first), a Sniper's laser while it aims, the shells (<see cref="Effects.ShellFlight"/>, <see cref="Effects.IncomingShell"/>),
    /// and on the player's own selected Artillery and Snipers a thin reload bar (an aim bar while a Sniper aims) under the health bar,
    /// with a small flame on the bar of anything burning. Owned and driven by <see cref="ClientWorld"/>.
    /// </summary>
    public sealed class CombatVisuals
    {
        /// <summary>Tiles an airborne Bomber is drawn above its ground track at the leap's top.</summary>
        public const float LeapHeight = 1.25f;
        /// <summary>Seconds between an airborne Bomber's trail sparks.</summary>
        public const float SparkSeconds = 0.06f;
        /// <summary>Seconds between soot puffs over one burning unit or building.</summary>
        public const float SootSeconds = 0.6f;
        /// <summary>The reload or aim bar: width and height in tiles, and its gap under the health bar.</summary>
        public const float BarWidth = 0.6f, BarHeight = 0.06f, BarGap = 0.09f;

        private struct Leap
        {
            public float2 From, To;
            public float Start, End;
            public float LastSpark;
        }

        private struct Aim
        {
            public int Target;
            public float Start;
            public bool Glinted, Sounded;
        }

        /// <summary>Seconds between restarts of the burn loop, and how many of the nearest fires carry it.</summary>
        public const float BurnLoopSeconds = 1.5f;
        public const int BurnLoopVoices = 3;
        private float lastBurnLoop = float.NegativeInfinity;

        private readonly float dt;
        private readonly GameConfigData config;
        private readonly Dictionary<int, Leap> leaps = new Dictionary<int, Leap>();
        private readonly Dictionary<int, Aim> aims = new Dictionary<int, Aim>();
        private readonly Dictionary<int, float> lastShot = new Dictionary<int, float>();
        private readonly Dictionary<int, float> lastSoot = new Dictionary<int, float>();
        private readonly HashSet<int> burningBefore = new HashSet<int>(), burningNow = new HashSet<int>();
        private readonly List<(float distance, Vector2 at, int size, int key)> fires = new List<(float, Vector2, int, int)>();
        private readonly List<int> scratch = new List<int>();
        private readonly List<(int key, Leap leap)> leapUpdates = new List<(int, Leap)>();
        private readonly List<(int key, Aim aim)> aimUpdates = new List<(int, Aim)>();
        private GameObject root;
        private ArtAtlas atlas;
        private Sprite bar;
        private readonly List<SpriteRenderer> flameSprites = new List<SpriteRenderer>(), laserSprites = new List<SpriteRenderer>(),
            shadowSprites = new List<SpriteRenderer>(), barSprites = new List<SpriteRenderer>(), markerSprites = new List<SpriteRenderer>();
        private int flamesUsed, lasersUsed, shadowsUsed, barsUsed, markersUsed;
        private float serverTime;

        public CombatVisuals(float tickSeconds, GameConfigData config)
        {
            dt = tickSeconds;
            this.config = config;
            ClientCombat.Received += OnNews;
        }

        /// <summary>Stops listening and drops every sprite.</summary>
        public void Dispose()
        {
            ClientCombat.Received -= OnNews;
            if (root != null) Object.Destroy(root);
        }

        /// <summary>Fires drawn last frame (tests).</summary>
        public int FiresDrawn { get; private set; }
        /// <summary>Lasers drawn last frame (tests).</summary>
        public int LasersDrawn { get; private set; }
        /// <summary>Airborne Bombers drawn last frame (tests).</summary>
        public int LeapsDrawn { get; private set; }

        private void OnNews(CombatNews news)
        {
            float now = serverTime;
            foreach (LeapEntry l in news.Leaps)
            {
                leaps[l.Index] = new Leap { From = l.From, To = l.To, Start = l.Start * dt, End = l.End * dt, LastSpark = float.NegativeInfinity };
                Effects.Leap(new Vector2(l.From.x, l.From.y));
            }
            foreach (AimEntry a in news.Aims)
            {
                aims[a.Index] = new Aim { Target = a.TargetId, Start = Time.time };
            }
            foreach (ShellEntry s in news.Shells)
            {
                float land = s.Land * dt;
                if (s.Full) Effects.ShellFlight(new Vector2(s.From.x, s.From.y), new Vector2(s.To.x, s.To.y), math.max(0.05f, land - math.max(now, s.Launch * dt)));
                else Effects.IncomingShell(new Vector2(s.To.x, s.To.y), math.max(0.05f, land - now));
            }
        }

        /// <summary>An own unit fired (its reload bar restarts).</summary>
        public void Fired(int index) => lastShot[index] = Time.time;

        /// <summary>True while a Sniper aims at a target this client knows: where that target is drawn.</summary>
        public bool AimTarget(int index, out float2 at)
        {
            at = default;
            if (unitOfLast == null || !aims.TryGetValue(index, out Aim aim) || aim.Target == 0) return false;
            if ((ClientCombat.Current.StatusOf(index) & UnitStatusBits.Aiming) == 0) return false;
            return TryTarget(aim.Target, NetIdAllocator.IndexOf(aim.Target), unitOfLast, buildingsLast, out at);
        }

        private System.Func<int, (bool known, float2 at, byte type, float scale)> unitOfLast;
        private ClientBuildings buildingsLast;

        /// <summary>A unit left the client's knowledge: forget its leap, aim and reload.</summary>
        public void Forget(int index)
        {
            leaps.Remove(index);
            aims.Remove(index);
            lastShot.Remove(index);
            lastSoot.Remove(index);
        }

        /// <summary>
        /// True while the unit at <paramref name="index"/> is drawn in the air: <paramref name="drawn"/> is its sprite's position (raised by
        /// the arc) and <paramref name="ground"/> where its shadow goes.
        /// </summary>
        public bool Airborne(int index, out float2 drawn, out float2 ground, out float height01)
        {
            drawn = ground = default;
            height01 = 0f;
            if ((ClientCombat.Current.StatusOf(index) & UnitStatusBits.Airborne) == 0 || !leaps.TryGetValue(index, out Leap leap)) return false;
            float t = math.saturate((serverTime - leap.Start) / math.max(0.01f, leap.End - leap.Start));
            ground = math.lerp(leap.From, leap.To, t);
            height01 = 4f * t * (1f - t);
            drawn = ground + new float2(0f, LeapHeight * height01);
            return true;
        }

        /// <summary>
        /// Draws this frame's combat visuals. <paramref name="positionOf"/> gives a known unit's drawn position (false when it isn't known),
        /// <paramref name="typeOf"/> its type, <paramref name="ownSelected"/> the local player's selected unit indices.
        /// </summary>
        public void Draw(float now, float serverNow, Rect view, System.Func<int, (bool known, float2 at, byte type, float scale)> unitOf,
            IEnumerable<int> ownSelected, ClientBuildings buildings)
        {
            serverTime = serverNow;
            unitOfLast = unitOf;
            buildingsLast = buildings;
            if (atlas == null || atlas.Texture == null) atlas = ArtAtlas.Load(ArtAtlas.Effects);
            if (atlas == null) return;
            EnsureRoot();
            flamesUsed = lasersUsed = shadowsUsed = barsUsed = markersUsed = 0;
            Vector2 centre = view.center;
            ClientCombat combat = ClientCombat.Current;

            // Airborne Bombers: their shadow on the ground track and a trail of sparks.
            LeapsDrawn = 0;
            scratch.Clear();
            foreach (KeyValuePair<int, Leap> pair in leaps)
            {
                if (serverNow > pair.Value.End + 0.5f) { scratch.Add(pair.Key); continue; }
                if (!Airborne(pair.Key, out float2 drawn, out float2 ground, out float h)) continue;
                LeapsDrawn++;
                SpriteRenderer shadow = Next(shadowSprites, ref shadowsUsed, "LeapShadow", SortingOrders.Decals + 2);
                shadow.sprite = atlas.Sprite("LeapShadow", math.clamp((int)(h * 3.99f), 0, atlas.FrameCount("LeapShadow") - 1));
                shadow.transform.SetPositionAndRotation(new Vector3(ground.x, ground.y, 0f), Quaternion.identity);
                Leap leap = pair.Value;
                if (now - leap.LastSpark >= SparkSeconds)
                {
                    leap.LastSpark = now;
                    leapUpdates.Add((pair.Key, leap));
                    Effects.LeapSparks(new Vector2(drawn.x, drawn.y - 0.2f));
                }
            }
            foreach (int key in scratch) leaps.Remove(key);
            foreach (var (key, leap) in leapUpdates) leaps[key] = leap;
            leapUpdates.Clear();

            // Fire: burning units and seen buildings, nearest the view's centre first, capped.
            fires.Clear();
            burningNow.Clear();
            foreach (KeyValuePair<int, byte> pair in combat.Statuses)
            {
                if ((pair.Value & UnitStatusBits.Burning) == 0) continue;
                var (known, at, type, scale) = unitOf(pair.Key);
                if (!known) continue;
                burningNow.Add(pair.Key);
                if (!burningBefore.Contains(pair.Key)) Audio.AudioService.Sfx("flame_ignite", new Vector2(at.x, at.y));
                var p = new Vector2(at.x, at.y);
                fires.Add(((p - centre).sqrMagnitude, p, scale > 1.5f ? 2 : 1, pair.Key));
                Marker(p + new Vector2(0.32f, 0.4f * scale + 0.175f));
            }
            burningBefore.Clear();
            foreach (int k in burningNow) burningBefore.Add(k);
            foreach (KeyValuePair<int, ClientBuildings.Entry> pair in buildings.Entries)
            {
                ClientBuildings.Entry e = pair.Value;
                if (!e.Burning || e.Ghost) continue;
                int2 size = WorldStateManager.GetBuildingSize(e.Data.buildingType);
                float2 c = Footprint.VisualCenter((int2)math.round(e.Data.position), size);
                var p = new Vector2(c.x, c.y);
                fires.Add(((p - centre).sqrMagnitude, p, math.clamp(math.max(size.x, size.y), 1, 3), ~pair.Key));
                Marker(p + new Vector2(size.x * 0.5f, size.y * 0.5f + 0.2f));
            }
            fires.Sort((a, b) => a.distance.CompareTo(b.distance));
            int maxFires = math.max(0, config.Effects.MaxFires);
            if (fires.Count > 0 && now - lastBurnLoop >= BurnLoopSeconds)
            {
                lastBurnLoop = now;
                for (int i = 0; i < fires.Count && i < BurnLoopVoices; i++)
                    if (view.Contains(fires[i].at)) Audio.AudioService.Sfx("burn_loop", fires[i].at);
            }
            FiresDrawn = 0;
            for (int i = 0; i < fires.Count && i < maxFires; i++)
            {
                var (_, at, size, key) = fires[i];
                if (!view.Contains(at)) continue;
                string id = size >= 3 ? "BurnFlames3" : size == 2 ? "BurnFlames2" : "BurnFlames1";
                SpriteRenderer flame = Next(flameSprites, ref flamesUsed, id, SortingOrders.Effects);
                flame.sprite = atlas.Sprite(id, (int)(now * ArtStyle.FramesPerSecond + key) & 3);
                flame.transform.SetPositionAndRotation(new Vector3(at.x, at.y + 0.15f * size, 0f), Quaternion.identity);
                FiresDrawn++;
                if (!lastSoot.TryGetValue(key, out float sooted) || now - sooted >= SootSeconds)
                {
                    lastSoot[key] = now;
                    Effects.SootPuff(at + new Vector2(0.1f, 0.5f * size));
                }
            }

            // Lasers: a Sniper aiming, from its barrel to what it aims at, brightening as the aim completes.
            LasersDrawn = 0;
            scratch.Clear();
            float aimSeconds = math.max(0.05f, config.Sniper.AimSeconds);
            foreach (KeyValuePair<int, Aim> pair in aims)
            {
                var (known, from, _, _) = unitOf(pair.Key);
                if (!known || (combat.StatusOf(pair.Key) & UnitStatusBits.Aiming) == 0) { scratch.Add(pair.Key); continue; }
                Aim aim = pair.Value;
                bool changed = false;
                if (!aim.Sounded)
                {
                    aim.Sounded = changed = true;
                    Audio.AudioService.Sfx("sniper_aim", new Vector2(from.x, from.y));
                }
                if (aim.Target == 0 || !TryTarget(aim.Target, NetIdAllocator.IndexOf(aim.Target), unitOf, buildings, out float2 to))
                {
                    if (changed) aimUpdates.Add((pair.Key, aim));
                    continue; // the target isn't known here: no line
                }
                float charge = math.saturate((now - aim.Start) / aimSeconds);
                float2 d = to - from;
                float length = math.length(d);
                if (length < 0.1f) { if (changed) aimUpdates.Add((pair.Key, aim)); continue; }
                float2 dir = d / length;
                float2 muzzle = from + dir * 0.9f;
                float2 span = to - muzzle;
                float spanLength = math.length(span);
                SpriteRenderer laser = Next(laserSprites, ref lasersUsed, "AimLaser", SortingOrders.Effects);
                laser.sprite = atlas.Sprite("AimLaser", math.clamp((int)(charge * 4.99f), 0, 4));
                float2 mid = muzzle + span * 0.5f;
                laser.transform.SetPositionAndRotation(new Vector3(mid.x, mid.y, 0f), Quaternion.Euler(0f, 0f, math.degrees(math.atan2(dir.y, dir.x))));
                laser.transform.localScale = new Vector3(spanLength / 4f, 1f, 1f);
                LasersDrawn++;
                if (charge >= 0.9f && !aim.Glinted)
                {
                    Effects.MuzzleGlint(new Vector2(muzzle.x, muzzle.y));
                    aim.Glinted = changed = true;
                }
                if (changed) aimUpdates.Add((pair.Key, aim));
            }
            foreach (int key in scratch) aims.Remove(key);
            foreach (var (key, aim) in aimUpdates) aims[key] = aim;
            aimUpdates.Clear();

            // B1: the own selected Artillery's and Sniper's reload bar, a Sniper's aim bar while it aims (never an enemy's).
            if (ownSelected != null)
                foreach (int index in ownSelected)
                {
                    var (known, at, type, scale) = unitOf(index);
                    if (!known || (type != (byte)UnitType.Artillery && type != (byte)UnitType.Sniper)) continue;
                    UnitConfig u = config.GetUnit((UnitType)type);
                    bool aiming = (combat.StatusOf(index) & UnitStatusBits.Aiming) != 0 && aims.ContainsKey(index);
                    float fill = aiming ? math.saturate((now - aims[index].Start) / aimSeconds)
                        : lastShot.TryGetValue(index, out float shot) ? math.saturate((now - shot) / math.max(0.05f, u.AttackInterval)) : 1f;
                    Bar(new Vector2(at.x, at.y + 0.4f * scale + 0.175f - BarGap), fill, aiming ? new Color(0.95f, 0.25f, 0.2f) : new Color(0.55f, 0.75f, 0.95f));
                }

            Hide(flameSprites, flamesUsed);
            Hide(laserSprites, lasersUsed);
            Hide(shadowSprites, shadowsUsed);
            Hide(barSprites, barsUsed);
            Hide(markerSprites, markersUsed);
        }

        private bool TryTarget(int id, int index, System.Func<int, (bool known, float2 at, byte type, float scale)> unitOf, ClientBuildings buildings, out float2 at)
        {
            var (known, p, _, _) = unitOf(index);
            if (known) { at = p; return true; }
            if (buildings.Entries.TryGetValue(id, out ClientBuildings.Entry e) && !e.Ghost)
            {
                at = Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType));
                return true;
            }
            at = default;
            return false;
        }

        /// <summary>A small flame beside a health bar (Figma B1: the shape, not only the colour, says it burns).</summary>
        private void Marker(Vector2 at)
        {
            SpriteRenderer m = Next(markerSprites, ref markersUsed, "Fire", SortingOrders.Effects + 2);
            m.sprite = atlas.Sprite("Fire", (int)(Time.time * ArtStyle.FramesPerSecond) & 3);
            m.transform.SetPositionAndRotation(new Vector3(at.x, at.y, 0f), Quaternion.identity);
            m.transform.localScale = Vector3.one * 0.35f;
        }

        private void Bar(Vector2 at, float fill, Color colour)
        {
            if (bar == null)
            {
                var texture = new Texture2D(1, 1) { filterMode = FilterMode.Point };
                texture.SetPixel(0, 0, Color.white);
                texture.Apply();
                bar = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0f, 0.5f), 1f);
            }
            SpriteRenderer back = Next(barSprites, ref barsUsed, "Bar", SortingOrders.Effects + 1);
            back.sprite = bar;
            back.color = new Color(0f, 0f, 0f, 0.6f);
            back.transform.SetPositionAndRotation(new Vector3(at.x - BarWidth * 0.5f, at.y, 0f), Quaternion.identity);
            back.transform.localScale = new Vector3(BarWidth, BarHeight, 1f);
            SpriteRenderer front = Next(barSprites, ref barsUsed, "Bar", SortingOrders.Effects + 2);
            front.sprite = bar;
            front.color = colour;
            front.transform.SetPositionAndRotation(new Vector3(at.x - BarWidth * 0.5f, at.y, 0f), Quaternion.identity);
            front.transform.localScale = new Vector3(BarWidth * fill, BarHeight, 1f);
        }

        private void EnsureRoot()
        {
            if (root != null) return;
            root = new GameObject("CombatVisuals");
        }

        private SpriteRenderer Next(List<SpriteRenderer> pool, ref int used, string name, int order)
        {
            if (used == pool.Count)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                pool.Add(go.AddComponent<SpriteRenderer>());
            }
            SpriteRenderer s = pool[used++];
            s.sortingOrder = order;
            s.color = Color.white;
            s.transform.localScale = Vector3.one;
            if (!s.gameObject.activeSelf) s.gameObject.SetActive(true);
            return s;
        }

        private static void Hide(List<SpriteRenderer> pool, int used)
        {
            for (int i = used; i < pool.Count; i++)
                if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
        }
    }
}
