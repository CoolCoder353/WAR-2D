using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// The hierarchical cache of the plan's Step 6, on top of a <see cref="SectorGraph"/>. A field is an
    /// order's route: the sectors its units stand in, plus the sectors the route passes through, each
    /// with one local 32x32 field towards its exit portal; the goal's sector is seeded with the order's
    /// own goal cells. Fields are built one order at a time (the route search runs on the calling thread,
    /// the sector fields as jobs), and <see cref="Invalidate"/> drops only the sectors a terrain change
    /// can touch.
    ///
    /// <para><b>What Task 6 must do with <see cref="FlowDirections.None"/></b>: a cell outside the
    /// covered sectors has no direction from this field — it means the unit is off the order's route
    /// (the route covers the sectors its units were in when the order was placed, plus the way to the
    /// goal). A sampled unit should keep its last heading from this field, or steer straight at the goal,
    /// and the sim should extend or re-acquire the order's field once a unit leaves the covered set;
    /// <see cref="CoversSector"/> tells it which sectors are covered.</para>
    /// </summary>
    public sealed class SectorFieldCache : IFlowFieldCache
    {
        /// <summary>Start cells one order keeps, enough to find the sectors its units stand in.</summary>
        private const int MaxStartCells = 512;

        private sealed class Order
        {
            public int2 Goal;
            public int SizeClass;
            public int RefCount;
            public int DirtySeq, BuiltSeq;
            public bool Dirty, Ready, InFlight, EverBuilt, Dead;
            public List<JobHandle> Handles; // the jobs of the route being built
            public NativeArray<int> Goals;      // the goal spread
            public NativeArray<int> StartCells; // the order's unit cells, subsampled
            public NativeArray<ushort>[] Costs;
            public NativeArray<byte>[] Directions;
            public bool[] Covered;              // per sector: has a local field
            public int CoveredCount;
        }

        private readonly SectorGraph graph;
        private readonly Allocator allocator;
        private readonly List<Order> orders = new List<Order>();
        private readonly Dictionary<long, int> byGoal = new Dictionary<long, int>();
        private readonly List<int> free = new List<int>();

        private JobHandle pending;
        private bool hasPending;
        private int inFlight;
        private long rebuilds;
        private bool disposed;

        /// <summary>Builds a cache whose routes are sector-scoped. <paramref name="map"/> is shared, not owned.</summary>
        public SectorFieldCache(in SpikeMap map, Allocator allocator)
        {
            graph = new SectorGraph(map, allocator);
            this.allocator = allocator;
        }

        /// <summary>The sector graph the routes are searched on.</summary>
        public SectorGraph Graph => graph;

        /// <summary>Tiles across.</summary>
        public int Width => graph.Width;

        /// <summary>Tiles down.</summary>
        public int Height => graph.Height;

        /// <summary>Sectors one order's route covers, summed over the live orders.</summary>
        public int CoveredSectorCount
        {
            get
            {
                int count = 0;
                foreach (Order order in orders) if (Live(order)) count += order.CoveredCount;
                return count;
            }
        }

        /// <summary>Live fields.</summary>
        public int LiveCount
        {
            get
            {
                int count = 0;
                foreach (Order order in orders) if (Live(order)) count++;
                return count;
            }
        }

        /// <summary>Live fields of one size class.</summary>
        public int LiveCountOfClass(int sizeClass)
        {
            int count = 0;
            foreach (Order order in orders) if (Live(order) && order.SizeClass == sizeClass) count++;
            return count;
        }

        /// <summary>Bytes the live orders' local fields hold: 3 bytes per cell of every covered sector.</summary>
        public long LiveBytes
        {
            get
            {
                long bytes = 0;
                foreach (Order order in orders) if (Live(order)) bytes += order.CoveredCount * SectorGraph.SectorCells * 3L;
                return bytes;
            }
        }

        /// <summary>Bytes the hierarchy holds itself: the graph's crops and cost tables.</summary>
        public long GraphBytes => graph.GraphBytes;

        /// <summary>Nothing is pooled here; the field arrays live exactly as long as their order.</summary>
        public long PooledBytes => 0;

        /// <summary>Order rebuilds scheduled.</summary>
        public long RebuildCount => rebuilds;

        /// <summary>Live fields whose route predates the latest terrain change.</summary>
        public int DirtyCount
        {
            get
            {
                int count = 0;
                foreach (Order order in orders) if (Live(order) && !order.Ready) count++;
                return count;
            }
        }

        /// <summary>
        /// Adds a follower to the order for (<paramref name="goal"/>, <paramref name="sizeClass"/>),
        /// creating it when no live order matches. <paramref name="startCells"/> are the cells of the
        /// units being ordered: they decide which sectors the route covers and are copied, so the caller
        /// keeps ownership of its array.
        /// </summary>
        public int Acquire(int2 goal, int sizeClass = FlowSizeClass.Small, int followers = 1, NativeArray<int> startCells = default)
        {
            ThrowIfDisposed();
            if (sizeClass < 0 || sizeClass >= FlowSizeClass.Count)
                throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "unknown size class");

            int handle = Find(goal, sizeClass);
            if (handle < 0) handle = Create(goal, sizeClass, startCells);
            orders[handle].RefCount += math.max(1, followers);
            return handle;
        }

        /// <summary>Drops <paramref name="followers"/> followers; the last one frees the field.</summary>
        public void Release(int handle, int followers = 1)
        {
            Order order = OrderAt(handle);
            if (order == null || order.RefCount <= 0) return;
            order.RefCount -= math.max(1, followers);
            if (order.RefCount > 0) return;
            order.RefCount = 0;
            if (order.InFlight) order.Dead = true; // its jobs still read the field's arrays
            else DisposeOrder(handle);
        }

        /// <summary>Drops followers of the field for a goal and class.</summary>
        public void Release(int2 goal, int sizeClass = FlowSizeClass.Small, int followers = 1)
        {
            int handle = Find(goal, sizeClass);
            if (handle >= 0) Release(handle, followers);
        }

        /// <summary>True while the handle's field has a follower.</summary>
        public bool IsLive(int handle) => OrderAt(handle) != null;

        /// <summary>True when the handle's route and local fields are current.</summary>
        public bool IsReady(int handle)
        {
            Order order = OrderAt(handle);
            return order != null && order.Ready;
        }

        /// <summary>True when the handle's route covers the sector a cell falls in.</summary>
        public bool CoversSector(int handle, int sector)
        {
            Order order = OrderAt(handle);
            return order != null && order.Covered != null && sector >= 0 && sector < order.Covered.Length && order.Covered[sector];
        }

        /// <summary>
        /// The direction at a cell, resolved through the order's route sectors. Cells outside the route
        /// return <see cref="FlowDirections.None"/>: see the class summary for what Task 6 does with it.
        /// </summary>
        public byte DirectionAtCell(int handle, int cellIndex)
        {
            Order order = OrderAt(handle);
            if (order == null || order.Covered == null) return FlowDirections.None;
            int width = graph.Width;
            if (cellIndex < 0 || cellIndex >= width * graph.Height) return FlowDirections.None;
            int sector = graph.SectorOfCell(cellIndex);
            if (!order.Covered[sector]) return FlowDirections.None;
            int x = cellIndex % width, y = cellIndex / width;
            return order.Directions[sector][y % SectorGraph.SectorSize * SectorGraph.SectorSize + x % SectorGraph.SectorSize];
        }

        /// <summary>
        /// Marks every live order stale after a terrain change. The graph drops the portals, crops and
        /// cost tables of the tile's sector and its eight neighbours; the orders' routes are re-searched
        /// when they are rebuilt.
        /// </summary>
        public void Invalidate(int2 changedTile)
        {
            ThrowIfDisposed();
            graph.Invalidate(changedTile);
            foreach (Order order in orders)
            {
                if (!Live(order)) continue;
                order.DirtySeq++;
                order.Dirty = true;
                order.Ready = false;
            }
        }

        /// <summary>
        /// Schedules up to <paramref name="maxPerTick"/> stale orders for a rebuild, never-built orders
        /// first. One order's rebuild is its whole route: the abstract search runs here, then one sector
        /// field job per covered sector is scheduled in parallel.
        /// </summary>
        public JobHandle RebuildDirty(int maxPerTick)
        {
            ThrowIfDisposed();
            if (maxPerTick <= 0) return pending;

            int scheduled = 0;
            var handles = new List<JobHandle>(maxPerTick * 4);
            for (int pass = 0; pass < 2 && scheduled < maxPerTick; pass++)
            {
                bool newOnly = pass == 0;
                for (int i = 0; i < orders.Count && scheduled < maxPerTick; i++)
                {
                    Order order = orders[i];
                    if (order == null || !Live(order) || !order.Dirty || order.InFlight) continue;
                    if (newOnly != !order.EverBuilt) continue;
                    handles.AddRange(Rebuild(i, order));
                    scheduled++;
                }
            }
            if (handles.Count > 0)
            {
                var all = new NativeArray<JobHandle>(handles.ToArray(), Allocator.Temp);
                JobHandle batch = JobHandle.CombineDependencies(all);
                all.Dispose();
                pending = hasPending ? JobHandle.CombineDependencies(pending, batch) : batch;
                hasPending = true;
            }
            return pending;
        }

        /// <summary>
        /// Blocks until the scheduled rebuilds have finished, then marks each order ready if no terrain
        /// change has dirtied it meanwhile. Orders whose last follower left while rebuilding are freed.
        /// </summary>
        public void CompleteRebuilds()
        {
            if (inFlight == 0) return;
            pending.Complete();
            pending = default;
            hasPending = false;
            inFlight = 0;

            for (int i = 0; i < orders.Count; i++)
            {
                Order order = orders[i];
                if (order == null || !order.InFlight) continue;
                // Complete this order's own jobs as well as the batch: a handle is only ever used
                // once, so completing it here is what guarantees its arrays are safe to replace.
                if (order.Handles != null)
                {
                    foreach (JobHandle handle in order.Handles) handle.Complete();
                    order.Handles = null;
                }
                order.InFlight = false;
                order.EverBuilt = true;
                if (order.Dead)
                {
                    DisposeOrder(i);
                    continue;
                }
                if (order.BuiltSeq != order.DirtySeq) continue; // dirtied again while rebuilding
                order.Dirty = false;
                order.Ready = true;
            }
        }

        /// <summary>Completes anything in flight, frees every order and disposes the graph.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (inFlight > 0) pending.Complete();
            pending = default;
            hasPending = false;
            inFlight = 0;
            foreach (Order order in orders) if (order != null) order.InFlight = false;

            foreach (Order order in orders)
            {
                if (order == null) continue;
                if (order.Goals.IsCreated) order.Goals.Dispose();
                if (order.StartCells.IsCreated) order.StartCells.Dispose();
                DisposeFields(order);
            }
            orders.Clear();
            byGoal.Clear();
            free.Clear();
            graph.Dispose();
        }

        private static bool Live(Order order) => order != null && !order.Dead && order.RefCount > 0;

        private Order OrderAt(int handle) =>
            handle >= 0 && handle < orders.Count && Live(orders[handle]) ? orders[handle] : null;

        private long Key(int2 goal, int sizeClass) => ((long)goal.y * graph.Width + goal.x) << 3 | (uint)sizeClass;

        private int Find(int2 goal, int sizeClass)
        {
            if (!byGoal.TryGetValue(Key(goal, sizeClass), out int handle)) return -1;
            return OrderAt(handle) != null ? handle : -1;
        }

        private int Create(int2 goal, int sizeClass, NativeArray<int> startCells)
        {
            NativeArray<byte> grid = graph.GridOf(sizeClass);

            int count = math.min(startCells.Length, MaxStartCells);
            var starts = new NativeArray<int>(count, Allocator.Persistent);
            for (int i = 0; i < count; i++) starts[i] = startCells[i];

            var order = new Order
            {
                Goal = goal,
                SizeClass = sizeClass,
                Goals = FlowGoals.Around(goal, grid, graph.Width, graph.Height, Allocator.Persistent),
                StartCells = starts,
                DirtySeq = 1,
                Dirty = true,
            };

            int handle;
            if (free.Count > 0)
            {
                handle = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                orders[handle] = order;
            }
            else
            {
                handle = orders.Count;
                orders.Add(order);
            }
            byGoal[Key(goal, sizeClass)] = handle;
            return handle;
        }

        private void DisposeOrder(int handle)
        {
            Order order = orders[handle];
            if (order == null) return;
            long key = Key(order.Goal, order.SizeClass);
            if (byGoal.TryGetValue(key, out int mapped) && mapped == handle) byGoal.Remove(key);
            order.Goals.Dispose();
            order.StartCells.Dispose();
            DisposeFields(order);
            orders[handle] = null;
            free.Add(handle);
        }

        private static void DisposeFields(Order order)
        {
            if (order.Costs == null) return;
            foreach (NativeArray<ushort> cost in order.Costs)
                if (cost.IsCreated) cost.Dispose();
            foreach (NativeArray<byte> direction in order.Directions)
                if (direction.IsCreated) direction.Dispose();
            order.Costs = null;
            order.Directions = null;
            order.Covered = null;
            order.CoveredCount = 0;
        }

        /// <summary>
        /// One order's rebuild: search the route, then schedule a seeded field job per covered sector.
        /// Returns the field jobs' handles so the tick's rebuild can complete them together.
        /// </summary>
        private List<JobHandle> Rebuild(int handle, Order order)
        {
            order.InFlight = true;
            order.BuiltSeq = order.DirtySeq;
            inFlight++;
            rebuilds++;

            SectorFieldSpec[] specs = graph.BuildRoute(order.SizeClass, order.Goals, order.StartCells, out int covered);
            DisposeFields(order);
            int sectors = graph.SectorsX * graph.SectorsY;
            order.Costs = new NativeArray<ushort>[sectors];
            order.Directions = new NativeArray<byte>[sectors];
            order.Covered = new bool[sectors];
            order.CoveredCount = covered;

            NativeArray<byte> grid = graph.GridOf(order.SizeClass);
            var handles = new List<JobHandle>(specs.Length);
            foreach (SectorFieldSpec spec in specs)
            {
                var cost = new NativeArray<ushort>(SectorGraph.SectorCells, Allocator.Persistent);
                var direction = new NativeArray<byte>(SectorGraph.SectorCells, Allocator.Persistent);
                order.Costs[spec.Sector] = cost;
                order.Directions[spec.Sector] = direction;
                order.Covered[spec.Sector] = true;

                var job = new BuildSectorFieldJob
                {
                    Width = SectorGraph.SectorSize, Height = SectorGraph.SectorSize,
                    Tiles = graph.CropOf(order.SizeClass, spec.Sector), WriteDirection = true,
                    Goals = spec.GoalCells, GoalCosts = spec.GoalCosts,
                    ExitCells = spec.ExitCells, ExitDirection = spec.ExitDirection,
                    Cost = cost, Direction = direction,
                };
                handles.Add(job.Schedule());
            }
            order.Handles = handles;
            return handles;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(SectorFieldCache));
        }
    }
}
