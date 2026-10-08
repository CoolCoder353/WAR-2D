using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The spike simulation's system group and the world it lives in. <see cref="SpikeSim"/> builds the
    /// whole thing (see its own summary), adds the five stages in order and drives one 20 Hz tick per
    /// <see cref="SpikeSim.Tick"/>.
    ///
    /// <para>In sync mode the group also times each stage's dependency completion after the systems have
    /// run, which is the spike's per-system breakdown: the first completion covers the first stage's
    /// jobs, and every later one covers the next stage's share of what is still running.</para>
    /// </summary>
    [DisableAutoCreation]
    public partial class SpikeTickGroup : ComponentSystemGroup
    {
        /// <summary>The five stage systems' handles, in update order; set by <see cref="SpikeSim"/>.</summary>
        public SystemHandle[] Stages;

        /// <summary>Per-stage completion time in ms, filled by the breakdown when it is on.</summary>
        public double[] StageMilliseconds;

        /// <summary>True while the breakdown is on (sync runs); async runs must not complete inside the group.</summary>
        public bool Breakdown;

        /// <summary>The lifecycle stage's handle: the breakdown plays its recorded commands back.</summary>
        public SystemHandle LifecycleHandle;

        private readonly Stopwatch stopwatch = new Stopwatch();

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (!Breakdown || Stages == null) return;

            for (int i = 0; i < Stages.Length; i++)
            {
                stopwatch.Restart();
                World.Unmanaged.ResolveSystemStateRef(Stages[i]).Dependency.Complete();
                stopwatch.Stop();
                StageMilliseconds[i] = stopwatch.Elapsed.TotalMilliseconds;
            }

            // The lifecycle left its commands for us: the jobs are complete now, so playing them back
            // is the structural half of the tick, outside the per-stage timings.
            World.Unmanaged.GetUnsafeSystemRef<SpikeLifecycleSystem>(LifecycleHandle)
                .PlaybackPending(World.EntityManager);
        }
    }

    /// <summary>
    /// The spike's simulation world: one <see cref="World"/> named <c>Spike</c> with a
    /// <see cref="SpikeTickGroup"/> holding the gather, hash, combat, movement and lifecycle systems,
    /// a singleton entity carrying <see cref="SpikeWorldData"/>, and the hand-driven 20 Hz tick.
    ///
    /// <para>The whole thing is deliberately outside the game's default world: every spike system is
    /// <c>[DisableAutoCreation]</c>, so nothing here can see or be seen by the game's systems.</para>
    ///
    /// <para>Orders are order slots: a slot is a direction grid copied out of the flow cache before the
    /// tick loop (outside the measured section, per the plan's ruling), and a unit follows a slot
    /// through <see cref="SpikeUnit.FieldGoal"/>. The tick itself never touches the flow cache.</para>
    /// </summary>
    public sealed partial class SpikeSim : IDisposable
    {
        /// <summary>The stages in update order, the names the breakdown reports.</summary>
        public static readonly string[] StageNames = { "gather", "hash", "combat", "movement", "lifecycle" };

        private readonly Allocator allocator;
        private readonly FlowFieldCache fieldCache;
        private readonly Entity unitSingleton;
        private readonly EntityQuery unitQuery;
        private readonly int[] orderHandles;
        private readonly SystemHandle lifecycleHandle;

        private SpikeWorldData data;
        private int tick;
        private bool disposed;

        /// <summary>The spike's own world; the game's world never sees it.</summary>
        public World World { get; }

        /// <summary>The group the tick drives; its <c>Update</c> runs one simulation tick.</summary>
        public SpikeTickGroup Group { get; }

        /// <summary>The flow cache orders were copied from, if the helper acquired any (released on dispose).</summary>
        public FlowFieldCache FieldCache => fieldCache;

        /// <summary>The world-data singleton this run was built with (a copy of the component).</summary>
        public SpikeWorldData Data => data;

        /// <summary>The entity manager of the spike world.</summary>
        public EntityManager EntityManager => World.EntityManager;

        /// <summary>Ticks run so far.</summary>
        public int Ticks => tick;

        private SpikeSim(in SpikeSimConfig config, in SpikeMap map, Allocator allocator, FlowFieldCache fieldCache)
        {
            this.allocator = allocator;
            this.fieldCache = fieldCache;

            int players = math.max(1, config.Players);
            int capacity = math.max(1, config.Capacity);
            int idCapacity = math.max(capacity + 1, config.IdCapacity);
            int cellsX = (map.Width + config.CellSize - 1) / config.CellSize;
            int cellsY = (map.Height + config.CellSize - 1) / config.CellSize;
            int cells = cellsX * cellsY;
            int orderCapacity = math.max(0, config.OrderCapacity);
            int searchCells = (int)math.ceil(SpikeSimRules.AttackRange / config.CellSize);

            data = new SpikeWorldData
            {
                Players = players,
                Capacity = capacity,
                IdCapacity = idCapacity,
                OrderCapacity = orderCapacity,
                Dt = SpikeSimRules.TickSeconds,
                Speed = SpikeSimRules.Speed,
                AttackRange = SpikeSimRules.AttackRange,
                AttackRangeSq = SpikeSimRules.AttackRange * SpikeSimRules.AttackRange,
                AttackDamage = SpikeSimRules.AttackDamage,
                AttackCooldown = SpikeSimRules.AttackCooldown,
                SeparationStrength = config.SeparationStrength,
                SeparationIterations = math.clamp(config.SeparationIterations, 1, 2),
                SeparationInterval = math.max(1, config.SeparationInterval),
                MoveHalves = config.MoveHalves,
                Slice = math.max(1, config.Slice),
                CellSize = config.CellSize,
                CellsX = cellsX,
                CellsY = cellsY,
                SearchCells = searchCells,
                SpawnPerTick = math.max(0, config.SpawnPerTick),
                SpawnTarget = math.max(0, config.SpawnTarget),
                SpawnHealth = config.SpawnHealth,
                Async = config.Async,
                Width = map.Width,
                Height = map.Height,
                Tiles = map.Tiles,
                LargeGrid = BuildLargeGrid(map, allocator),
                Directions = new NativeArray<byte>(orderCapacity * map.Width * map.Height, allocator, NativeArrayOptions.UninitializedMemory),
                OrderBase = new NativeArray<int>(orderCapacity, allocator, NativeArrayOptions.UninitializedMemory),
                OrderHandle = Filled(orderCapacity, -1),
                PlayerSlot = Filled(players * 2, -1),
                Positions = new NativeArray<float2>(capacity, allocator, NativeArrayOptions.UninitializedMemory),
                Velocity = new NativeArray<float2>(capacity, allocator),
                Team = new NativeArray<byte>(capacity, allocator),
                SizeClass = new NativeArray<byte>(capacity, allocator),
                Health = new NativeArray<float>(capacity, allocator),
                Radius = new NativeArray<float>(capacity, allocator),
                RangeSq = new NativeArray<float>(capacity, allocator),
                Cooldown = new NativeArray<float>(capacity, allocator),
                FieldGoal = Filled(capacity, -1),
                Arrived = new NativeArray<byte>(capacity, allocator),
                IdOf = new NativeArray<int>(capacity, allocator),
                IndexOfId = Filled(idCapacity, -1),
                Cell = new NativeArray<int>(capacity, allocator),
                CellStart = new NativeArray<int>(cells + 1, allocator),
                Sorted = new NativeArray<int>(capacity, allocator),
                Target = Filled(capacity, -1),
                SpawnCursor = new NativeArray<int>(players, allocator),
                SpawnBase = new NativeArray<int>(players, allocator),
                SpawnCount = new NativeArray<int>(players, allocator),
                SpawnTiles = new NativeArray<int2>(players * SpawnTileCount, allocator),
                NextId = Filled(1, 1),
            };
            data.OrderCount = 0;
            for (int i = 0; i < capacity; i++) data.RangeSq[i] = data.AttackRangeSq;
            FillSpawnTiles(config, map, players);

            World = new World("Spike");
            Group = World.GetOrCreateSystemManaged<SpikeTickGroup>();
            SystemHandle gather = World.CreateSystem<SpikeGatherSystem>();
            SystemHandle hash = World.CreateSystem<SpikeHashSystem>();
            SystemHandle combat = World.CreateSystem<SpikeCombatSystem>();
            SystemHandle movement = World.CreateSystem<SpikeMovementSystem>();
            SystemHandle lifecycle = World.CreateSystem<SpikeLifecycleSystem>();
            Group.AddSystemToUpdateList(gather);
            Group.AddSystemToUpdateList(hash);
            Group.AddSystemToUpdateList(combat);
            Group.AddSystemToUpdateList(movement);
            Group.AddSystemToUpdateList(lifecycle);
            Group.SortSystems();
            Group.Stages = new[] { gather, hash, combat, movement, lifecycle };
            Group.StageMilliseconds = new double[Group.Stages.Length];
            Group.LifecycleHandle = lifecycle;
            lifecycleHandle = lifecycle;

            EntityManager entityManager = World.EntityManager;
            data.UnitArchetype = entityManager.CreateArchetype(typeof(SpikeUnit));
            unitSingleton = entityManager.CreateEntity(typeof(SpikeWorldData));
            entityManager.SetComponentData(unitSingleton, data);
            unitQuery = entityManager.CreateEntityQuery(typeof(SpikeUnit));

            orderHandles = new int[orderCapacity];
            for (int i = 0; i < orderHandles.Length; i++) orderHandles[i] = -1;
        }

        /// <summary>
        /// Builds a spike simulation: its own world, the group, the five stages and the singleton, with
        /// <paramref name="config"/>'s sizes and knobs. <paramref name="map"/>'s tile array is shared,
        /// not copied, and the caller keeps owning it. <paramref name="fieldCache"/> may be null while
        /// no orders are added; a non-null one is released by <see cref="Dispose"/>.
        /// </summary>
        public static SpikeSim Create(in SpikeSimConfig config, in SpikeMap map, Allocator allocator,
            FlowFieldCache fieldCache = null) => new SpikeSim(config, map, allocator, fieldCache);

        /// <summary>
        /// Adds the scenario's units to the world, ids 1..n in index order (unit i of player p is
        /// <c>p * UnitsPerPlayer + i</c>), all with no orders and no cooldown. The caller creates the
        /// sim with a capacity of at least the scenario's unit count.
        /// </summary>
        public void AddScenario(in SpikeScenario scenario)
        {
            int count = scenario.UnitCount;
            if (count > data.Capacity)
                throw new InvalidOperationException($"{count} units do not fit a capacity of {data.Capacity}");
            for (int i = 0; i < count; i++)
            {
                AddUnit(new SpikeUnit
                {
                    Id = i + 1,
                    Owner = scenario.UnitOwners[i],
                    Team = scenario.UnitOwners[i],
                    SizeClass = scenario.UnitSizeClass[i],
                    Radius = scenario.UnitRadius[i],
                    Health = scenario.UnitHealth[i],
                    Position = scenario.UnitPositions[i],
                    TargetId = -1,
                    FieldGoal = -1,
                });
            }
            data.NextId[0] = count + 1;
        }

        /// <summary>Creates one unit entity from <paramref name="unit"/> and returns it.</summary>
        public Entity AddUnit(in SpikeUnit unit)
        {
            Entity entity = World.EntityManager.CreateEntity(data.UnitArchetype);
            World.EntityManager.SetComponentData(entity, unit);
            return entity;
        }

        /// <summary>
        /// Registers (goal, size class) as a new order slot: acquires the field from
        /// <paramref name="cache"/>, builds it and copies its directions into the sim, all outside the
        /// measured tick. The slot is the value units carry in <see cref="SpikeUnit.FieldGoal"/> and the
        /// one <see cref="SetPlayerSlot"/> maps an army to. Returns the slot.
        /// </summary>
        public int AddField(FlowFieldCache cache, int2 goal, int sizeClass = FlowSizeClass.Small)
        {
            int handle = cache.Acquire(goal, sizeClass, 1);
            cache.RebuildDirty(data.OrderCapacity + 1).Complete();
            cache.CompleteRebuilds();
            if (!cache.TryGetField(handle, out FlowField field))
                throw new InvalidOperationException("the flow cache dropped the field the sim just acquired");
            return AddField(field, handle);
        }

        /// <summary>Registers an already-built field view as an order slot and copies its directions.</summary>
        public int AddField(in FlowField field, int cacheHandle = -1)
        {
            if (data.OrderCount >= data.OrderCapacity)
                throw new InvalidOperationException($"the sim holds {data.OrderCapacity} order slots");
            int slot = data.OrderCount++;
            data.OrderBase[slot] = slot * data.Width * data.Height;
            data.OrderHandle[slot] = cacheHandle;
            orderHandles[slot] = cacheHandle;
            NativeArray<byte>.Copy(field.Direction,
                data.Directions.GetSubArray(data.OrderBase[slot], data.Width * data.Height), data.Width * data.Height);
            World.EntityManager.SetComponentData(unitSingleton, data);
            return slot;
        }

        /// <summary>
        /// Re-copies every order slot's directions from the flow cache, for a run that changed terrain
        /// and rebuilt its fields. Outside the tick, like <see cref="AddField(FlowFieldCache,int2,int)"/>.
        /// </summary>
        public void RefreshFields(FlowFieldCache cache)
        {
            for (int slot = 0; slot < data.OrderCount; slot++)
            {
                int handle = orderHandles[slot];
                if (handle < 0 || !cache.TryGetField(handle, out FlowField field)) continue;
                NativeArray<byte>.Copy(field.Direction,
                    data.Directions.GetSubArray(data.OrderBase[slot], data.Width * data.Height), data.Width * data.Height);
            }
        }

        /// <summary>The order slot an army and size class orders at, or -1.</summary>
        public void SetPlayerSlot(int player, int sizeClass, int slot)
        {
            if ((uint)player >= (uint)data.Players) throw new ArgumentOutOfRangeException(nameof(player));
            if ((uint)sizeClass >= 2) throw new ArgumentOutOfRangeException(nameof(sizeClass));
            data.PlayerSlot[player * 2 + sizeClass] = slot;
            World.EntityManager.SetComponentData(unitSingleton, data);
        }

        /// <summary>Orders every unit at <paramref name="slot"/> (or clears orders with -1).</summary>
        public void OrderAll(int slot, int owner = -1) => WriteOrders(slot, owner, 0, 0);

        /// <summary>
        /// The scripted order intake: at an order pulse, every army's units whose
        /// <c>(Id + pulse) % stride == 0</c> take their player's order slot, so each pulse re-orders
        /// about <c>1 / stride</c> of every army and four pulses cover it. Outside the measured tick.
        /// </summary>
        public void ApplyOrders(int pulse, int stride) =>
            WriteOrders(PulseOrders, -1, pulse, math.max(1, stride));

        /// <summary>A slot value meaning "take the unit's player slot", used by <see cref="ApplyOrders"/>.</summary>
        private const int PulseOrders = -2;

        /// <summary>
        /// Writes order slots straight into the unit components, chunk by chunk, on the main thread.
        /// It runs between ticks with every job complete, so it needs no job of its own - and it is
        /// outside the measured section, which is where the plan puts order intake.
        /// </summary>
        private void WriteOrders(int slot, int owner, int pulse, int stride)
        {
            Complete();
            var chunks = unitQuery.ToArchetypeChunkArray(Allocator.Temp);
            ComponentTypeHandle<SpikeUnit> units = World.EntityManager.GetComponentTypeHandle<SpikeUnit>(isReadOnly: false);
            for (int c = 0; c < chunks.Length; c++)
            {
                NativeArray<SpikeUnit> array = chunks[c].GetNativeArray(ref units);
                for (int i = 0; i < array.Length; i++)
                {
                    SpikeUnit unit = array[i];
                    if (owner >= 0 && unit.Owner != owner) continue;
                    if (slot == PulseOrders)
                    {
                        if ((unit.Id + pulse) % stride != 0) continue;
                        int playerSlot = data.PlayerSlot[unit.Owner * 2 + unit.SizeClass];
                        if (playerSlot < 0) continue;
                        unit.FieldGoal = playerSlot;
                    }
                    else
                    {
                        unit.FieldGoal = slot;
                    }
                    array[i] = unit;
                }
            }
            chunks.Dispose();
        }

        /// <summary>Waits for every job the world tracks; the caller's sync point between ticks.</summary>
        public void Complete() => World.EntityManager.CompleteAllTrackedJobs();

        /// <summary>The next id the spawner will hand out; the caller keeps it past the units it added itself.</summary>
        public void SetNextId(int nextId) => data.NextId[0] = nextId;

        /// <summary>Main-thread milliseconds the last asynchronous tick spent at its boundary.</summary>
        public double LastBoundaryMilliseconds { get; private set; }

        /// <summary>
        /// One 20 Hz tick: sets the world clock, updates the group and, in sync mode, completes the
        /// tick's jobs before returning.
        ///
        /// <para>In async mode the boundary runs first: the previous tick's jobs are completed and its
        /// lifecycle commands played back, so the tick itself only schedules and never waits on its own
        /// stages. Between these two calls the worker threads are free to run the tick's jobs while the
        /// main thread does whatever else the frame has to do. The boundary's cost is reported in
        /// <see cref="LastBoundaryMilliseconds"/>.</para>
        /// </summary>
        public void Tick()
        {
            if (data.Async) Settle();
            else LastBoundaryMilliseconds = 0;

            World.SetTime(new TimeData(tick * SpikeSimRules.TickSeconds, SpikeSimRules.TickSeconds));
            tick++;
            Group.Update();
            if (!data.Async) World.EntityManager.CompleteAllTrackedJobs();
        }

        /// <summary>
        /// The async tick boundary on its own: completes the previous tick's jobs and plays back its
        /// lifecycle commands, so the world can be read (fog, encoding, rendering) before the next
        /// <see cref="Tick"/> schedules. Calling it again before the next tick costs nothing; its cost
        /// is <see cref="LastBoundaryMilliseconds"/>.
        /// </summary>
        public void Settle()
        {
            var boundary = Stopwatch.StartNew();
            World.EntityManager.CompleteAllTrackedJobs();
            World.Unmanaged.GetUnsafeSystemRef<SpikeLifecycleSystem>(lifecycleHandle)
                .PlaybackPending(World.EntityManager);
            boundary.Stop();
            LastBoundaryMilliseconds = boundary.Elapsed.TotalMilliseconds;
        }

        /// <summary>Turns the group's per-stage breakdown on or off (sync mode only).</summary>
        public void SetBreakdown(bool on)
        {
            Group.Breakdown = on;
            data.Breakdown = on;
            World.EntityManager.SetComponentData(unitSingleton, data);
        }

        /// <summary>Disposes the world, the fields, and every array the sim allocated. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // No job may still be reading the SoA arrays or recording into a pending command buffer
            // when we release the world, the fields and the arrays.
            Complete();

            if (fieldCache != null)
            {
                for (int slot = 0; slot < orderHandles.Length; slot++)
                    if (orderHandles[slot] >= 0) fieldCache.Release(orderHandles[slot], 1);
            }

            unitQuery.Dispose();
            World.Dispose();

            data.Directions.Dispose();
            data.OrderBase.Dispose();
            data.OrderHandle.Dispose();
            data.PlayerSlot.Dispose();
            data.Positions.Dispose();
            data.Velocity.Dispose();
            data.Team.Dispose();
            data.SizeClass.Dispose();
            data.Health.Dispose();
            data.Radius.Dispose();
            data.RangeSq.Dispose();
            data.Cooldown.Dispose();
            data.FieldGoal.Dispose();
            data.Arrived.Dispose();
            data.IdOf.Dispose();
            data.IndexOfId.Dispose();
            data.Cell.Dispose();
            data.CellStart.Dispose();
            data.Sorted.Dispose();
            data.Target.Dispose();
            data.LargeGrid.Dispose();
            data.SpawnCursor.Dispose();
            data.SpawnBase.Dispose();
            data.SpawnCount.Dispose();
            data.SpawnTiles.Dispose();
            data.NextId.Dispose();
        }

        /// <summary>A persistent array of <paramref name="count"/> copies of <paramref name="value"/>.</summary>
        private NativeArray<int> Filled(int count, int value)
        {
            var array = new NativeArray<int>(math.max(0, count), allocator);
            for (int i = 0; i < array.Length; i++) array[i] = value;
            return array;
        }

        /// <summary>
        /// The grid a large unit is allowed to stand on: a floor tile whose 3 x 3 neighbourhood is all
        /// floor, the same clearance >= 2 predicate <see cref="FlowSizeClass.MinClearance"/> gives the
        /// large class. Built once, from the map as it is at creation.
        /// </summary>
        private static NativeArray<byte> BuildLargeGrid(in SpikeMap map, Allocator allocator)
        {
            var grid = new NativeArray<byte>(map.Width * map.Height, allocator, NativeArrayOptions.UninitializedMemory);
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                byte value = 1;
                if (map.TileAt(new int2(x, y)) == SpikeMap.Floor)
                {
                    value = 0;
                    for (int dy = -1; dy <= 1 && value == 0; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (map.IsBlocked(new int2(x + dx, y + dy)))
                        {
                            value = 1;
                            break;
                        }
                }
                grid[y * map.Width + x] = value;
            }
            return grid;
        }

        /// <summary>
        /// Fills every player's spawn list: the free floor tiles of a spiral out from the player's
        /// origin, up to <see cref="SpawnTileCount"/> each. Spawns cycle these, so a burst of spawns
        /// lands on distinct tiles and never in a wall. A player with no room keeps an empty list and
        /// simply never spawns.
        /// </summary>
        private void FillSpawnTiles(in SpikeSimConfig config, in SpikeMap map, int players)
        {
            for (int player = 0; player < players; player++)
            {
                int baseIndex = player * SpawnTileCount;
                data.SpawnBase[player] = baseIndex;
                data.SpawnCount[player] = 0;
                if (config.SpawnPerTick <= 0 || config.SpawnTarget <= 0) continue;

                var origins = config.SpawnOrigins;
                int2 origin = origins.IsCreated && player < origins.Length
                    ? origins[player]
                    : new int2(map.Width / 2, map.Height / 2);
                int maxRing = math.max(map.Width, map.Height);
                for (int ring = 0; ring <= maxRing && data.SpawnCount[player] < SpawnTileCount; ring++)
                {
                    int ringTiles = ring == 0 ? 1 : 8 * ring;
                    for (int i = 0; i < ringTiles && data.SpawnCount[player] < SpawnTileCount; i++)
                    {
                        int2 tile = FlowGoals.RingTile(origin, ring, i);
                        if (!map.Contains(tile) || map.IsBlocked(tile)) continue;
                        data.SpawnTiles[baseIndex + data.SpawnCount[player]] = tile;
                        data.SpawnCount[player]++;
                    }
                }
            }
        }

        /// <summary>Tiles a player's spawn spiral collects.</summary>
        private const int SpawnTileCount = 256;
    }
}

