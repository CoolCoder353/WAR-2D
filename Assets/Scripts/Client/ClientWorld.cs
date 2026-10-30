using WAR2D.Dev.Logging;
using System;
using System.Collections.Generic;
using Config;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;
using ReadOnly = Unity.Collections.ReadOnlyAttribute;

namespace WAR2D.Client
{
    /// <summary>What the client knows about one unit, predicted to now.</summary>
    public struct ClientUnitView
    {
        public int Id, OwnerId;
        public UnitType Type;
        public float2 Position;
        public float Health01;
    }

    /// <summary>
    /// The client's units: replication batches in, predicted positions out, drawn with GPU instancing.
    /// Its clock (<see cref="ServerTime"/>) runs on real time and is nudged toward the newest batch's tick,
    /// never backward. Created by <c>UnitCommander</c> on clients.
    /// </summary>
    public sealed class ClientWorld : MonoBehaviour
    {
        private const int MaxTracersPerFrame = 200;

        public static ClientWorld Instance { get; private set; }

        private ClientUnitStore store;
        private float dt;
        private int lastBatchTick = -1;
        private float serverTime;
        private float sinceBatch; // seconds since the newest batch arrived: the server kept ticking meanwhile
        private NativeArray<float2> lastPosition;
        private NativeArray<float> rotation;
        private NativeArray<uint> frame;
        private NativeArray<float> lastAction; // server time of the unit's last attack, by index
        private UnitFrames.Table frames;
        private NativeArray<uint> color;
        private NativeArray<float> health01;
        private NativeArray<float> drawScale;
        private float[] scaleByType;
        private ArtAtlas unitAtlas;
        private NativeArray<byte> selected;
        // Hit feedback (HitFeedback), by index: whose health is tracked (an index is reused with a new id), its health last frame,
        // when it was last hit, its bar's trail, and whether it flashes now.
        private NativeArray<int> trackedId;
        private NativeArray<byte> lastHealth;
        private NativeArray<float> hitAt, trail;
        private NativeArray<byte> flash;
        private float lastDrawAt;
        private WorldEffects worldEffects;
        private float firstBatchAt = -1f; // Time.time the first batch arrived (the produced sound stays quiet just after)
        private NativeList<UnitInstance> instances, bars, rings;
        private NativeArray<int> barCount, ringCount;
        private InstancedUnitRenderer unitRenderer;
        private InstancedBuildingRenderer buildingRenderer;
        private System.Func<int, uint> buildingColour;
        private System.Func<int, bool> buildingSelected;
        private int tracerSerial; // counts tracers for the Reduced setting (every second one drawn)

        /// <summary>True for one of the local player's selected buildings (the SelectedOnly health bars).</summary>
        private static bool IsSelectedBuilding(int id)
        {
            UnitCommander commander = UnitCommander.Instance;
            if (commander == null) return false;
            IReadOnlyList<int> ids = commander.Selection.SelectedBuildings;
            for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return true;
            return false;
        }
        private readonly List<(int attacker, int target)> attacks = new List<(int, int)>();
        private readonly List<(int building, int target)> buildingAttacks = new List<(int, int)>();
        private readonly Dictionary<int, float> nextPulse = new Dictionary<int, float>();
        private readonly List<int> pulseGone = new List<int>();

        /// <summary>Seconds between a Jammer's pulses.</summary>
        public const float PulseSeconds = 2.5f;
        private readonly List<int> selectedIndices = new List<int>();
        private readonly Dictionary<int, uint> colorOfOwner = new Dictionary<int, uint>();

        /// <summary>Units known.</summary>
        public int KnownCount => store?.Count ?? 0;

        /// <summary>Seconds on the server's tick clock, smoothed.</summary>
        public float ServerTime => serverTime;

        /// <summary>Batches the client could not decode (logged and dropped).</summary>
        public int MalformedBatches { get; private set; }

