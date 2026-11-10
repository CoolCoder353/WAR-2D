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
        private NativeArray<float2> lastPosition, lastSteer;
        private NativeArray<float> rotation;
        private NativeArray<uint> frame;
        private NativeArray<float> lastAction; // server time of the unit's last attack, by index
        // Turrets (TurretAim), by index: whose it is (an index is reused with a new id), its angle and when it last turned, and what it
        // aims at until when. Only ever touched on the main thread and by the draw job (on the job threads, one unit's index each, and
        // complete before Draw goes on), for units on screen or firing.
        private NativeArray<int> turretOf;
        private NativeArray<float> turretAngle, turretTurnedAt, aimUntil;
        private NativeArray<float2> aimAt;
        private NativeList<UnitInstance> turrets;
        private NativeList<int> filtered; // FilterJob's ids
        // PackUnitsJob's per-unit slots (parallel to the known list), gathered into the bar and ring lists in unit order.
        private NativeList<byte> packEmits;
        private NativeList<UnitInstance> barSlots, ringSlots;
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
        private NativeArray<byte> hidden;
        private NativeList<UnitHit> hits;
        private NativeList<float3> tracks;
        // The draw job's per-unit slots (parallel to the known list), gathered into the four lists above in unit order.
        private NativeList<byte> drawEmits;
        private NativeList<UnitHit> hitSlots;
        private NativeList<float3> trackSlots;
        private NativeList<UnitInstance> turretSlots, shadowSlots;

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
        private NativeArray<int> barCount, ringCount, shownCount;
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

        /// <summary>
        /// Set by a view made on purpose (the menu battle, the replay viewer) just before it adds its ClientWorld: that one
        /// becomes <see cref="Instance"/> even while an older one lingers (playtest 2026-11-10: after leaving a match the
        /// menu battle's view found a stale Instance, destroyed itself, and was drawn every frame with no store).
        /// </summary>
        internal static bool TakeOverNext;

        private void Awake()
        {
            bool takeOver = TakeOverNext;
            TakeOverNext = false;
            if (Instance != null && Instance != this && !takeOver) { Destroy(this); return; }
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
            if (store != null) { Instance = this; return; }
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
            combatVisuals = new CombatVisuals(dt, config) { Shown = p => UnitShown == null || UnitShown(0, p) };
            hideBar = new NativeArray<byte>(capacity, Allocator.Persistent);
            store.Forgotten += index => { ClientCombat.Current.Forget(index); combatVisuals.Forget(index); };
            Effects.LandingShown = p => UnitShown == null ? ClientFog.Current.State == null || ClientFog.Current.Sees(p.x, p.y) : UnitShown(0, new float2(p.x, p.y));
            store.Appeared += (index, at) => { if (Shown(index, at)) worldEffects.UnitAppeared(new Vector2(at.x, at.y), store.OwnerOf(index), Time.time - firstBatchAt); };
            lastPosition = new NativeArray<float2>(capacity, Allocator.Persistent);
            lastSteer = new NativeArray<float2>(capacity, Allocator.Persistent);
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
            hidden = new NativeArray<byte>(capacity, Allocator.Persistent);
            hits = new NativeList<UnitHit>(256, Allocator.Persistent);
            tracks = new NativeList<float3>(256, Allocator.Persistent);
            turrets = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            filtered = new NativeList<int>(1024, Allocator.Persistent);
            packEmits = new NativeList<byte>(4096, Allocator.Persistent);
            barSlots = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            ringSlots = new NativeList<UnitInstance>(8192, Allocator.Persistent);
            barrelShadows = new NativeList<UnitInstance>(64, Allocator.Persistent);
            drawEmits = new NativeList<byte>(4096, Allocator.Persistent);
            hitSlots = new NativeList<UnitHit>(4096, Allocator.Persistent);
            trackSlots = new NativeList<float3>(4096, Allocator.Persistent);
            turretSlots = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            shadowSlots = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            flameLine = TryFlameLine;
            instances = new NativeList<UnitInstance>(4096, Allocator.Persistent);
            bars = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            barCount = new NativeArray<int>(1, Allocator.Persistent);
            shownCount = new NativeArray<int>(1, Allocator.Persistent);
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
            if (filtered.IsCreated) { filtered.Dispose(); packEmits.Dispose(); barSlots.Dispose(); ringSlots.Dispose(); }
            if (drawEmits.IsCreated) { drawEmits.Dispose(); hitSlots.Dispose(); trackSlots.Dispose(); turretSlots.Dispose(); shadowSlots.Dispose(); }
            if (turretOf.IsCreated)
            {
                turretOf.Dispose(); turretAngle.Dispose(); turretTurnedAt.Dispose(); aimUntil.Dispose(); aimAt.Dispose();
                cells.Dispose(); ownerColours.Dispose(); hidden.Dispose(); hits.Dispose(); tracks.Dispose();
            }
            if (scaleByType.IsCreated) scaleByType.Dispose();
            if (lastPosition.IsCreated)
            {
                lastPosition.Dispose(); lastSteer.Dispose(); rotation.Dispose(); frame.Dispose(); lastAction.Dispose(); color.Dispose(); health01.Dispose(); drawScale.Dispose(); selected.Dispose();
                trackedId.Dispose(); lastHealth.Dispose(); hitAt.Dispose(); trail.Dispose(); flash.Dispose();
                instances.Dispose(); bars.Dispose(); barCount.Dispose(); rings.Dispose(); ringCount.Dispose(); shownCount.Dispose();
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
            // Units away from the view (past the turrets' margin and OffViewMargin more) are predicted in turns, each at least every
            // OffViewRefreshSeconds, too seldom to cross that margin unseen; a replay's per-player view tests every unit's position each
            // frame, so it predicts them all.
            // The units are predicted and packed at most every UnitPackSeconds, or when the known count changed; the frames between draw
            // the last packing carried along each instance's velocity, toward where the next packing will have it (2026-11-08: a
            // 100,000-unit client on three cores spent more on its per-frame unit work than on anything else).
            FireTracers();
            FireTurrets();
            float real = Time.time;
            unitsDue = real - unitsPackedAt >= UnitPackSeconds || real < unitsPackedAt || store.Count != unitsPackedCount || !unitsPacked;
            if (unitsDue) Predict(real);
        }

        /// <summary>Starts the units' prediction job for the next packing (<see cref="Draw"/>).</summary>
        private void Predict(float real)
        {
            unitsPackedAt = real;
            unitsPackedCount = store.Count;
            Camera viewCamera = ViewCamera != null ? ViewCamera : Camera.main;
            if (viewCamera == null || UnitShown != null) store.PredictAll(serverTime);
            else
            {
                Rect cull = WorldEffects.ViewOf(viewCamera, TurretViewMargin + OffViewMargin);
                store.PredictAll(serverTime, new float4(cull.xMin, cull.yMin, cull.xMax, cull.yMax), OffViewRefreshSeconds, UnitPackSeconds);
            }
        }

        // Whether this frame's Update predicted the units (Draw packs them then), when and how many, and whether a packing was drawn.
        private bool unitsDue, unitsPacked;
        private float unitsPackedAt = float.NegativeInfinity;
        private int unitsPackedCount = -1;

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
        public void CollectOwn(int ownerId, List<int> into, UnitType type = UnitType.None) =>
            Filter(into, ownerId, false, type, float.MinValue, float.MaxValue, 0f, -1f, false);

        /// <summary>Adds the ids of every known unit within <paramref name="radius"/> of a point that is not <paramref name="excludeOwner"/>'s (the enemy brackets of a range ring).</summary>
        public void CollectInRadius(float2 centre, float radius, int excludeOwner, List<int> into) =>
            Filter(into, excludeOwner, true, UnitType.None, float.MinValue, float.MaxValue, centre, radius * radius, true);

        /// <summary>Adds the ids of the owner's units inside the box (inclusive) to <paramref name="into"/>.</summary>
        public void QueryBox(float2 min, float2 max, int ownerId, List<int> into) =>
            Filter(into, ownerId, false, UnitType.None, math.min(min, max), math.max(min, max), 0f, -1f, true);

        /// <summary>How many units of each type (indexed by <see cref="UnitType"/>) the client knows of the owner.</summary>
        public void CountOwn(int ownerId, int[] byType)
        {
            using var counts = new NativeArray<int>(byType.Length, Allocator.TempJob);
            new CountOwnJob { Known = store.Known, Owners = store.OwnerArray, Types = store.TypeArray, Owner = ownerId, Counts = counts }.Run();
            counts.CopyTo(byType);
        }

        [BurstCompile]
        private struct CountOwnJob : IJob
        {
            [ReadOnly] public NativeArray<int> Known, Owners;
            [ReadOnly] public NativeArray<byte> Types;
            public int Owner;
            public NativeArray<int> Counts;

            public void Execute()
            {
                for (int i = 0; i < Known.Length; i++)
                {
                    int index = Known[i];
                    if (Owners[index] == Owner && Types[index] < Counts.Length) Counts[Types[index]]++;
                }
            }
        }

        private void Filter(List<int> into, int owner, bool excludeOwner, UnitType type, float2 lo, float2 hi, float2 centre, float radiusSq, bool usePositions)
        {
            filtered.Clear();
            new FilterJob
            {
                Known = store.Known, Owners = store.OwnerArray, Types = store.TypeArray, States = store.StateArray, Positions = store.Predicted,
                Owner = owner, ExcludeOwner = excludeOwner, Type = (byte)type, Lo = lo, Hi = hi, Centre = centre, RadiusSq = radiusSq,
                UsePositions = usePositions, Ids = filtered,
            }.Run();
            for (int k = 0; k < filtered.Length; k++) into.Add(filtered[k]);
        }

        /// <summary>
        /// The ids of the known units of an owner (or, with <see cref="ExcludeOwner"/>, of every other owner), of one type (0: any),
        /// inside a box and a circle (<see cref="RadiusSq"/> &lt; 0: none), in known order. A Burst job: the managed loops walked every
        /// known unit, 4 to 10 ms a call at 100,000 units (the quick-select counts run 4 times a second).
        /// </summary>
        [BurstCompile]
        private struct FilterJob : IJob
        {
            [ReadOnly] public NativeArray<int> Known, Owners;
            [ReadOnly] public NativeArray<byte> Types;
            [ReadOnly] public NativeArray<PredictState> States;
            [ReadOnly] public NativeArray<float2> Positions; // parallel to Known
            public int Owner;
            public bool ExcludeOwner, UsePositions;
            public byte Type;
            public float2 Lo, Hi, Centre;
            public float RadiusSq;
            public NativeList<int> Ids;

            public void Execute()
            {
                int n = UsePositions ? math.min(Known.Length, Positions.Length) : Known.Length;
                for (int i = 0; i < n; i++)
                {
                    int index = Known[i];
                    if ((Owners[index] == Owner) == ExcludeOwner) continue;
                    if (Type != 0 && Types[index] != Type) continue;
                    if (UsePositions)
                    {
                        float2 p = Positions[i];
                        if (p.x < Lo.x || p.y < Lo.y || p.x > Hi.x || p.y > Hi.y) continue;
                        if (RadiusSq >= 0f && math.distancesq(p, Centre) > RadiusSq) continue;
                    }
                    Ids.Add(States[index].Id);
                }
            }
        }

        /// <summary>Plots every known unit on the minimap in its owner's colour (the minimap drops units on cells not seen now).</summary>
        public void PlotOn(MinimapTexture minimap)
        {
            CacheOwnerColours();
            minimap.PlotUnits(store.Known, store.Predicted, store.OwnerArray, ownerColours, UnknownColour);
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
            if (store == null) return; // a duplicate that destroyed itself in Awake
            // Between packs (see Update): the last packing again, carried along each instance's velocity.
            if (!unitsDue && unitsPacked && unitRenderer != null)
            {
                Shader.SetGlobalFloat(InstancedUnitRenderer.DrawAheadProperty, math.clamp(Time.time - unitsPackedAt, 0f, 2f * UnitPackSeconds));
                using (BuildingsMarker.Auto()) DrawBuildings();
                using (SubmitMarker.Auto()) unitRenderer.Redraw();
                return;
            }
            unitsDue = false;
            Shader.SetGlobalFloat(InstancedUnitRenderer.DrawAheadProperty, 0f);
            SelectMarker.Begin();
            int count = store.Count;
            if (count == 0 || store.DrawnAfterPrediction.Length != count)
            {
                unitsPacked = false;
                SelectMarker.End();
                using (BuildingsMarker.Auto()) DrawBuildings();
                return;
            }
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
            // Smoothed (corrections glide instead of stepping); the prediction job may still be filling them: the draw job waits for it.
            NativeArray<float2> positions = store.DrawnAfterPrediction;
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
            using (ColoursMarker.Auto()) CacheOwnerColours();
            if (UnitShown != null) store.CompletePrediction(); // a replay's per-player view reads the positions here
            if (UnitShown != null) // a replay's per-player view: the delegate can't run in the job
                for (int i = 0; i < count; i++) hidden[known[i]] = (byte)(UnitShown(owners[known[i]], positions[i]) ? 0 : 1);
            // v0.20 (spec §8.3): a Sniper's head turns to what it aims at and holds there while it aims (the job turns it).
            foreach (KeyValuePair<int, byte> status in ClientCombat.Current.Statuses)
                if ((uint)status.Key < (uint)aimAt.Length && (status.Value & WAR2D.Sim.UnitStatusBits.Aiming) != 0 // the index is the server's: bound it
                    && combatVisuals.AimTarget(status.Key, out float2 aimed))
                {
                    aimAt[status.Key] = aimed;
                    aimUntil[status.Key] = now + TurretAim.HoldSeconds;
                }
            SelectMarker.End();
            PrepareMarker.Begin();
            // Per unit: facing, frame, colour, hit feedback, track marks, size and turret (Burst; 2026-11-06, 4.5 of 8 ms a frame
            // at 10,000 units as a managed loop), over the job threads, then gathered in unit order.
            int n = known.Length;
            drawEmits.Resize(n, NativeArrayOptions.ClearMemory); // cleared: a batch that throws (editor) leaves no stale bits for the gather
            hitSlots.ResizeUninitialized(n);
            trackSlots.ResizeUninitialized(n);
            turretSlots.ResizeUninitialized(n);
            shadowSlots.ResizeUninitialized(n);
            JobHandle prepared = new PrepareUnitsJob
            {
                Known = known,
                Positions = positions,
                Refreshed = store.RefreshedAfterPrediction,
                Velocity = store.VelocityAfterPrediction,
                Steer = store.SteerAfterPrediction,
                MaxTurn = UnitTurnRate * frameSeconds,
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
                LastSteer = lastSteer,
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
                Emits = drawEmits.AsArray(),
                HitSlots = hitSlots.AsArray(),
                TrackSlots = trackSlots.AsArray(),
                TurretSlots = turretSlots.AsArray(),
                ShadowSlots = shadowSlots.AsArray(),
            }.Schedule(n, DrawBatch, store.Prediction);
            // The gather is chained too, then the buildings draw on the main thread while the jobs run.
            JobHandle gathered = new GatherUnitsJob
            {
                Emits = drawEmits.AsArray(),
                HitSlots = hitSlots.AsArray(),
                TrackSlots = trackSlots.AsArray(),
                TurretSlots = turretSlots.AsArray(),
                ShadowSlots = shadowSlots.AsArray(),
                Hits = hits,
                Tracks = tracks,
                Turrets = turrets,
                BarrelShadows = barrelShadows,
            }.Schedule(prepared);
            JobHandle.ScheduleBatchedJobs();
            PrepareMarker.End();
            using (BuildingsMarker.Auto()) DrawBuildings();
            using (PrepareMarker.Auto())
            {
                gathered.Complete();
                store.CompletePrediction();
            }
            EffectsMarker.Begin();
            // The job's main-thread effects, in unit order: hits on the minimap, then track marks.
            HitsMarker.Begin();
            MinimapCombat combat = MinimapCombat.Current;
            for (int i = 0; i < hits.Length && combat != null; i++)
            {
                UnitHit hit = hits[i];
                // v0.16: fights on the minimap, but not an own unit's upkeep decay (code review 2026-11-03).
                if (!(hit.Own && combat.IsOwnDecay(hit.Drop, hit.SinceLastHit))) combat.Hit(hit.Position, hit.Own, now);
            }
            HitsMarker.End();
            using (TracksMarker.Auto())
                for (int i = 0; i < tracks.Length; i++) Decals.Track(tracks[i].xy, tracks[i].z);
            RaiseAirborne(positions);
            Effects.FollowFlames(flameLine);
            // Fires, lasers, leaps and reload bars are sprites that hold between steps: placed with the units, at most every Effects.StepSeconds (2026-11-08:
            // walking every unit status each frame cost a 100,000-unit client on three cores 0.5 ms a frame).
            if (now - combatDrawnAt >= Effects.StepSeconds || now < combatDrawnAt)
            {
                combatDrawnAt = now;
                ownSelected.Clear();
                foreach (int index in selectedIndices)
                    if ((selected[index] & 1) != 0 && store.IsKnown(index) && store.OwnerOf(index) == WorldEffects.LocalOwner
                        && ((UnitType)store.TypeOf(index) == UnitType.Artillery || (UnitType)store.TypeOf(index) == UnitType.Sniper)) ownSelected.Add(index); // the reload bars: a selected army of 12,500 cost a call each
                using (CombatMarker.Auto()) combatVisuals.Draw(now, serverTime, view, UnitAt, ownSelected, BuildingsShown ?? ClientBuildings.Current);
            }
            EffectsMarker.End();
            PackMarker.Begin();
            instances.ResizeUninitialized(count);
            bars.ResizeUninitialized(count);
            rings.ResizeUninitialized(count * 2 + barrelShadows.Length); // a selected roaming unit has both rings
            packEmits.ResizeUninitialized(count);
            barSlots.ResizeUninitialized(count);
            ringSlots.ResizeUninitialized(count * 2);
            new PackUnitsJob
            {
                Known = known,
                Positions = positions,
                Refreshed = store.RefreshedAfterPrediction,
                Velocity = store.VelocityAfterPrediction,
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
                View = new float4(turretView.xMin, turretView.yMin, turretView.xMax, turretView.yMax),
                RingFrame = unitRenderer.RingFrame,
                RoamRingFrame = unitRenderer.RoamRingFrame,
                Instances = instances.AsArray(),
                BarSlots = barSlots.AsArray(),
                RingSlots = ringSlots.AsArray(),
                Emits = packEmits.AsArray(),
            }.Schedule(count, DrawBatch).Complete();
            new GatherPackedJob
            {
                Emits = packEmits.AsArray(), BarSlots = barSlots.AsArray(), RingSlots = ringSlots.AsArray(), Instances = instances.AsArray(), ShownCount = shownCount,
                Bars = bars.AsArray(), BarCount = barCount, Rings = rings.AsArray(), RingCount = ringCount,
            }.Run();
            for (int i = 0; i < barrelShadows.Length; i++) rings[ringCount[0] + i] = barrelShadows[i];
            PackMarker.End();
            using var submitting = SubmitMarker.Auto();
            unitRenderer.DrawRings(rings.AsArray(), ringCount[0] + barrelShadows.Length);
            unitRenderer.Draw(instances.AsArray(), shownCount[0]);
            unitRenderer.DrawTurrets(turrets.AsArray(), turrets.Length);
            unitRenderer.DrawHealthBars(bars.AsArray(), barCount[0]);
            unitsPacked = true;
        }

        // Profiler markers (nearly free in a release player): where a frame's draw time goes.
        private static readonly Unity.Profiling.ProfilerMarker BuildingsMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Buildings"),
            SelectMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Select"), PrepareMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Prepare"),
            EffectsMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Effects"), PackMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Pack"),
            SubmitMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Submit"), HitsMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Hits"),
            TracksMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Tracks"), CombatMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Combat"),
            ColoursMarker = new Unity.Profiling.ProfilerMarker("ClientWorld.Draw.Colours");

        private readonly List<int> barsHidden = new List<int>();

        /// <summary>
        /// v0.20: a Bomber in the air is drawn raised on its leap's arc with no health bar (Figma B1); its tracks, facing and fires stay on
        /// the ground track below (the draw job has already used that). Walks only the units with the Airborne status (it walked every
        /// known unit while any leap was on: 100,000 dictionary lookups a frame in a big battle). The raise is <see cref="CombatVisuals.LeapHeight"/>
        /// (1.25 tiles) up, far less than <see cref="OffViewMargin"/>, so a raised unit in view was already in view on the ground track
        /// the prepare job tested: its frame and colour are never stale. Keep LeapHeight under the margin.
        /// </summary>
        private void RaiseAirborne(NativeArray<float2> positions)
        {
            foreach (int index in barsHidden) hideBar[index] = 0;
            barsHidden.Clear();
            foreach (KeyValuePair<int, byte> status in ClientCombat.Current.Statuses)
            {
                if ((status.Value & WAR2D.Sim.UnitStatusBits.Airborne) == 0) continue;
                int slot = store.SlotOf(status.Key);
                if (slot < 0 || slot >= positions.Length || !combatVisuals.Airborne(status.Key, out float2 raised, out _, out _)) continue;
                positions[slot] = raised;
                hideBar[status.Key] = 1;
                barsHidden.Add(status.Key);
            }
        }

        /// <summary>How fast a unit's drawn facing turns (radians a second): half a turn in a quarter second.</summary>
        private const float UnitTurnRate = 4f * math.PI;

        /// <summary>The longest a unit away from the view waits for its prediction and draw state to be refreshed.</summary>
        private const float OffViewRefreshSeconds = 0.5f;

        /// <summary>Tiles past the turrets' margin a unit still counts as in view: more than the fastest unit or leap goes in <see cref="OffViewRefreshSeconds"/>.</summary>
        private const float OffViewMargin = 6f;

        /// <summary>The longest the drawn units wait for a fresh prediction, facing, frame and colour (the frames between carry them on).</summary>
        private const float UnitPackSeconds = 1f / 60f;

        private float combatDrawnAt = float.NegativeInfinity;

        /// <summary>Units per batch of the draw jobs on the job threads.</summary>
        private const int DrawBatch = 64;

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
        internal struct UnitHit
        {
            public float2 Position;
            public int Drop;
            public float SinceLastHit;
            public bool Own;
        }

        /// <summary>
        /// Puts the colour of every owner the client has seen units of into the cache the draw job reads, before it runs, so a new
        /// owner's units draw in its colour from the first frame. An owner with no colour yet (a player still arriving) stays out: the
        /// job draws it grey, as <see cref="ColorOf"/> does. Walks the store's few owners, not its units (0.5 ms a frame at 100,000).
        /// </summary>
        private void CacheOwnerColours()
        {
            foreach (int owner in store.Owners)
            {
                if (ownerColours.ContainsKey(owner)) continue;
                uint c = ColorOf(owner);
                if (colorOfOwner.ContainsKey(owner)) ownerColours[owner] = c;
            }
        }

        /// <summary>
        /// Per known unit, what the draw needs, as the main thread did it until 2026-11-06: its facing from how it moved, its body's atlas
        /// cell, colour, health, hit feedback (<see cref="HitFeedback"/>: a unit whose health dropped flashes and its bar keeps a
        /// draining trail; one new to its index starts afresh), track marks (<see cref="WorldEffects.TrackMark"/>), its drawn size, and
        /// its turret: turned toward what it fired at while it holds its aim, else the way it faces, by the time since it last turned (so
        /// one that was off screen catches up at once), packed with a raised Artillery barrel's shadow. The hits and marks go to lists the
        /// main thread applies after.
        /// Runs over the job threads (2026-11-07: 1.0 ms of the main thread at 10,000 units on one core): each unit writes only its own
        /// index (the known list holds each index once) and its own slot <c>i</c>; <see cref="GatherUnitsJob"/> then appends the slots to
        /// the lists in unit order, so the output is the same as one thread's.
        /// The editor compiles it before its first run (CompileSynchronously), so it never runs once in Mono and once in Burst.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct PrepareUnitsJob : IJobParallelFor
        {
            /// <summary>Bits of <see cref="Emits"/>: which of a unit's slots hold a result this frame.</summary>
            internal const byte EmitHit = 1, EmitTrack = 2, EmitTurret = 4, EmitShadow = 8;

            [ReadOnly] public NativeArray<int> Known;
            [ReadOnly] public NativeArray<float2> Positions; // parallel to Known
            /// <summary>Parallel to Known: 0 for a unit away from the view that the prediction left for a later turn (it keeps its state).</summary>
            [ReadOnly] public NativeArray<byte> Refreshed;
            /// <summary>Parallel to Known: how fast the unit's drawn position moves (its turret and barrel shadow move with it between packs).</summary>
            [ReadOnly] public NativeArray<float2> Velocity;
            /// <summary>Parallel to Known: where each unit is going without the correction glide (its facing follows this).</summary>
            [ReadOnly] public NativeArray<float2> Steer;
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
            /// <summary>The most a unit's facing turns this frame (radians).</summary>
            public float MaxTurn;
            public int LocalOwner;
            public float4 View, TurretView; // xMin, yMin, xMax, yMax
            public uint BarrelShadowFrame;

            // By index (each unit writes only its own).
            [NativeDisableParallelForRestriction] public NativeArray<float2> LastPosition, LastSteer;
            [NativeDisableParallelForRestriction] public NativeArray<float> Rotation;
            [NativeDisableParallelForRestriction] public NativeArray<uint> Frame, Color;
            [NativeDisableParallelForRestriction] public NativeArray<float> Health01, DrawScale;
            [NativeDisableParallelForRestriction] public NativeArray<int> TrackedId;
            [NativeDisableParallelForRestriction] public NativeArray<byte> LastHealth, Flash;
            [NativeDisableParallelForRestriction] public NativeArray<float> HitAt, Trail;
            [NativeDisableParallelForRestriction] public NativeArray<int> TrackIds;
            [NativeDisableParallelForRestriction] public NativeArray<float2> LastTracks;
            [NativeDisableParallelForRestriction] public NativeArray<int> TurretOf;
            [NativeDisableParallelForRestriction] public NativeArray<float> TurretAngle, TurretTurnedAt, AimUntil;
            [ReadOnly] public NativeArray<float2> AimAt;

            // By slot i, parallel to Known: what goes to the lists, read by GatherUnitsJob where Emits says so.
            public NativeArray<byte> Emits;
            public NativeArray<UnitHit> HitSlots;
            public NativeArray<float3> TrackSlots; // position, heading
            public NativeArray<UnitInstance> TurretSlots, ShadowSlots;

            public void Execute(int i)
            {
                if (Refreshed[i] == 0)
                {
                    Emits[i] = 0;
                    return;
                }
                int index = Known[i];
                float2 p = Positions[i];
                int owner = Owners[index], id = States[index].Id;
                // Facing follows the way the unit goes (its route, or its shown order), not the drawn glide that takes a correction's
                // jump out, and turns at most MaxTurn a frame: a glide back, or a one-frame jump, flipped units round (2026-11-07).
                float2 delta = Steer[i] - LastSteer[index];
                bool moving = math.lengthsq(delta) > 1e-6f && math.lengthsq(delta) < 4f;
                bool inView = Inside(TurretView, p);
                if (moving)
                {
                    float heading = math.atan2(delta.y, delta.x);
                    // Off screen (outside the view and its margin) a unit just faces its way: nobody sees it turn, and two of the three
                    // trigonometric calls a frame for each of the 100,000 units go (2026-11-08, a client on three cores).
                    if (!inView) Rotation[index] = heading;
                    else
                    {
                        float turned = TrackedId[index] != id ? heading : TurretAim.Step(Rotation[index], heading, MaxTurn); // a new unit faces its way at once
                        Rotation[index] = math.atan2(math.sin(turned), math.cos(turned));
                    }
                }
                LastSteer[index] = Steer[i];
                LastPosition[index] = p;
                byte type = Types[index];
                int row = type < Cells.Length ? type : 0; // no bounds checks in a player's Burst code (DecodeEnter refuses bad types too)
                byte health = Health[index];
                byte emit = 0;
                if (TrackHit(index, id, health, p, owner, out UnitHit hit))
                {
                    HitSlots[i] = hit;
                    emit |= EmitHit;
                }
                DrawScale[index] = UseHidden && Hidden[index] != 0 ? -1f : type < ScaleByType.Length ? ScaleByType[type] : 1f;
                if (!inView)
                {
                    // Off screen: PackUnitsJob packs nothing outside this view, so its frame, colour and bar wait until it is in it
                    // (2026-11-07: a frame pick and a colour lookup for each of 100,000 known units a frame, for about 13,000 drawn).
                    Emits[i] = emit;
                    return;
                }
                if (HasFrames)
                    Frame[index] = Cells[row].BodyCell(Rotation[index], moving, ServerTime, ServerTime - LastAction[index], index);
                Color[index] = Colours.TryGetValue(owner, out uint c) ? c : UnknownColour;
                Health01[index] = health / 100f;
                if (moving)
                {
                    int tracked = TrackIds[index];
                    float2 last = LastTracks[index];
                    bool lay = WorldEffects.TrackMark(ref tracked, ref last, id, type, p, View, out float2 mid, out float heading);
                    TrackIds[index] = tracked;
                    LastTracks[index] = last;
                    if (lay)
                    {
                        TrackSlots[i] = new float3(mid, heading);
                        emit |= EmitTrack;
                    }
                }
                if (HasFrames && Cells[row].TurretFirst >= 0 && DrawScale[index] > 0f) emit |= PackTurret(i, index, id, type, Cells[row], p);
                Emits[i] = emit;
            }

            private static bool Inside(float4 r, float2 p) => p.x >= r.x && p.x < r.z && p.y >= r.y && p.y < r.w; // Rect.Contains

            /// <summary>Hit feedback for one unit; true, with the hit for the minimap, when its health dropped.</summary>
            private bool TrackHit(int index, int id, byte health, float2 position, int owner, out UnitHit hit)
            {
                hit = default;
                bool dropped = false;
                if (TrackedId[index] != id)
                {
                    TrackedId[index] = id;
                    LastHealth[index] = health;
                    HitAt[index] = float.NegativeInfinity;
                    Trail[index] = health / 100f;
                }
                else if (health < LastHealth[index])
                {
                    hit = new UnitHit { Position = position, Own = owner == LocalOwner, Drop = LastHealth[index] - health, SinceLastHit = Now - HitAt[index] };
                    dropped = true;
                    HitAt[index] = Now;
                }
                LastHealth[index] = health;
                Trail[index] = HitFeedback.Trail(Trail[index], health / 100f, Now - HitAt[index], FrameSeconds);
                Flash[index] = (byte)(HitFeedback.Flashing(HitAt[index], Now) ? 1 : 0);
                return dropped;
            }

            /// <summary>The unit's turret into its slot, and a raised Artillery barrel's shadow into its own; returns their <see cref="Emits"/> bits.</summary>
            private byte PackTurret(int i, int index, int id, byte type, UnitFrames.TypeCells cell, float2 p)
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
                TurretSlots[i] = new UnitInstance
                {
                    position = at,
                    frame = cell.TurretCell(facing, frameIndex),
                    color = Color[index],
                    scale = cell.TurretTiles,
                    pad = (uint)(Selected[index] & 1) | (uint)Flash[index] << 1,
                    velocity = Velocity[i],
                };
                if (!(unit == UnitType.Artillery && aiming)) return EmitTurret;
                ShadowSlots[i] = new UnitInstance { position = at, frame = BarrelShadowFrame + (uint)facing, color = 0xFFFFFFFFu, scale = BarrelShadowTiles, velocity = Velocity[i] };
                return EmitTurret | EmitShadow;
            }
        }

        /// <summary>Appends <see cref="PrepareUnitsJob"/>'s slots to the hit, track, turret and barrel-shadow lists, in unit order.</summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct GatherUnitsJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Emits;
            [ReadOnly] public NativeArray<UnitHit> HitSlots;
            [ReadOnly] public NativeArray<float3> TrackSlots;
            [ReadOnly] public NativeArray<UnitInstance> TurretSlots, ShadowSlots;
            public NativeList<UnitHit> Hits;
            public NativeList<float3> Tracks;
            public NativeList<UnitInstance> Turrets, BarrelShadows;

            public void Execute()
            {
                for (int i = 0; i < Emits.Length; i++)
                {
                    byte emit = Emits[i];
                    if (emit == 0) continue;
                    if ((emit & PrepareUnitsJob.EmitHit) != 0) Hits.Add(HitSlots[i]);
                    if ((emit & PrepareUnitsJob.EmitTrack) != 0) Tracks.Add(TrackSlots[i]);
                    if ((emit & PrepareUnitsJob.EmitTurret) != 0) Turrets.Add(TurretSlots[i]);
                    if ((emit & PrepareUnitsJob.EmitShadow) != 0) BarrelShadows.Add(ShadowSlots[i]);
                }
            }
        }

        /// <summary>How many tiles across a unit type is drawn (2 for the large units), from the art manifest.</summary>
        /// <summary>Each unit type's atlas id and its turret head's, by type byte, made on first use (each death made both strings).</summary>
        private static readonly string[] AtlasIds = new string[256], HeadIds = new string[256];
        private int deathViewFrame = -1;
        private bool deathViewAll;
        private Rect deathView;

        /// <summary>A known unit died: its scorch, and its wreck at its last facing (not a Bomber, which blew itself up).</summary>
        private void OnUnitDied(int index, float2 at)
        {
            if (!Shown(index, at)) return; // a replay's per-player view: a death that player didn't see leaves nothing
            byte type = store.TypeOf(index);
            var position = new Vector2(at.x, at.y);
            bool large = ScaleOf(type) > 1.5f;
            worldEffects.UnitDied(position, large);
            // v0.20 (spec §8.1): the death by its cause and size; a Bomber's own blast is its death.
            byte cause = store.LastDeathReason == LeaveReason.DiedBlast ? (byte)1 : store.LastDeathReason == LeaveReason.DiedFire ? (byte)2 : (byte)0;
            // Only on or near the screen, as the server's death explosions are (an off-screen battle would churn the pools). The view
            // is read once a frame: one batch can hold hundreds of deaths at 100,000 units.
            if (deathViewFrame != Time.frameCount)
            {
                deathViewFrame = Time.frameCount;
                Camera cam = ViewCamera != null ? ViewCamera : Camera.main;
                deathViewAll = cam == null;
                if (!deathViewAll) deathView = WorldEffects.ViewOf(cam, 2f);
            }
            bool shown = deathViewAll || deathView.Contains(position);
            if (type != (byte)UnitType.Bomber && shown) Effects.UnitDeath(position, large, cause);
            if (unitAtlas == null || type == (byte)UnitType.Bomber || (uint)index >= (uint)rotation.Length) return;
            string id = AtlasIds[type] ??= ((UnitType)type).ToString();
            int directions = unitAtlas.Directions(id);
            if (directions <= 0) return;
            Sprite wreck = unitAtlas.Sprite(id, UnitFrames.Direction(rotation[index], directions) * unitAtlas.Frames(id));
            // A blast flings the wreck up to a tile from where it stood; fire leaves it burnt black.
            if (cause == 1) position += new Vector2(Mathf.Cos(index * 2.39996f), Mathf.Sin(index * 2.39996f)) * 0.6f;
            if (cause == 2) Decals.BurntWreck(position, wreck);
            else Decals.Wreck(position, wreck);
            // v0.20 (spec §8.1): shot dead, a turreted unit's head is thrown off to land beside its wreck, at the facing it last had.
            string head = HeadIds[type] ??= id + ArtManifest.TurretSuffix;
            int headDirections = cause == 0 && TurretAim.Has((UnitType)type) ? unitAtlas.Directions(head) : 0;
            if (headDirections > 0 && turretAngle.IsCreated && (uint)index < (uint)turretAngle.Length)
            {
                float a = turretAngle[index] + 2.4f, throwTiles = large ? 0.9f : 0.6f;
                Decals.Wreck(position + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * throwTiles,
                    unitAtlas.Sprite(head, TurretAim.Facing(turretAngle[index]) % headDirections * unitAtlas.Frames(head)));
            }
        }

        /// <summary>
        /// True unless a replay's per-player view (<see cref="UnitShown"/>) hides the unit at <paramref name="at"/>: its shots, death,
        /// wreck and arrival are drawn only when that player saw it (live play: always, the server sends only what the player sees).
        /// </summary>
        private bool Shown(int index, float2 at) => UnitShown == null || UnitShown(store.OwnerOf(index), at);

        /// <summary>v0.20: a known unit's drawn position, type and size by id index, for <see cref="CombatVisuals"/>.</summary>
        private (bool known, float2 at, byte type, float scale) UnitAt(int index)
        {
            if (!store.IsKnown(index) || (uint)index >= (uint)lastPosition.Length) return (false, default, 0, 0f);
            // A replay's per-player view hides what that player didn't see: its fire, laser and leap go with it.
            if (UnitShown != null && hidden[index] != 0) return (false, default, 0, 0f);
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
            Camera viewCamera = ViewCamera != null ? ViewCamera : Camera.main;
            buildingRenderer.Draw(known, Time.time, buildingColour, SettingsStore.Current.HealthBars, buildingSelected, viewCamera != null ? WorldEffects.ViewOf(viewCamera) : null);
            // Smoke, fire, glints and jammer pulses go off on their own timers: checked with the buildings' repack, not every frame.
            if (Time.time - buildingEffectsAt < InstancedBuildingRenderer.RepackSeconds && Time.time >= buildingEffectsAt) return;
            buildingEffectsAt = Time.time;
            PulseJammers(known);
            worldEffects.Buildings(known, WorldEffects.ViewOf(viewCamera), Time.time);
        }

        private float buildingEffectsAt = float.NegativeInfinity;

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
                if (!Shown(attacker, from)) continue; // a replay's per-player view: no shots from units that player can't see
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
