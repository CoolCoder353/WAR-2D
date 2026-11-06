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
        /// <summary>v0.20: the unit burns (its status bit, from the combat news this client was sent).</summary>
        public bool Burning;
    }

    /// <summary>
    /// The client's units: replication batches in, predicted positions out, drawn with GPU instancing.
    /// Its clock (<see cref="ServerTime"/>) runs on real time and drifts toward the batches' average, never
    /// backward. Created by <c>UnitCommander</c> on clients.
    /// </summary>
    public sealed class ClientWorld : MonoBehaviour
    {
        private const int MaxTracersPerFrame = 200;

        public static ClientWorld Instance { get; private set; }

        private ClientUnitStore store;
        private float dt;
        private int lastBatchTick = -1;
        private float serverTime;
        private float clockEstimate; // the server's clock going by the batches, their late and early arrivals averaged out

        /// <summary>How much of each batch's arrival time moves the estimate.</summary>
        private const float ClockBatchShare = 0.1f;
        /// <summary>The share of real time the clock may run fast or slow to reach the estimate: too little for any eye to catch.</summary>
        private const float ClockDrift = 0.1f;
        /// <summary>Further off than this (seconds) the clock is simply wrong, and closes the gap quickly.</summary>
        private const float ClockFarSeconds = 0.25f;
        private NativeArray<float2> lastPosition;
        private NativeArray<float> rotation;
        private NativeArray<uint> frame;
        private NativeArray<float> lastAction; // server time of the unit's last attack, by index
        // Turrets (TurretAim), by index: whose it is (an index is reused with a new id), its angle and when it last turned, and what it
        // aims at until when. Only ever touched on the main thread (and the draw job it runs), for units on screen or firing.
        private NativeArray<int> turretOf;
        private NativeArray<float> turretAngle, turretTurnedAt, aimUntil;
        private NativeArray<float2> aimAt;
        private NativeList<UnitInstance> turrets;
        private NativeList<UnitInstance> barrelShadows;
        private Effects.FlameLine flameLine;
        /// <summary>v0.20: fires, lasers, leaps and reload bars (<see cref="CombatVisuals"/>), and the health bars hidden in the air.</summary>
        private CombatVisuals combatVisuals;
        private NativeArray<byte> hideBar;
        private readonly List<int> ownSelected = new List<int>();
        private UnitFrames.Table frames;
        private NativeArray<UnitFrames.TypeCells> cells; // frames as the draw job reads them (empty until the atlas loads)
        private NativeArray<uint> color;
        private NativeArray<float> health01;
        private NativeArray<float> drawScale;
        private NativeArray<float> scaleByType;
        // The draw job's inputs and outputs: owner colours, units a replay's view hides, and this frame's hits and track marks.
        private NativeHashMap<int, uint> ownerColours;
        private NativeList<int> missingOwners;
        private NativeArray<byte> hidden;
        private NativeList<UnitHit> hits;
        private NativeList<float3> tracks;

        /// <summary>The colour of an owner whose colour isn't known yet (a player still arriving); never cached.</summary>
        private static readonly uint UnknownColour = InstancedUnitRenderer.Pack(new Color32(160, 160, 160, 255));
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
        /// <summary>
        /// An attack event the client was told about: attacker id (a unit's or a building's) and target id (0 unnamed,
        /// negative a wall tile). v0.16 target lines.
        /// </summary>
        public event Action<int, int> AttackSeen;

        internal void Initialise()
        {
            if (store != null) return;
            Instance = this;
            GameConfigData config = ConfigLoader.LoadConfig();
            dt = config.Simulation.TickSeconds;
            int capacity = config.Simulation.MaxEntities;
            store = new ClientUnitStore(capacity, dt) { LeadOpen = IsOpenFloor };
            store.Attack += (attacker, target) =>
            {
                attacks.Add((attacker, target));
                if ((uint)attacker < (uint)lastAction.Length) lastAction[attacker] = serverTime;
                AttackSeen?.Invoke(store.IdOf(attacker), target);
            };
            // A building's attack (v0.13): only a client that may know the building is sent one.
            store.BuildingAttack += (building, target) =>
            {
                buildingAttacks.Add((building, target));
                AttackSeen?.Invoke(building, target);
            };
            worldEffects = new WorldEffects(capacity);
            MinimapCombatConfig pulses = config.MinimapCombat;
            MinimapCombat.Current = new MinimapCombat(pulses.CellTiles, pulses.PulseSeconds, pulses.QuietSeconds, pulses.Max)
            {
                DecayPercentPerSecond = config.Resources.DecayPercentPerSecond,
            };
            store.Died += OnUnitDied;
            // v0.20: the combat news's drawing, and what it forgets when a unit leaves.
            combatVisuals = new CombatVisuals(dt, config);
            hideBar = new NativeArray<byte>(capacity, Allocator.Persistent);
            store.Forgotten += index => { ClientCombat.Current.Forget(index); combatVisuals.Forget(index); };
            Effects.LandingShown = p => UnitShown == null ? ClientFog.Current.State == null || ClientFog.Current.Sees(p.x, p.y) : UnitShown(0, new float2(p.x, p.y));
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
            turretOf = new NativeArray<int>(capacity, Allocator.Persistent);
            turretAngle = new NativeArray<float>(capacity, Allocator.Persistent);
            turretTurnedAt = new NativeArray<float>(capacity, Allocator.Persistent);
            aimUntil = new NativeArray<float>(capacity, Allocator.Persistent);
            aimAt = new NativeArray<float2>(capacity, Allocator.Persistent);
            cells = new NativeArray<UnitFrames.TypeCells>(0, Allocator.Persistent);
            ownerColours = new NativeHashMap<int, uint>(16, Allocator.Persistent);
            missingOwners = new NativeList<int>(16, Allocator.Persistent);
            hidden = new NativeArray<byte>(capacity, Allocator.Persistent);
            hits = new NativeList<UnitHit>(256, Allocator.Persistent);
            tracks = new NativeList<float3>(256, Allocator.Persistent);
            turrets = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            barrelShadows = new NativeList<UnitInstance>(64, Allocator.Persistent);
            flameLine = TryFlameLine;
            instances = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            bars = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            barCount = new NativeArray<int>(1, Allocator.Persistent);
            rings = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            ringCount = new NativeArray<int>(1, Allocator.Persistent);
            ReplicationClient.Received += OnBatch;
            Palettes.Changed += ClearColours;
            FogView.Ensure();
        }

        private void ClearColours()
        {
            colorOfOwner.Clear();
            if (ownerColours.IsCreated) ownerColours.Clear();
        }

        private void OnDestroy()
        {
            ReplicationClient.Received -= OnBatch;
            Palettes.Changed -= ClearColours;
            worldEffects?.Dispose();
            combatVisuals?.Dispose();
            Effects.LandingShown = null;
            if (hideBar.IsCreated) hideBar.Dispose();
            ClientFog.Clear(); // the match is over; the next one starts with a fresh snapshot
            ClientBuildings.Clear();
            ClientCombat.Clear();
            ClientTerrain.Clear();
            if (Instance == this) Instance = null;
            unitRenderer?.Dispose();
            buildingRenderer?.Dispose();
            store?.Dispose();
            if (turrets.IsCreated) { turrets.Dispose(); barrelShadows.Dispose(); }
            if (turretOf.IsCreated)
            {
                turretOf.Dispose(); turretAngle.Dispose(); turretTurnedAt.Dispose(); aimUntil.Dispose(); aimAt.Dispose();
                cells.Dispose(); ownerColours.Dispose(); missingOwners.Dispose(); hidden.Dispose(); hits.Dispose(); tracks.Dispose();
            }
            if (scaleByType.IsCreated) scaleByType.Dispose();
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
                float at = batch.Tick * dt;
                if (lastBatchTick < 0) serverTime = at;
                clockEstimate = lastBatchTick < 0 || math.abs(at - clockEstimate) > ClockFarSeconds ? at : clockEstimate + (at - clockEstimate) * ClockBatchShare;
                lastBatchTick = batch.Tick;
            }
        }

        /// <summary>
        /// Advances the clock by <paramref name="deltaSeconds"/>, never backward, drifting toward the server's estimated
        /// time. One late batch must not stop it: every unit on screen would stall with it. A quiet server sends no
        /// batches, but its clock keeps running.
        /// </summary>
        public void AdvanceClock(float deltaSeconds)
        {
            if (lastBatchTick < 0) return;
            clockEstimate += deltaSeconds;
            float off = clockEstimate - serverTime, drift = ClockDrift * deltaSeconds;
            float next = serverTime + deltaSeconds;
            next += math.abs(off) > ClockFarSeconds ? (clockEstimate - next) * 0.1f : math.clamp(off, -drift, drift);
            serverTime = math.max(serverTime, next);
        }

        /// <summary>A replay's player advances the clock itself, at the playback speed and only while playing.</summary>
        public bool ClockDriven { get; set; }

        private void Update()
        {
            // A paused match sends no ticks: the prediction clock waits with it, so units don't run ahead along their routes.
            AdvanceClock(ClockDriven || (GameCore.Instance != null && GameCore.Instance.IsPaused) ? 0f : Time.unscaledDeltaTime);
            store.PredictAll(serverTime);
            FireTracers();
            FireTurrets();
        }

        /// <summary>
        /// Shows an own order on screen at once, as a racing game moves your kart before the server hears of it. Drawing only: the order
        /// still goes to the server, which checks it, and the units glide onto whatever it decides.
        /// </summary>
        public void ShowOrder(OrderKind kind, int2 goal, IReadOnlyList<int> ids)
        {
            // Work orders aren't shown: the server picks the tile a worker stands on, which can be another side of the job.
            if (kind == OrderKind.Detonate || kind == OrderKind.Build || kind == OrderKind.Repair || kind == OrderKind.Dig) return;
            bool halt = kind == OrderKind.Stop || kind == OrderKind.Hold;
            bool walkAll = kind == OrderKind.Move || kind == OrderKind.AttackMove;
            for (int i = 0; i < ids.Count; i++)
            {
                int index = NetIdAllocator.IndexOf(ids[i]);
                if (store.IdOf(index) != ids[i]) continue;
                if (halt) store.ShowHalt(index);
                else store.ShowMove(index, (float2)goal + 0.5f, walkAll ? 0.5f : ConfigLoader.LoadConfig().GetUnit((UnitType)store.TypeOf(index)).Range);
            }
        }

        private static bool IsOpenFloor(float2 p)
        {
            WorldStateManager manager = WorldStateManager.Instance;
            return manager == null || manager.Map == null || manager.Map.Grid.IsWalkable((int2)math.floor(p));
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
                Burning = (ClientCombat.Current.StatusOf(index) & WAR2D.Sim.UnitStatusBits.Burning) != 0,
            };
            return true;
        }

        /// <summary>True while the client knows the unit and it is still short of its route's end (it is moving).</summary>
        public bool IsMoving(int id) => id > 0 && store.IdOf(NetIdAllocator.IndexOf(id)) == id && store.IsMoving(NetIdAllocator.IndexOf(id), serverTime);

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
                else
                {
                    frames = new UnitFrames.Table(atlas);
                    cells.Dispose();
                    cells = frames.ToNative(Allocator.Persistent);
                }
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
            float now = Time.time, frameSeconds = lastDrawAt > 0f ? math.max(0f, now - lastDrawAt) : 0f;
            lastDrawAt = now;
            Rect view = WorldEffects.ViewOf(ViewCamera != null ? ViewCamera : Camera.main);
            Camera viewCamera = ViewCamera != null ? ViewCamera : Camera.main;
            // With no camera to cull by, every turret draws: culling against an empty view hid every unit's head (replay, 2026-11-06).
            Rect turretView = viewCamera != null ? WorldEffects.ViewOf(viewCamera, TurretViewMargin) : new Rect(-1e6f, -1e6f, 2e6f, 2e6f);
            turrets.Clear();
            barrelShadows.Clear();
            hits.Clear();
            tracks.Clear();
            CacheOwnerColours(known, owners);
            if (UnitShown != null) // a replay's per-player view: the delegate can't run in the job
                for (int i = 0; i < count; i++) hidden[known[i]] = (byte)(UnitShown(owners[known[i]], positions[i]) ? 0 : 1);
            // v0.20 (spec §8.3): a Sniper's head turns to what it aims at and holds there while it aims (the job turns it).
            foreach (KeyValuePair<int, byte> status in ClientCombat.Current.Statuses)
                if ((status.Value & WAR2D.Sim.UnitStatusBits.Aiming) != 0 && combatVisuals.AimTarget(status.Key, out float2 aimed))
                {
                    aimAt[status.Key] = aimed;
                    aimUntil[status.Key] = now + TurretAim.HoldSeconds;
                }
            // Per unit: facing, frame, colour, hit feedback, track marks, size and turret (Burst; 2026-11-06, 4.5 of 8 ms a frame
            // at 10,000 units as a managed loop).
            new PrepareUnitsJob
            {
                Known = known,
                Positions = positions,
                Owners = owners,
                Types = store.TypeArray,
                Health = store.HealthArray,
                States = store.StateArray,
                LastAction = lastAction,
                Selected = selected,
                ScaleByType = ScaleTable(),
                Hidden = hidden,
                UseHidden = UnitShown != null,
                Colours = ownerColours,
                UnknownColour = UnknownColour,
                Cells = cells,
                HasFrames = frames != null,
                ServerTime = serverTime,
                Now = now,
                FrameSeconds = frameSeconds,
                LocalOwner = WorldEffects.LocalOwner,
                View = new float4(view.xMin, view.yMin, view.xMax, view.yMax),
                TurretView = new float4(turretView.xMin, turretView.yMin, turretView.xMax, turretView.yMax),
                BarrelShadowFrame = unitRenderer.BarrelShadowFrame,
                LastPosition = lastPosition,
                Rotation = rotation,
                Frame = frame,
                Color = color,
                Health01 = health01,
                DrawScale = drawScale,
                TrackedId = trackedId,
                LastHealth = lastHealth,
                HitAt = hitAt,
                Trail = trail,
                Flash = flash,
                TrackIds = worldEffects.TrackIds,
                LastTracks = worldEffects.LastTracks,
                TurretOf = turretOf,
                TurretAngle = turretAngle,
                TurretTurnedAt = turretTurnedAt,
                AimUntil = aimUntil,
                AimAt = aimAt,
                Turrets = turrets,
                BarrelShadows = barrelShadows,
                Hits = hits,
                Tracks = tracks,
            }.Run();
            // The job's main-thread effects, in unit order: hits on the minimap, then track marks.
            MinimapCombat combat = MinimapCombat.Current;
            for (int i = 0; i < hits.Length && combat != null; i++)
            {
                UnitHit hit = hits[i];
                // v0.16: fights on the minimap, but not an own unit's upkeep decay (code review 2026-11-03).
                if (!(hit.Own && combat.IsOwnDecay(hit.Drop, hit.SinceLastHit))) combat.Hit(hit.Position, hit.Own, now);
            }
            for (int i = 0; i < tracks.Length; i++) Decals.Track(tracks[i].xy, tracks[i].z);
            RaiseAirborne(known, positions, count);
            Effects.FollowFlames(flameLine);
            ownSelected.Clear();
            foreach (int index in selectedIndices)
                if ((selected[index] & 1) != 0 && store.IsKnown(index) && store.OwnerOf(index) == WorldEffects.LocalOwner) ownSelected.Add(index);
            combatVisuals.Draw(now, serverTime, view, UnitAt, ownSelected, BuildingsShown ?? ClientBuildings.Current);

            instances.ResizeUninitialized(count);
            bars.ResizeUninitialized(count);
            rings.ResizeUninitialized(count * 2 + barrelShadows.Length); // a selected roaming unit has both rings
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
                HideBar = hideBar,
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
            for (int i = 0; i < barrelShadows.Length; i++) rings[ringCount[0] + i] = barrelShadows[i];
            unitRenderer.DrawRings(rings.AsArray(), ringCount[0] + barrelShadows.Length);
            unitRenderer.Draw(instances.AsArray(), count);
            unitRenderer.DrawTurrets(turrets.AsArray(), turrets.Length);
            unitRenderer.DrawHealthBars(bars.AsArray(), barCount[0]);
        }

        private readonly List<int> barsHidden = new List<int>();

        /// <summary>
        /// v0.20: a Bomber in the air is drawn raised on its leap's arc with no health bar (Figma B1); its tracks, facing and fires stay on
        /// the ground track below (the draw job has already used that). Walks the units only while a leap is on.
        /// </summary>
        private void RaiseAirborne(NativeArray<int> known, NativeArray<float2> positions, int count)
        {
            foreach (int index in barsHidden) hideBar[index] = 0;
            barsHidden.Clear();
            bool any = false;
            foreach (KeyValuePair<int, byte> status in ClientCombat.Current.Statuses)
                if ((status.Value & WAR2D.Sim.UnitStatusBits.Airborne) != 0) { any = true; break; }
            if (!any) return;
            for (int i = 0; i < count; i++)
            {
                int index = known[i];
                if (!combatVisuals.Airborne(index, out float2 raised, out _, out _)) continue;
                positions[i] = raised;
                hideBar[index] = 1;
                barsHidden.Add(index);
            }
        }

        /// <summary>Tiles past the screen's edge a unit's turret is still drawn: the longest barrel and its shadow reach this far.</summary>
        private const float TurretViewMargin = 2.5f;

        /// <summary>Tiles across the raised barrel's shadow sprite is drawn.</summary>
        private const float BarrelShadowTiles = 3f;

        /// <summary>A new unit on this index starts with its turret straight ahead and no aim.</summary>
        private void ResetTurretIfNew(int index) =>
            ResetTurretIfNew(index, store.IdOf(index), rotation[index], Time.time, turretOf, turretAngle, turretTurnedAt, aimUntil);

        private static void ResetTurretIfNew(int index, int id, float hull, float now, NativeArray<int> turretOf, NativeArray<float> turretAngle,
            NativeArray<float> turretTurnedAt, NativeArray<float> aimUntil)
        {
            if (turretOf[index] == id) return;
            turretOf[index] = id;
            turretAngle[index] = hull;
            turretTurnedAt[index] = now;
            aimUntil[index] = float.NegativeInfinity;
        }

        /// <summary>A known unit's health drop this frame, for the minimap's fights (applied on the main thread after the draw job).</summary>
        private struct UnitHit
        {
            public float2 Position;
            public int Drop;
            public float SinceLastHit;
            public bool Own;
        }

        /// <summary>
        /// Puts the colour of every owner among the known units into the cache the draw job reads, before it runs, so a new owner's
        /// units draw in its colour from the first frame. An owner with no colour yet (a player still arriving) stays out: the job
        /// draws it grey, as <see cref="ColorOf"/> does.
        /// </summary>
        private void CacheOwnerColours(NativeArray<int> known, NativeArray<int> owners)
        {
            missingOwners.Clear();
            new MissingOwnersJob { Known = known, Owners = owners, Cached = ownerColours, Missing = missingOwners }.Run();
            for (int i = 0; i < missingOwners.Length; i++)
            {
                int owner = missingOwners[i];
                uint c = ColorOf(owner);
                if (colorOfOwner.ContainsKey(owner)) ownerColours[owner] = c;
            }
        }

        /// <summary>Owners of known units that have no cached colour.</summary>
        [BurstCompile]
        private struct MissingOwnersJob : IJob
        {
            [ReadOnly] public NativeArray<int> Known, Owners;
            [ReadOnly] public NativeHashMap<int, uint> Cached;
            public NativeList<int> Missing;

            public void Execute()
            {
                int last = int.MinValue;
                for (int i = 0; i < Known.Length; i++)
                {
                    int owner = Owners[Known[i]];
                    if (owner == last) continue;
                    last = owner;
                    if (!Cached.ContainsKey(owner) && !Missing.Contains(owner)) Missing.Add(owner);
                }
            }
        }

        /// <summary>
        /// Per known unit, what the draw needs, as the main thread did it until 2026-11-06: its facing from how it moved, its body's atlas
        /// cell, colour, health, hit feedback (<see cref="HitFeedback"/>: a unit whose health dropped flashes and its bar keeps a
        /// draining trail; one new to its index starts afresh), track marks (<see cref="WorldEffects.TrackMark"/>), its drawn size, and
        /// its turret: turned toward what it fired at while it holds its aim, else the way it faces, by the time since it last turned (so
        /// one that was off screen catches up at once), packed with a raised Artillery barrel's shadow. The hits and marks go to lists the
        /// main thread applies after.
        /// </summary>
        [BurstCompile]
        private struct PrepareUnitsJob : IJob
        {
            [ReadOnly] public NativeArray<int> Known;
            [ReadOnly] public NativeArray<float2> Positions; // parallel to Known
            [ReadOnly] public NativeArray<int> Owners;
            [ReadOnly] public NativeArray<byte> Types, Health;
            [ReadOnly] public NativeArray<PredictState> States;
            [ReadOnly] public NativeArray<float> LastAction;
            [ReadOnly] public NativeArray<byte> Selected;
            [ReadOnly] public NativeArray<float> ScaleByType;
            [ReadOnly] public NativeArray<byte> Hidden;
            public bool UseHidden;
            [ReadOnly] public NativeHashMap<int, uint> Colours;
            public uint UnknownColour;
            [ReadOnly] public NativeArray<UnitFrames.TypeCells> Cells;
            public bool HasFrames;
            public float ServerTime, Now, FrameSeconds;
            public int LocalOwner;
            public float4 View, TurretView; // xMin, yMin, xMax, yMax
            public uint BarrelShadowFrame;

            // By index.
            public NativeArray<float2> LastPosition;
            public NativeArray<float> Rotation;
            public NativeArray<uint> Frame, Color;
            public NativeArray<float> Health01, DrawScale;
            public NativeArray<int> TrackedId;
            public NativeArray<byte> LastHealth, Flash;
            public NativeArray<float> HitAt, Trail;
            public NativeArray<int> TrackIds;
            public NativeArray<float2> LastTracks;
            public NativeArray<int> TurretOf;
            public NativeArray<float> TurretAngle, TurretTurnedAt, AimUntil;
            [ReadOnly] public NativeArray<float2> AimAt;

            public NativeList<UnitInstance> Turrets, BarrelShadows;
            public NativeList<UnitHit> Hits;
            public NativeList<float3> Tracks; // position, heading

            public void Execute()
            {
                for (int i = 0; i < Known.Length; i++)
                {
                    int index = Known[i];
                    float2 p = Positions[i];
                    float2 delta = p - LastPosition[index];
                    bool moving = math.lengthsq(delta) > 1e-6f && math.lengthsq(delta) < 4f;
                    if (moving) Rotation[index] = math.atan2(delta.y, delta.x);
                    LastPosition[index] = p;
                    byte type = Types[index];
                    int owner = Owners[index], id = States[index].Id;
                    int row = type < Cells.Length ? type : 0; // no bounds checks in a player's Burst code (DecodeEnter refuses bad types too)
                    if (HasFrames)
                        Frame[index] = Cells[row].BodyCell(Rotation[index], moving, ServerTime, ServerTime - LastAction[index], index);
                    Color[index] = Colours.TryGetValue(owner, out uint c) ? c : UnknownColour;
                    byte health = Health[index];
                    Health01[index] = health / 100f;
                    TrackHit(index, id, health, p, owner);
                    if (moving)
                    {
                        int tracked = TrackIds[index];
                        float2 last = LastTracks[index];
                        bool lay = WorldEffects.TrackMark(ref tracked, ref last, id, type, p, View, out float2 mid, out float heading);
                        TrackIds[index] = tracked;
                        LastTracks[index] = last;
                        if (lay) Tracks.Add(new float3(mid, heading));
                    }
                    DrawScale[index] = UseHidden && Hidden[index] != 0 ? -1f : type < ScaleByType.Length ? ScaleByType[type] : 1f;
                    if (HasFrames && Cells[row].TurretFirst >= 0 && DrawScale[index] > 0f && Inside(TurretView, p)) PackTurret(index, id, type, Cells[row], p);
                }
            }

            private static bool Inside(float4 r, float2 p) => p.x >= r.x && p.x < r.z && p.y >= r.y && p.y < r.w; // Rect.Contains

            private void TrackHit(int index, int id, byte health, float2 position, int owner)
            {
                if (TrackedId[index] != id)
                {
                    TrackedId[index] = id;
                    LastHealth[index] = health;
                    HitAt[index] = float.NegativeInfinity;
                    Trail[index] = health / 100f;
                }
                else if (health < LastHealth[index])
                {
                    Hits.Add(new UnitHit { Position = position, Own = owner == LocalOwner, Drop = LastHealth[index] - health, SinceLastHit = Now - HitAt[index] });
                    HitAt[index] = Now;
                }
                LastHealth[index] = health;
                Trail[index] = HitFeedback.Trail(Trail[index], health / 100f, Now - HitAt[index], FrameSeconds);
                Flash[index] = (byte)(HitFeedback.Flashing(HitAt[index], Now) ? 1 : 0);
            }

            private void PackTurret(int index, int id, byte type, UnitFrames.TypeCells cell, float2 p)
            {
                UnitType unit = (UnitType)type;
                ResetTurretIfNew(index, id, Rotation[index], Now, TurretOf, TurretAngle, TurretTurnedAt, AimUntil);
                bool aiming = Now < AimUntil[index];
                float hull = Rotation[index];
                float goal = TurretAim.Goal(hull, aiming ? math.atan2(AimAt[index].y - p.y, AimAt[index].x - p.x) : 0f, aiming);
                TurretAngle[index] = TurretAim.Step(TurretAngle[index], goal, TurretAim.TurnRate(unit) * (Now - TurretTurnedAt[index]));
                TurretTurnedAt[index] = Now;
                int facing = TurretAim.Facing(TurretAngle[index]);
                int frameIndex = TurretAim.TurretFrame(cell.TurretFrames, aiming, TurretAim.Recoiling(ServerTime - LastAction[index]));
                Vector2 pivot = TurretAim.PivotPixels(unit, TurretAim.HullAngle(hull)) / ArtStyle.PixelsPerUnit;
                float2 at = p + new float2(pivot.x, pivot.y);
                Turrets.Add(new UnitInstance
                {
                    position = at,
                    frame = cell.TurretCell(facing, frameIndex),
                    color = Color[index],
                    scale = cell.TurretTiles,
                    pad = (uint)(Selected[index] & 1) | (uint)Flash[index] << 1,
                });
                if (unit == UnitType.Artillery && aiming)
                    BarrelShadows.Add(new UnitInstance { position = at, frame = BarrelShadowFrame + (uint)facing, color = 0xFFFFFFFFu, scale = BarrelShadowTiles });
            }
        }

        /// <summary>How many tiles across a unit type is drawn (2 for the large units), from the art manifest.</summary>
        /// <summary>A known unit died: its scorch, and its wreck at its last facing (not a Bomber, which blew itself up).</summary>
        private void OnUnitDied(int index, float2 at)
        {
            byte type = store.TypeOf(index);
            var position = new Vector2(at.x, at.y);
            bool large = ScaleOf(type) > 1.5f;
            worldEffects.UnitDied(position, large);
            // v0.20 (spec §8.1): the death by its cause and size; a Bomber's own blast is its death.
            byte cause = store.LastDeathReason == LeaveReason.DiedBlast ? (byte)1 : store.LastDeathReason == LeaveReason.DiedFire ? (byte)2 : (byte)0;
            // Only on or near the screen, as the server's death explosions are (an off-screen battle would churn the pools).
            Camera cam = ViewCamera != null ? ViewCamera : Camera.main;
            bool shown = cam == null || WorldEffects.ViewOf(cam, 2f).Contains(position);
            if (type != (byte)UnitType.Bomber && shown) Effects.UnitDeath(position, large, cause);
            if (unitAtlas == null || type == (byte)UnitType.Bomber || (uint)index >= (uint)rotation.Length) return;
            string id = ((UnitType)type).ToString();
            int directions = unitAtlas.Directions(id);
            if (directions <= 0) return;
            Sprite wreck = unitAtlas.Sprite(id, UnitFrames.Direction(rotation[index], directions) * unitAtlas.Frames(id));
            // A blast flings the wreck up to a tile from where it stood; fire leaves it burnt black.
            if (cause == 1) position += new Vector2(Mathf.Cos(index * 2.39996f), Mathf.Sin(index * 2.39996f)) * 0.6f;
            if (cause == 2) Decals.BurntWreck(position, wreck);
            else Decals.Wreck(position, wreck);
            // v0.20 (spec §8.1): shot dead, a turreted unit's head is thrown off to land beside its wreck, at the facing it last had.
            string head = id + ArtManifest.TurretSuffix;
            int headDirections = cause == 0 && TurretAim.Has((UnitType)type) ? unitAtlas.Directions(head) : 0;
            if (headDirections > 0 && turretAngle.IsCreated && (uint)index < (uint)turretAngle.Length)
            {
                float a = turretAngle[index] + 2.4f, throwTiles = large ? 0.9f : 0.6f;
                Decals.Wreck(position + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * throwTiles,
                    unitAtlas.Sprite(head, TurretAim.Facing(turretAngle[index]) % headDirections * unitAtlas.Frames(head)));
            }
        }

        /// <summary>v0.20: a known unit's drawn position, type and size by id index, for <see cref="CombatVisuals"/>.</summary>
        private (bool known, float2 at, byte type, float scale) UnitAt(int index)
        {
            if (!store.IsKnown(index) || (uint)index >= (uint)lastPosition.Length) return (false, default, 0, 0f);
            byte type = store.TypeOf(index);
            return (true, lastPosition[index], type, ScaleOf(type));
        }

        private float ScaleOf(byte type)
        {
            NativeArray<float> scales = ScaleTable();
            return type < scales.Length ? scales[type] : 1f;
        }

        /// <summary>Tiles across each unit type is drawn, by type (made on first use).</summary>
        private NativeArray<float> ScaleTable()
        {
            if (!scaleByType.IsCreated)
            {
                scaleByType = new NativeArray<float>(System.Enum.GetValues(typeof(UnitType)).Length, Allocator.Persistent);
                for (int t = 0; t < scaleByType.Length; t++)
                    scaleByType[t] = ArtManifest.TryGet(((UnitType)t).ToString(), out ArtEntry entry) && entry.Kind == ArtKind.Unit ? entry.WidthTiles : 1f;
            }
            return scaleByType;
        }

        /// <summary>Buildings drawn last frame (tests).</summary>
        public int BuildingsDrawn => buildingRenderer?.LastDrawn ?? 0;

        /// <summary>The team colour (RGB) an owner's building was drawn in last frame (tests).</summary>
        public uint LastBuildingColour(int ownerId) => buildingRenderer?.LastColourOf(ownerId, ClientBuildings.Current) ?? 0;

        /// <summary>Draws every building the client knows (seen, ghosts, blueprints, known bombs) in one instanced call.</summary>
        /// <summary>A replay's per-player view (v0.18): the buildings to draw instead of everything known.</summary>
        public ClientBuildings BuildingsShown;

        /// <summary>A replay's per-player view (v0.18): whether to draw a unit (owner, position). Null draws them all.</summary>
        public Func<int, float2, bool> UnitShown;

        private void DrawBuildings()
        {
            ClientBuildings known = BuildingsShown ?? ClientBuildings.Current;
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
        internal bool TryTargetPosition(int target, out Vector3 to)
        {
            if (AttackTargetIds.IsWall(target)) // a wall tile, only in the shooter's owner's events (v0.16)
            {
                int tile = AttackTargetIds.WallTile(target), width = ClientTerrain.Current.Width;
                if (width <= 0) { to = default; return false; }
                to = new Vector3(tile % width + 0.5f, tile / width + 0.5f, 0);
                return true;
            }
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
                Effects.TurretShot(new Vector2(centre.x, centre.y), angle, line ? to : (Vector3?)null, building);
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
                UnitType type = (UnitType)store.TypeOf(attacker);
                // The Gameplay setting thins or hides the tracer lines; the flash, sound, shells and flame streams stay.
                bool line = Overlays.OverlayRules.KeepTracer(tracerMode, tracerSerial++);
                ArmourCheck(type, target, to);
                if (TurretAim.Has(type))
                {
                    FireGun(attacker, type, from, target, to, line);
                    if (store.OwnerOf(attacker) == WorldEffects.LocalOwner) combatVisuals.Fired(attacker);
                    continue;
                }
                // Face the target: the facing picks the drawn direction, and the fire frames show the barrel.
                if ((uint)attacker < (uint)rotation.Length && (to.x != from.x || to.y != from.y))
                    rotation[attacker] = math.atan2(to.y - from.y, to.x - from.x);
                if (type == UnitType.Flamethrower) FireFlame(attacker, target, to);
                else Effects.Tracer(new Vector3(from.x, from.y, 0), to, type, line);
            }
            attacks.Clear();
        }

        /// <summary>
        /// A turreted unit's shot: its turret aims at the target, for as long as the gun takes to fire again and a short hold after, and the
        /// flash and shot leave the barrel where it is drawn now. The hull keeps the way it drives.
        /// </summary>
        private void FireGun(int attacker, UnitType type, float2 from, int target, Vector3 to, bool line)
        {
            ResetTurretIfNew(attacker);
            UnitConfig gun = ConfigLoader.LoadConfig().GetUnit(type);
            aimAt[attacker] = new float2(to.x, to.y);
            aimUntil[attacker] = Time.time + gun.AttackInterval + TurretAim.HoldSeconds;
            float turret = TurretAim.FacingAngle(TurretAim.Facing(turretAngle[attacker]));
            // A flash stays on a gun that moves: it plays for a moment at the speed the unit drives.
            float2 ahead = store.PredictOne(attacker, serverTime + DriftSampleSeconds) - from;
            Vector3 drift = new Vector3(ahead.x, ahead.y, 0f) / DriftSampleSeconds;
            Effects.GunShot(type, new Vector2(from.x, from.y), TurretAim.HullAngle(rotation[attacker]), turret, to, TargetRadius(target), line, drift);
        }

        /// <summary>
        /// v0.20 (spec §8.2): a shot a known armoured unit's flat armour cuts to the least hit shows the armour spark ring, so small arms
        /// visibly bounce off. Worked out here from the config the client already has, from shots it was already told about.
        /// </summary>
        private void ArmourCheck(UnitType attacker, int target, Vector3 to)
        {
            int index = NetIdAllocator.IndexOf(target);
            if (target <= 0 || store.IdOf(index) != target) return;
            GameConfigData config = ConfigLoader.LoadConfig();
            UnitConfig victim = config.GetUnit((UnitType)store.TypeOf(index)), gun = config.GetUnit(attacker);
            if (victim.FlatArmour <= 0f || gun.FiresShells || gun.IsBomber) return;
            float raw = gun.Damage * config.Damage.Multiplier(attacker, victim.Armour);
            if (raw > 0f && raw - victim.FlatArmour <= config.Combat.MinHit) Effects.ArmourSpark(new Vector2(to.x, to.y));
        }

        /// <summary>Seconds ahead a gun's speed is read from its route.</summary>
        private const float DriftSampleSeconds = 0.1f;

        /// <summary>A known unit's radius, 0 for anything else: a shot stops at a unit's near side, and at a building's or wall's aim point.</summary>
        private float TargetRadius(int target)
        {
            int index = NetIdAllocator.IndexOf(target);
            return target > 0 && store.IdOf(index) == target ? ConfigLoader.LoadConfig().GetUnit((UnitType)store.TypeOf(index)).Radius : 0f;
        }

        /// <summary>A Flamethrower's shot: its flame burns until just after its next one is due, so a unit that keeps firing keeps one flame going.</summary>
        private void FireFlame(int attacker, int target, Vector3 to)
        {
            int id = store.IdOf(attacker);
            if (!TryFlameLine(id, target, out Vector3 nozzle, out Vector3 end)) return;
            UnitConfig flamer = ConfigLoader.LoadConfig().GetUnit(UnitType.Flamethrower);
            Effects.Flame(id, target, nozzle, end, flamer.Splash, flamer.AttackInterval + FlameOverlapSeconds);
        }

        /// <summary>Seconds a flame outlasts the time to its unit's next shot.</summary>
        private const float FlameOverlapSeconds = 0.15f;

        /// <summary>
        /// Where a unit's flame runs now, facing it at the target: from its nozzle, at the drawn facing nearest the target, to the target or
        /// as far as its range reaches. False once the unit or its target has gone.
        /// </summary>
        private bool TryFlameLine(int attacker, int target, out Vector3 nozzle, out Vector3 end)
        {
            nozzle = end = default;
            int index = NetIdAllocator.IndexOf(attacker);
            if (store.IdOf(index) != attacker || !TryTargetPosition(target, out Vector3 to)) return false;
            float2 p = lastPosition[index];
            rotation[index] = math.atan2(to.y - p.y, to.x - p.x);
            float facing = TurretAim.HullAngle(rotation[index]);
            nozzle = new Vector3(p.x + math.cos(facing) * Effects.BarrelReach, p.y + math.sin(facing) * Effects.BarrelReach, 0f);
            end = Effects.FlameEnd(new Vector2(p.x, p.y), to, ConfigLoader.LoadConfig().GetUnit(UnitType.Flamethrower).Range);
            return true;
        }
    }
}