        /// <summary>An owner's palette colour index (by default their lobby colour; the menu battle maps its bots).</summary>
        public Func<int, int> ColourIndexOf = PlayerPalette.ColourIndexOf;

        /// <summary>The camera tracers are culled against; null uses <c>Camera.main</c> (the menu's camera isn't tagged MainCamera).</summary>
        public Camera ViewCamera;

        /// <summary>The underlying store (tests).</summary>
        internal ClientUnitStore Store => store;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Initialise();
        }

        /// <summary>Allocates the store and buffers and subscribes to batches (Awake; tests call it directly).</summary>
        internal void Initialise()
        {
            if (store != null) return;
            Instance = this;
            GameConfigData config = ConfigLoader.LoadConfig();
            dt = config.Simulation.TickSeconds;
            int capacity = config.Simulation.MaxEntities;
            store = new ClientUnitStore(capacity, dt);
            store.Attack += (attacker, target) =>
            {
                attacks.Add((attacker, target));
                if ((uint)attacker < (uint)lastAction.Length) lastAction[attacker] = serverTime;
            };
            // A building's attack (v0.13): only a client that may know the building is sent one.
            store.BuildingAttack += (building, target) => buildingAttacks.Add((building, target));
            worldEffects = new WorldEffects(capacity);
            store.Died += OnUnitDied;
            store.Appeared += (index, at) => worldEffects.UnitAppeared(new Vector2(at.x, at.y), store.OwnerOf(index), Time.time - firstBatchAt);
            lastPosition = new NativeArray<float2>(capacity, Allocator.Persistent);
            rotation = new NativeArray<float>(capacity, Allocator.Persistent);
            frame = new NativeArray<uint>(capacity, Allocator.Persistent);
            lastAction = new NativeArray<float>(capacity, Allocator.Persistent);
            for (int i = 0; i < capacity; i++) lastAction[i] = float.NegativeInfinity;
            color = new NativeArray<uint>(capacity, Allocator.Persistent);
            health01 = new NativeArray<float>(capacity, Allocator.Persistent);
            drawScale = new NativeArray<float>(capacity, Allocator.Persistent);
            selected = new NativeArray<byte>(capacity, Allocator.Persistent);
            trackedId = new NativeArray<int>(capacity, Allocator.Persistent);
            lastHealth = new NativeArray<byte>(capacity, Allocator.Persistent);
            hitAt = new NativeArray<float>(capacity, Allocator.Persistent);
            trail = new NativeArray<float>(capacity, Allocator.Persistent);
            flash = new NativeArray<byte>(capacity, Allocator.Persistent);
            instances = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            bars = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            barCount = new NativeArray<int>(1, Allocator.Persistent);
            rings = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            ringCount = new NativeArray<int>(1, Allocator.Persistent);
            ReplicationClient.Received += OnBatch;
            Palettes.Changed += colorOfOwner.Clear;
            FogView.Ensure();
        }

        private void OnDestroy()
        {
            ReplicationClient.Received -= OnBatch;
            Palettes.Changed -= colorOfOwner.Clear;
            worldEffects?.Dispose();
            ClientFog.Clear(); // the match is over; the next one starts with a fresh snapshot
            ClientBuildings.Clear();
            ClientTerrain.Clear();
            if (Instance == this) Instance = null;
            unitRenderer?.Dispose();
            buildingRenderer?.Dispose();
            store?.Dispose();
            if (lastPosition.IsCreated)
            {
                lastPosition.Dispose(); rotation.Dispose(); frame.Dispose(); lastAction.Dispose(); color.Dispose(); health01.Dispose(); drawScale.Dispose(); selected.Dispose();
                trackedId.Dispose(); lastHealth.Dispose(); hitAt.Dispose(); trail.Dispose(); flash.Dispose();
                instances.Dispose(); bars.Dispose(); barCount.Dispose(); rings.Dispose(); ringCount.Dispose();
            }
        }

        private void OnBatch(ReplicationBatch batch) => Apply(batch);

