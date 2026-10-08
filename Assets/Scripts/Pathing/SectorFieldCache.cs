using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Pathing
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
    public sealed class SectorFieldCache : IDisposable
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

        // Table bookkeeping: which sectors of each handle hold a block, and the free blocks.
        private readonly System.Collections.Generic.Dictionary<int, List<int>> publishedSectors = new Dictionary<int, List<int>>();
        private readonly Stack<int> freeBlocks = new Stack<int>();

        private readonly SectorGraph graph;
        private readonly Allocator allocator;
        private readonly List<Order> orders = new List<Order>();
        private readonly Dictionary<long, int> byGoal = new Dictionary<long, int>();
        private readonly List<int> free = new List<int>();

        private JobHandle pending;
        private bool hasPending;
        private int inFlight;
        private long rebuilds;
        private long newRoutes, reRoutes, newSectorFields, reSectorFields;
        private long unreachableRebuilds, unreachableStartSectors;
        private bool disposed;

        private readonly WAR2D.World.MapGrid source;
        private NativeArray<byte> blocked;
        private readonly bool ownsBlocked;

        /// <summary>
        /// Builds a cache over a pathing grid the caller owns and edits directly (tests and tools): a
        /// change to <paramref name="map"/>'s tiles is picked up by <see cref="Invalidate"/>.
        /// </summary>
        public SectorFieldCache(in PathMap map, Allocator allocator, int cellSize = 1)
        {
            blocked = map.Tiles;
            graph = new SectorGraph(map, allocator, cellSize);
            this.allocator = allocator;
        }

        /// <summary>
        /// Builds a cache whose routes are sector-scoped, over the map's blocked grid (walls, gems,
        /// border and building footprints). <paramref name="map"/> is shared, not owned; the cache owns
        /// the blocked grid and refreshes a tile of it on <see cref="Invalidate"/>.
        /// </summary>
        public SectorFieldCache(in WAR2D.World.MapGrid map, Allocator allocator, int cellSize = 2)
        {
            source = map;
            blocked = PathMap.Combine(map, allocator);
            ownsBlocked = true;
            graph = new SectorGraph(new PathMap(map.Width, map.Height, blocked), allocator, cellSize);
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
                foreach (Order order in orders) if (Live(order)) bytes += order.CoveredCount * graph.SectorCells * 3L;
                return bytes;
            }
        }

        /// <summary>Bytes the hierarchy holds itself: the graph's crops and cost tables.</summary>
        public long GraphBytes => graph.GraphBytes;

        /// <summary>Nothing is pooled here; the field arrays live exactly as long as their order.</summary>
        public long PooledBytes => 0;

        /// <summary>Order rebuilds scheduled.</summary>
        public long RebuildCount => rebuilds;

        /// <summary>Rebuilds that materialise a brand new route.</summary>
        public long NewRoutes => newRoutes;

        /// <summary>Rebuilds that re-materialise a route a terrain change made stale.</summary>
        public long ReRoutes => reRoutes;

        /// <summary>Sector fields built for new routes.</summary>
        public long NewSectorFields => newSectorFields;

        /// <summary>Sector fields rebuilt because of a terrain change.</summary>
        public long ReSectorFields => reSectorFields;

        /// <summary>Rebuilds whose search left at least one of the order's start sectors unreachable.</summary>
        public long UnreachableRebuilds => unreachableRebuilds;

        /// <summary>Start sectors left unreachable, summed over rebuilds.</summary>
        public long UnreachableStartSectors => unreachableStartSectors;

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
            // A unit samples the (half-resolution) cell that covers its tile.
            var tile = new int2(cellIndex % width, cellIndex / width);
            int sector = graph.SectorOf(tile);
            if (!order.Covered[sector]) return FlowDirections.None;
            int cell = graph.CellOf(tile);
            int side = graph.SectorCellsSide;
            int x = cell % graph.CellWidth % side, y = cell / graph.CellWidth % side;
            return order.Directions[sector][y * side + x];
        }

        /// <summary>
        /// Marks every live order stale after a terrain change. The graph drops the portals, crops and
        /// cost tables of the tile's sector and its eight neighbours; the orders' routes are re-searched
        /// when they are rebuilt.
        /// </summary>
        public void Invalidate(int2 changedTile)
        {
            ThrowIfDisposed();
            if (ownsBlocked)
            {
                if (!source.Contains(changedTile)) return;
                int index = source.Index(changedTile);
                byte value = PathMap.Blocked(source, index);
                if (blocked[index] == value) return;
                blocked[index] = value;
            }
            graph.Invalidate(changedTile);

            // Selectivity: the change can only alter a route that runs through the changed sector, one
            // of its neighbours, or the crossings between them, so an order whose covered sectors all
            // sit at least two sectors away keeps its materialised fields (and stays ready). A route
            // that passes further away cannot cross the changed portals; the one approximation is that
            // a far-away change that would have offered a cheaper route is only picked up when that
            // order is next rebuilt.
            int sector = graph.SectorOf(changedTile);
            int sx = sector % graph.SectorsX, sy = sector / graph.SectorsX;
            foreach (Order order in orders)
            {
                if (!Live(order)) continue;
                if (!Touches(order, sx, sy)) continue;
                order.DirtySeq++;
                order.Dirty = true;
                order.Ready = false;
            }
        }

        /// <summary>True when the order's route enters the changed sector or one of its eight neighbours.</summary>
        private bool Touches(Order order, int sx, int sy)
        {
            if (order.Covered == null) return true; // never routed: it has to be built anyway
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = sx + dx, y = sy + dy;
                if ((uint)x >= (uint)graph.SectorsX || (uint)y >= (uint)graph.SectorsY) continue;
                if (order.Covered[y * graph.SectorsX + x]) return true;
            }
            return false;
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

        /// <summary>A table sized for this cache's map, for <see cref="CompleteRebuilds(ref OrderFieldTable)"/>.</summary>
        public OrderFieldTable CreateTable(Allocator allocator) =>
            new OrderFieldTable(graph.SectorsX, graph.SectorsY, graph.CellSize, allocator);

        /// <summary>
        /// Extends an order's route to cover a sector a unit wandered into (pushed off the route, or
        /// spawned outside it): the sector's centre tile joins the order's start cells (up to the cap)
        /// and the order is rebuilt on a later tick.
        /// </summary>
        public void AddStartSector(int handle, int sector)
        {
            Order order = OrderAt(handle);
            if (order == null || sector < 0 || sector >= graph.SectorsX * graph.SectorsY) return;
            if (order.Covered != null && order.Covered[sector]) return;
            int sx = sector % graph.SectorsX, sy = sector / graph.SectorsX;
            int2 centre = math.min(new int2(sx, sy) * SectorGraph.SectorSize + SectorGraph.SectorSize / 2,
                new int2(graph.Width - 1, graph.Height - 1));
            int cell = centre.y * graph.Width + centre.x;
            for (int i = 0; i < order.StartCells.Length; i++)
                if (order.StartCells[i] == cell) { MarkDirty(order); return; }
            if (order.StartCells.Length >= MaxStartCells) return;
            var grown = new NativeArray<int>(order.StartCells.Length + 1, Allocator.Persistent);
            NativeArray<int>.Copy(order.StartCells, grown, order.StartCells.Length);
            grown[order.StartCells.Length] = cell;
            order.StartCells.Dispose();
            order.StartCells = grown;
            MarkDirty(order);
        }

        private static void MarkDirty(Order order)
        {
            order.DirtySeq++;
            order.Dirty = true;
        }

        /// <summary>
        /// Schedules up to <paramref name="maxPerTick"/> stale orders' rebuilds and returns their handle
        /// without completing it (the tick completes it at the next boundary).
        /// </summary>
        public JobHandle ScheduleRebuilds(int maxPerTick) => RebuildDirty(maxPerTick);

        /// <summary>
        /// Completes the scheduled rebuilds, then publishes every sector they produced into
        /// <paramref name="table"/> and frees the blocks of orders that died or sectors a route dropped.
        /// </summary>
        public void CompleteRebuilds(ref OrderFieldTable table)
        {
            var rebuilt = new List<int>();
            for (int i = 0; i < orders.Count; i++)
                if (orders[i] != null && orders[i].InFlight) rebuilt.Add(i);
            CompleteRebuilds();

            // Orders that died: free their blocks.
            var handles = new List<int>(publishedSectors.Keys);
            foreach (int handle in handles)
            {
                if (OrderAt(handle) != null) continue;
                FreeBlocks(ref table, handle, keep: null);
            }

            foreach (int handle in rebuilt)
            {
                Order order = OrderAt(handle);
                if (order == null || order.Directions == null || handle >= OrderFieldTable.MaxOrders) continue;
                FreeBlocks(ref table, handle, keep: order.Covered);
                if (!publishedSectors.TryGetValue(handle, out List<int> sectors))
                    publishedSectors[handle] = sectors = new List<int>();
                int blockCells = table.CellsPerSector * table.CellsPerSector;
                for (int sector = 0; sector < order.Covered.Length; sector++)
                {
                    if (!order.Covered[sector]) continue;
                    int slot = handle * table.SectorCount + sector;
                    int block = table.BlockOf[slot];
                    bool isNew = block < 0;
                    if (isNew)
                    {
                        if (freeBlocks.Count > 0) block = freeBlocks.Pop();
                        else
                        {
                            block = table.BlockVersion.Length;
                            table.BlockVersion.Add(0);
                            table.Blocks.ResizeUninitialized(table.Blocks.Length + blockCells);
                        }
                        table.BlockOf[slot] = block;
                        sectors.Add(sector);
                    }
                    // Only a block whose directions changed is rewritten (and versioned).
                    NativeArray<byte> fresh = order.Directions[sector];
                    NativeArray<byte> target = table.Blocks.AsArray().GetSubArray(block * blockCells, blockCells);
                    bool changed = table.BlockVersion[block] == 0 || isNew;
                    for (int c = 0; c < blockCells && !changed; c++) changed = target[c] != fresh[c];
                    if (!changed) continue;
                    NativeArray<byte>.Copy(fresh, target, blockCells);
                    table.BlockVersion[block] = table.BlockVersion[block] + 1;
                }
            }
        }

        /// <summary>Frees every block a handle published (its order was released), so a reused handle starts clean.</summary>
        public void Unpublish(ref OrderFieldTable table, int handle) => FreeBlocks(ref table, handle, keep: null);

        /// <summary>True when the handle has at least one sector in the table.</summary>
        public bool HasPublished(int handle) => publishedSectors.ContainsKey(handle);

        private void FreeBlocks(ref OrderFieldTable table, int handle, bool[] keep)
        {
            if (!publishedSectors.TryGetValue(handle, out List<int> sectors)) return;
            for (int i = sectors.Count - 1; i >= 0; i--)
            {
                int sector = sectors[i];
                if (keep != null && sector < keep.Length && keep[sector]) continue;
                int slot = handle * table.SectorCount + sector;
                if (table.BlockOf[slot] >= 0) freeBlocks.Push(table.BlockOf[slot]);
                table.BlockOf[slot] = -1;
                sectors.RemoveAt(i);
            }
            if (sectors.Count == 0) publishedSectors.Remove(handle);
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
            if (ownsBlocked && blocked.IsCreated) blocked.Dispose();
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

            // The goal spread is a set of free cells, so it is laid out on the cell grid (half-resolution
            // cells at cellSize 2) and reported back as map cell indices for the route search.
            NativeArray<int> spread = FlowGoals.Around(
                new int2(goal.x / graph.CellSize, goal.y / graph.CellSize), grid,
                graph.CellWidth, graph.CellHeight, Allocator.Persistent,
                spread: startCells.IsCreated ? (startCells.Length + 3) / 4 : FlowGoals.Spread); // about 4 units per goal cell (a 2x2-tile cell)
            for (int i = 0; i < spread.Length; i++) spread[i] = graph.TileOfCell(spread[i]);

            var order = new Order
            {
                Goal = goal,
                SizeClass = sizeClass,
                Goals = spread,
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
            if (order.EverBuilt)
            {
                reRoutes++;
            }
            else
            {
                newRoutes++;
            }

            SectorFieldSpec[] specs = graph.BuildRoute(order.SizeClass, order.Goals, order.StartCells, out int covered);
            if (graph.UnreachableStartSectors > 0)
            {
                unreachableRebuilds++;
                unreachableStartSectors += graph.UnreachableStartSectors;
            }
            DisposeFields(order);
            int sectors = graph.SectorsX * graph.SectorsY;
            order.Costs = new NativeArray<ushort>[sectors];
            order.Directions = new NativeArray<byte>[sectors];
            order.Covered = new bool[sectors];
            order.CoveredCount = covered;

            NativeArray<byte> grid = graph.GridOf(order.SizeClass);
            if (order.EverBuilt) reSectorFields += specs.Length;
            else newSectorFields += specs.Length;
            var handles = new List<JobHandle>(specs.Length);
            foreach (SectorFieldSpec spec in specs)
            {
                var cost = new NativeArray<ushort>(graph.SectorCells, Allocator.Persistent);
                var direction = new NativeArray<byte>(graph.SectorCells, Allocator.Persistent);
                order.Costs[spec.Sector] = cost;
                order.Directions[spec.Sector] = direction;
                order.Covered[spec.Sector] = true;

                var job = new BuildSectorFieldJob
                {
                    Width = graph.SectorCellsSide, Height = graph.SectorCellsSide,
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
