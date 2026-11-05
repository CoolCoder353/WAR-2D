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
        // Turrets (TurretAim), by index: whose it is (an index is reused with a new id), its angle and when it last turned, and what it
        // aims at until when. Only ever touched on the main thread, for units on screen or firing.
        private int[] turretOf;
        private float[] turretAngle, turretTurnedAt, aimUntil;
        private float2[] aimAt;
        private NativeList<UnitInstance> turrets;
        private NativeList<UnitInstance> barrelShadows;
        private Effects.FlameLine flameLine;
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
        private readonly List<(int gun, float2 landing, float seconds)> shellsFired = new List<(int, float2, float)>();
        private readonly List<float2> shellsLanded = new List<float2>();
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
            store = new ClientUnitStore(capacity, dt);
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
            store.ShellFired += (gun, landing, seconds) => shellsFired.Add((gun, landing, seconds));
            store.ShellLanded += shellsLanded.Add;
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
            turretOf = new int[capacity];
            turretAngle = new float[capacity];
            turretTurnedAt = new float[capacity];
            aimUntil = new float[capacity];
            aimAt = new float2[capacity];
            turrets = new NativeList<UnitInstance>(1024, Allocator.Persistent);
            barrelShadows = new NativeList<UnitInstance>(64, Allocator.Persistent);
            flameLine = TryFlameLine;
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
            if (turrets.IsCreated) { turrets.Dispose(); barrelShadows.Dispose(); }
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
            FireShells();
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
            Rect turretView = WorldEffects.ViewOf(ViewCamera != null ? ViewCamera : Camera.main, TurretViewMargin);
            turrets.Clear();
            barrelShadows.Clear();
            for (int i = 0; i < count; i++)
            {
                int index = known[i];
                float2 p = positions[i];
                float2 delta = p - lastPosition[index];
                bool moving = math.lengthsq(delta) > 1e-6f && math.lengthsq(delta) < 4f;
                if (moving) rotation[index] = math.atan2(delta.y, delta.x);
                lastPosition[index] = p;
                if (frames != null)
                    frame[index] = frames.BodyCell(store.TypeOf(index), rotation[index], moving, serverTime, serverTime - lastAction[index], index);
                color[index] = ColorOf(owners[index]);
                health01[index] = healthBytes[index] / 100f;
                TrackHit(index, healthBytes[index], p, owners[index], now, frameSeconds);
                if (moving) worldEffects.Moved(index, store.IdOf(index), store.TypeOf(index), p, view);
                drawScale[index] = UnitShown != null && !UnitShown(owners[index], p) ? -1f : ScaleOf(store.TypeOf(index));
                if (frames != null && frames.TurretFirst[store.TypeOf(index)] >= 0 && drawScale[index] > 0f && turretView.Contains((Vector2)p))
                    PackTurret(index, store.TypeOf(index), p, now);
            }
            Effects.FollowFlames(flameLine);

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

        /// <summary>Tiles past the screen's edge a unit's turret is still drawn: the longest barrel and its shadow reach this far.</summary>
        private const float TurretViewMargin = 2.5f;

        /// <summary>
        /// Turns a unit's turret toward what it fired at while it holds its aim, else toward the way it faces, and packs it (and a raised
        /// barrel's shadow) to be drawn. Turns by the time since it last did, so a unit that was off screen catches up at once.
        /// </summary>
        private void PackTurret(int index, byte type, float2 p, float now)
        {
            UnitType unit = (UnitType)type;
            ResetTurretIfNew(index);
            bool aiming = now < aimUntil[index];
            float hull = rotation[index];
            float goal = TurretAim.Goal(hull, aiming ? math.atan2(aimAt[index].y - p.y, aimAt[index].x - p.x) : 0f, aiming);
            turretAngle[index] = TurretAim.Step(turretAngle[index], goal, TurretAim.TurnRate(unit) * (now - turretTurnedAt[index]));
            turretTurnedAt[index] = now;
            int facing = TurretAim.Facing(turretAngle[index]);
            int cell = TurretAim.TurretFrame(frames.TurretFrames[type], aiming, TurretAim.Recoiling(serverTime - lastAction[index]));
            Vector2 pivot = TurretAim.PivotPixels(unit, TurretAim.HullAngle(hull)) / ArtStyle.PixelsPerUnit;
            float2 at = p + new float2(pivot.x, pivot.y);
            turrets.Add(new UnitInstance
            {
                position = at,
                frame = frames.TurretCell(type, facing, cell),
                color = color[index],
                scale = frames.TurretTiles[type],
                pad = (uint)(selected[index] & 1) | (uint)flash[index] << 1,
            });
            if (unit == UnitType.Artillery && aiming)
                barrelShadows.Add(new UnitInstance { position = at, frame = unitRenderer.BarrelShadowFrame + (uint)facing, color = 0xFFFFFFFFu, scale = BarrelShadowTiles });
        }

        /// <summary>Tiles across the raised barrel's shadow sprite is drawn.</summary>
        private const float BarrelShadowTiles = 3f;

        /// <summary>A new unit on this index starts with its turret straight ahead and no aim.</summary>
        private void ResetTurretIfNew(int index)
        {
            int id = store.IdOf(index);
            if (turretOf[index] == id) return;
            turretOf[index] = id;
            turretAngle[index] = rotation[index];
            turretTurnedAt[index] = Time.time;
            aimUntil[index] = float.NegativeInfinity;
        }

        /// <summary>
        /// A unit whose health dropped since last frame was hit: it flashes and its bar keeps a draining trail (<see cref="HitFeedback"/>).
        /// A unit new to this index (a new id) starts from its health, with no flash.
        /// </summary>
        private void TrackHit(int index, byte health, float2 position, int owner, float now, float frameSeconds)
        {
            int id = store.IdOf(index);
            if (trackedId[index] != id)
            {
                trackedId[index] = id;
                lastHealth[index] = health;
                hitAt[index] = float.NegativeInfinity;
                trail[index] = health / 100f;
            }
            else if (health < lastHealth[index])
            {
                // v0.16: fights on the minimap, but not an own unit's upkeep decay (code review 2026-11-03).
                bool own = owner == WorldEffects.LocalOwner;
                MinimapCombat combat = MinimapCombat.Current;
                if (combat != null && !(own && combat.IsOwnDecay(lastHealth[index] - health, now - hitAt[index]))) combat.Hit(position, own, now);
                hitAt[index] = now;
            }
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
                UnitType type = (UnitType)store.TypeOf(attacker);
                // The Gameplay setting thins or hides the tracer lines; the flash, sound, shells and flame streams stay.
                bool line = Overlays.OverlayRules.KeepTracer(tracerMode, tracerSerial++);
                if (TurretAim.Has(type))
                {
                    FireGun(attacker, type, from, target, to, line);
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
        /// This frame's shells: each flies from its gun's bore to where the server says it comes down (after <see cref="FireTracers"/>, so the
        /// turret aims there, not at the target), and each landing is an explosion and a crater, which is what a miss looks like too.
        /// </summary>
        private void FireShells()
        {
            for (int i = math.max(0, shellsFired.Count - MaxTracersPerFrame); i < shellsFired.Count; i++)
            {
                var (gun, landing, seconds) = shellsFired[i];
                if (!store.IsKnown(gun)) continue;
                ResetTurretIfNew(gun);
                aimAt[gun] = landing;
                aimUntil[gun] = Time.time + ConfigLoader.LoadConfig().GetUnit((UnitType)store.TypeOf(gun)).AttackInterval + TurretAim.HoldSeconds;
                float turret = TurretAim.FacingAngle(TurretAim.Facing(turretAngle[gun]));
                float2 from = store.PredictOne(gun, serverTime);
                Vector2 bore = new Vector2(from.x, from.y) + TurretAim.ArtilleryBore(TurretAim.HullAngle(rotation[gun]), turret) / ArtStyle.PixelsPerUnit;
                Effects.ShellFlight(bore, new Vector2(landing.x, landing.y), seconds);
            }
            shellsFired.Clear();
            for (int i = math.max(0, shellsLanded.Count - MaxTracersPerFrame); i < shellsLanded.Count; i++)
                Effects.ShellLanded(new Vector2(shellsLanded[i].x, shellsLanded[i].y));
            shellsLanded.Clear();
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