        /// <summary>Decodes and applies a batch; a malformed one is logged and dropped.</summary>
        public void Apply(in ReplicationBatch batch)
        {
            store.Now = serverTime;
            if (firstBatchAt < 0f) firstBatchAt = Time.time;
            if (!store.Apply(batch.Payload, batch.Tick))
            {
                MalformedBatches++;
                WarLog.Warn(LogCategory.Replication, $"dropped a malformed batch for tick {batch.Tick}");
                return;
            }
            if (batch.Tick > lastBatchTick)
            {
                if (lastBatchTick < 0) serverTime = batch.Tick * dt;
                lastBatchTick = batch.Tick;
                sinceBatch = 0f;
            }
        }

        /// <summary>
        /// Advances the clock by <paramref name="deltaSeconds"/>, nudged (at most 10 % a frame, never
        /// backward) toward the server's estimated time: the newest batch's tick plus the time since it
        /// arrived. A quiet server sends no batches, but its clock keeps running.
        /// </summary>
        public void AdvanceClock(float deltaSeconds)
        {
            if (lastBatchTick < 0) return;
            sinceBatch += deltaSeconds;
            float target = lastBatchTick * dt + sinceBatch;
            float next = serverTime + deltaSeconds;
            next += (target - next) * 0.1f;
            serverTime = math.max(serverTime, next);
        }

        private void Update()
        {
            // A paused match sends no ticks: the prediction clock waits with it, so units don't run ahead along their routes.
            AdvanceClock(GameCore.Instance != null && GameCore.Instance.IsPaused ? 0f : Time.unscaledDeltaTime);
            store.PredictAll(serverTime);
            FireTracers();
            FireTurrets();
        }

        /// <summary>Copies a known unit's route (dev overlay).</summary>
        public bool CopyRoute(int id, List<float2> into) => store != null && store.CopyRoute(NetIdAllocator.IndexOf(id), into);

        /// <summary>True while the client knows the unit with this id.</summary>
        public bool IsKnownId(int id) => id > 0 && store.IdOf(NetIdAllocator.IndexOf(id)) == id;

        /// <summary>A known unit by id, predicted at the current clock.</summary>
        public bool TryGet(int id, out ClientUnitView view)
        {
            int index = NetIdAllocator.IndexOf(id);
            if (store.IdOf(index) != id || id <= 0) { view = default; return false; }
            view = new ClientUnitView
            {
                Id = id,
                OwnerId = store.OwnerOf(index),
                Type = (UnitType)store.TypeOf(index),
                Position = store.PredictOne(index, serverTime),
                Health01 = store.Health01(index),
            };
            return true;
        }

        /// <summary>True while the client knows the unit and it still has a route to follow (it is moving).</summary>
        public bool IsMoving(int id) => id > 0 && store.IdOf(NetIdAllocator.IndexOf(id)) == id && store.HasRoute(NetIdAllocator.IndexOf(id));

        /// <summary>Adds the ids of every unit the client knows of the owner (all of them, not only those on screen), optionally of one type.</summary>
        public void CollectOwn(int ownerId, List<int> into, UnitType type = UnitType.None)
        {
            NativeArray<int> known = store.Known;
            for (int i = 0; i < known.Length; i++)
            {
                int index = known[i];
                if (store.OwnerOf(index) != ownerId) continue;
                if (type != UnitType.None && (UnitType)store.TypeOf(index) != type) continue;
                into.Add(store.IdOf(index));
            }
        }

        /// <summary>Adds the ids of every known unit within <paramref name="radius"/> of a point that is not <paramref name="excludeOwner"/>'s (the enemy brackets of a range ring).</summary>
        public void CollectInRadius(float2 centre, float radius, int excludeOwner, List<int> into)
        {
            NativeArray<int> known = store.Known;
            NativeArray<float2> predicted = store.Predicted;
            int n = math.min(known.Length, predicted.Length);
            float r2 = radius * radius;
            for (int i = 0; i < n; i++)
            {
                int index = known[i];
                if (store.OwnerOf(index) == excludeOwner) continue;
                if (math.distancesq(predicted[i], centre) <= r2) into.Add(store.IdOf(index));
            }
        }

        /// <summary>Adds the ids of the owner's units inside the box (inclusive) to <paramref name="into"/>.</summary>
        public void QueryBox(float2 min, float2 max, int ownerId, List<int> into)
        {
            float2 lo = math.min(min, max), hi = math.max(min, max);
            NativeArray<int> known = store.Known;
            NativeArray<float2> predicted = store.Predicted;
            int n = math.min(known.Length, predicted.Length);
            for (int i = 0; i < n; i++)
            {
                int index = known[i];
                if (store.OwnerOf(index) != ownerId) continue;
                float2 p = predicted[i];
                if (p.x < lo.x || p.y < lo.y || p.x > hi.x || p.y > hi.y) continue;
                into.Add(store.IdOf(index));
            }
        }

        /// <summary>Plots every known unit on the minimap in its owner's colour (the minimap drops units on cells not seen now).</summary>
        public void PlotOn(MinimapTexture minimap)
        {
            NativeArray<int> known = store.Known;
            NativeArray<float2> predicted = store.Predicted;
            NativeArray<int> owners = store.OwnerArray;
            int n = math.min(known.Length, predicted.Length);
            int lastOwner = int.MinValue;
            Color32 colour = default;
            for (int i = 0; i < n; i++)
            {
                int owner = owners[known[i]];
                if (owner != lastOwner)
                {
                    lastOwner = owner;
                    colour = PlayerPalette.OfOwner(owner);
                }
                minimap.PlotUnit(predicted[i], colour);
            }
        }

        /// <summary>Predicted positions, parallel to the known list (refreshed every frame).</summary>
        public NativeArray<float2> Positions => store.Predicted;

        /// <summary>
        /// Draws the known buildings (instanced), then packs every known unit and draws it (and the health bars).
        /// Selected ids draw highlighted; roaming ids (Roam, J) get the dashed roaming ring.
        /// </summary>
        public void Draw(IReadOnlyCollection<int> selectedIds, IEnumerable<int> roamingIds = null)
        {
            // A -nographics player (perf clients) has no GPU buffers to draw with.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            DrawBuildings();
            int count = store.Count;
            if (count == 0 || store.Drawn.Length != count) return;
            if (unitRenderer == null)
            {
                ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Units);
                if (atlas == null) WarLog.Error(LogCategory.Art, $"Resources/{ArtAtlas.Folder}/{ArtAtlas.Units} is missing: run WAR-2D/Art/Build all.");
                else frames = new UnitFrames.Table(atlas);
                unitAtlas = atlas;
                unitRenderer = new InstancedUnitRenderer(atlas);
            }

            foreach (int index in selectedIndices) selected[index] = 0;
            selectedIndices.Clear();
            if (selectedIds != null)
                foreach (int id in selectedIds)
                {
                    int index = NetIdAllocator.IndexOf(id);
                    if (store.IdOf(index) != id) continue;
                    selected[index] = 1;
                    selectedIndices.Add(index);
                }
            // Bit 2: roaming (Figma "Playtest fixes" item 11).
            if (roamingIds != null)
                foreach (int id in roamingIds)
                {
                    int index = NetIdAllocator.IndexOf(id);
                    if (store.IdOf(index) != id) continue;
                    if (selected[index] == 0) selectedIndices.Add(index);
                    selected[index] |= 2;
                }

            NativeArray<int> known = store.Known;
            NativeArray<float2> positions = store.Drawn; // smoothed: corrections glide instead of stepping
            NativeArray<int> owners = store.OwnerArray;
            NativeArray<byte> healthBytes = store.HealthArray;
            float now = Time.time, frameSeconds = lastDrawAt > 0f ? math.max(0f, now - lastDrawAt) : 0f;
            lastDrawAt = now;
            Rect view = WorldEffects.ViewOf(ViewCamera != null ? ViewCamera : Camera.main);
            for (int i = 0; i < count; i++)
            {
                int index = known[i];
                float2 p = positions[i];
                float2 delta = p - lastPosition[index];
                bool moving = math.lengthsq(delta) > 1e-6f && math.lengthsq(delta) < 4f;
                if (moving) rotation[index] = math.atan2(delta.y, delta.x);
                lastPosition[index] = p;
                if (frames != null)
                    frame[index] = frames.Cell(store.TypeOf(index), rotation[index], moving, serverTime, serverTime - lastAction[index], index);
                color[index] = ColorOf(owners[index]);
                health01[index] = healthBytes[index] / 100f;
                TrackHit(index, healthBytes[index], now, frameSeconds);
                if (moving) worldEffects.Moved(index, store.IdOf(index), store.TypeOf(index), p, view);
                drawScale[index] = ScaleOf(store.TypeOf(index));
            }

            instances.ResizeUninitialized(count);
            bars.ResizeUninitialized(count);
            rings.ResizeUninitialized(count * 2); // a selected roaming unit has both rings
            new PackUnitsJob
            {
                Known = known,
                Positions = positions,
                Rotation = rotation,
                Frame = frame,
                Color = color,
                Health01 = health01,
                Scale = drawScale,
                LargeRingFrame = unitRenderer.LargeRingFrame,
                BarMode = SettingsStore.Current.HealthBars,
                Selected = selected,
                Flash = flash,
                Trail = trail,
                UnitHalfSize = 0.4f,
                RingFrame = unitRenderer.RingFrame,
                RoamRingFrame = unitRenderer.RoamRingFrame,
                Instances = instances.AsArray(),
                Bars = bars.AsArray(),
                BarCount = barCount,
                Rings = rings.AsArray(),
                RingCount = ringCount,
            }.Run();
            unitRenderer.DrawRings(rings.AsArray(), ringCount[0]);
            unitRenderer.Draw(instances.AsArray(), count);
            unitRenderer.DrawHealthBars(bars.AsArray(), barCount[0]);
        }

        /// <summary>
        /// A unit whose health dropped since last frame was hit: it flashes and its bar keeps a draining trail (<see cref="HitFeedback"/>).
        /// A unit new to this index (a new id) starts from its health, with no flash.
        /// </summary>
        private void TrackHit(int index, byte health, float now, float frameSeconds)
        {
            int id = store.IdOf(index);
            if (trackedId[index] != id)
            {
                trackedId[index] = id;
                lastHealth[index] = health;
                hitAt[index] = float.NegativeInfinity;
                trail[index] = health / 100f;
            }
            else if (health < lastHealth[index]) hitAt[index] = now;
            lastHealth[index] = health;
            trail[index] = HitFeedback.Trail(trail[index], health / 100f, now - hitAt[index], frameSeconds);
            flash[index] = (byte)(HitFeedback.Flashing(hitAt[index], now) ? 1 : 0);
        }

        /// <summary>How many tiles across a unit type is drawn (2 for the large units), from the art manifest.</summary>
        /// <summary>A known unit died: its scorch, and its wreck at its last facing (not a Suicide bomber, which blew itself up).</summary>
        private void OnUnitDied(int index, float2 at)
        {
            byte type = store.TypeOf(index);
            var position = new Vector2(at.x, at.y);
            worldEffects.UnitDied(position, ScaleOf(type) > 1.5f);
            if (unitAtlas == null || type == (byte)UnitType.SuicideBomber || (uint)index >= (uint)rotation.Length) return;
            string id = ((UnitType)type).ToString();
            int directions = unitAtlas.Directions(id);
            if (directions <= 0) return;
            Decals.Wreck(position, unitAtlas.Sprite(id, UnitFrames.Direction(rotation[index], directions) * unitAtlas.Frames(id)));
        }

        private float ScaleOf(byte type)
        {
            if (scaleByType == null)
            {
                scaleByType = new float[System.Enum.GetValues(typeof(UnitType)).Length];
                for (int t = 0; t < scaleByType.Length; t++)
                    scaleByType[t] = ArtManifest.TryGet(((UnitType)t).ToString(), out ArtEntry entry) && entry.Kind == ArtKind.Unit ? entry.WidthTiles : 1f;
            }
            return type < scaleByType.Length ? scaleByType[type] : 1f;
        }

        /// <summary>Buildings drawn last frame (tests).</summary>
        public int BuildingsDrawn => buildingRenderer?.LastDrawn ?? 0;

        /// <summary>The team colour (RGB) an owner's building was drawn in last frame (tests).</summary>
        public uint LastBuildingColour(int ownerId) => buildingRenderer?.LastColourOf(ownerId, ClientBuildings.Current) ?? 0;

        /// <summary>Draws every building the client knows (seen, ghosts, blueprints, known bombs) in one instanced call.</summary>
        private void DrawBuildings()
        {
            ClientBuildings known = ClientBuildings.Current;
            if (known.Entries.Count == 0) return;
            if (buildingRenderer == null)
            {
                ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Buildings);
                if (atlas == null) WarLog.Error(LogCategory.Art, $"Resources/{ArtAtlas.Folder}/{ArtAtlas.Buildings} is missing: run WAR-2D/Art/Build all.");
                buildingRenderer = new InstancedBuildingRenderer(atlas, ArtAtlas.Load(ArtAtlas.Effects));
                buildingColour = ColorOf;
            }
            buildingSelected ??= IsSelectedBuilding;
            buildingRenderer.Draw(known, Time.time, buildingColour, SettingsStore.Current.HealthBars, buildingSelected);
            PulseJammers(known);
            worldEffects.Buildings(known, WorldEffects.ViewOf(ViewCamera != null ? ViewCamera : Camera.main), Time.time);
        }

        /// <summary>
        /// The <c>JamPulse</c> on every finished Jammer the client sees now (not ghosts, not blueprints; the local
        /// player's own only while paid), every <see cref="PulseSeconds"/>, staggered per Jammer.
        /// </summary>
        private void PulseJammers(ClientBuildings known)
        {
            float now = Time.time;
            foreach (ClientBuildings.Entry e in known.Entries.Values)
            {
                if (e.Data.buildingType != BuildingType.Jammer) continue;
                int id = e.Data.id;
                if (e.Ghost || e.Progress < 100 || DefenceView.Unpaid(e))
                {
                    nextPulse.Remove(id);
                    continue;
                }
                if (!nextPulse.TryGetValue(id, out float at))
                {
                    nextPulse[id] = now + (uint)id % 10 * (PulseSeconds / 10f);
                    continue;
                }
                if (now < at) continue;
                nextPulse[id] = now + PulseSeconds;
                Effects.JamPulse(Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(BuildingType.Jammer)));
            }
            if (nextPulse.Count == 0) return;
            pulseGone.Clear();
            foreach (int id in nextPulse.Keys) if (!known.Entries.ContainsKey(id)) pulseGone.Add(id);
            foreach (int id in pulseGone) nextPulse.Remove(id);
        }

        /// <summary>Where a target named by an attack event is now: a known unit's prediction or a known building's centre.</summary>
        private bool TryTargetPosition(int target, out Vector3 to)
        {
            int targetIndex = NetIdAllocator.IndexOf(target);
            if (target > 0 && store.IdOf(targetIndex) == target)
            {
                float2 t = store.PredictOne(targetIndex, serverTime);
                to = new Vector3(t.x, t.y, 0);
                return true;
            }
            if (target != 0 && ClientBuildings.Current.Entries.TryGetValue(target, out ClientBuildings.Entry building))
            {
                float2 c = Footprint.VisualCenter((int2)math.round(building.Data.position), WorldStateManager.GetBuildingSize(building.Data.buildingType));
                to = new Vector3(c.x, c.y, 0);
                return true;
            }
            to = default;
            return false;
        }

        /// <summary>
        /// This frame's turret shots (v0.13): each turns its turret's head toward the target the client was told
        /// about (<see cref="ClientBuildings.Fired"/>), and, on screen, plays the muzzle flash, <c>shot_turret</c>
        /// and a tracer to the target (no tracer when the target wasn't named). A turret the client doesn't see
        /// (a ghost, or none) draws nothing.
        /// </summary>
        private void FireTurrets()
        {
            if (buildingAttacks.Count == 0) return;
            Camera cam = ViewCamera != null ? ViewCamera : Camera.main;
            ClientBuildings known = ClientBuildings.Current;
            float now = Time.time;
            int start = math.max(0, buildingAttacks.Count - MaxTracersPerFrame);
            int tracerMode = SettingsStore.Current.Tracers;
            for (int i = start; i < buildingAttacks.Count; i++)
            {
                var (building, target) = buildingAttacks[i];
                if (!known.Entries.TryGetValue(building, out ClientBuildings.Entry e) || e.Ghost || e.Data.buildingType != BuildingType.GunTurret) continue;
                float2 centre = Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType));
                bool named = TryTargetPosition(target, out Vector3 to);
                byte? facing = named && (to.x != centre.x || to.y != centre.y) ? ClientBuildings.FacingOf(to.x - centre.x, to.y - centre.y) : (byte?)null;
                known.Fired(building, facing, now);
                if (cam == null) continue;
                Vector3 viewport = cam.WorldToViewportPoint(new Vector3(centre.x, centre.y, 0));
                if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) continue;
                float angle = known.HeadOf(building).Facing * math.PI / 4f;
                bool line = named && Overlays.OverlayRules.KeepTracer(tracerMode, tracerSerial++);
                Effects.TurretShot(new Vector2(centre.x, centre.y), angle, line ? to : (Vector3?)null);
            }
            buildingAttacks.Clear();
        }

        private uint ColorOf(int ownerId)
        {
            if (colorOfOwner.TryGetValue(ownerId, out uint c)) return c;
            int colour = ColourIndexOf(ownerId);
            if (colour < 0) return InstancedUnitRenderer.Pack(new Color32(160, 160, 160, 255)); // not cached: the player may still arrive
            c = InstancedUnitRenderer.Pack(PlayerPalette.Of(colour));
            colorOfOwner[ownerId] = c;
            return c;
        }

        /// <summary>Tracers for this frame's attacks whose attacker is on screen, at most 200 (newest kept).</summary>
        private void FireTracers()
        {
            if (attacks.Count == 0) return;
            Camera cam = ViewCamera != null ? ViewCamera : Camera.main;
            int start = math.max(0, attacks.Count - MaxTracersPerFrame);
            int tracerMode = SettingsStore.Current.Tracers;
            for (int i = start; i < attacks.Count && cam != null; i++)
            {
                var (attacker, target) = attacks[i];
                if (!store.IsKnown(attacker)) continue; // an unseen attacker would predict from the origin
                float2 from = store.PredictOne(attacker, serverTime);
                Vector3 viewport = cam.WorldToViewportPoint(new Vector3(from.x, from.y, 0));
                if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) continue;
                if (!TryTargetPosition(target, out Vector3 to)) continue;
                // Face the target: the facing picks the drawn direction, and the fire frames show the barrel.
                if ((uint)attacker < (uint)rotation.Length && (to.x != from.x || to.y != from.y))
                    rotation[attacker] = math.atan2(to.y - from.y, to.x - from.x);
                // The Gameplay setting thins or hides the tracer lines; the flash, sound, shells and flame streams stay.
                Effects.Tracer(new Vector3(from.x, from.y, 0), to, (UnitType)store.TypeOf(attacker), Overlays.OverlayRules.KeepTracer(tracerMode, tracerSerial++));
            }
            attacks.Clear();
        }
    }
}
