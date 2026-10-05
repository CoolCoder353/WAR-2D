using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>One sector's local field to build: its goals, what each is seeded with, and its exit.</summary>
    public struct SectorFieldSpec
    {
        /// <summary>Sector index, <c>y * SectorsX + x</c>.</summary>
        public int Sector;

        /// <summary>Crop-local cell indices of the field's goals.</summary>
        public FixedList128Bytes<int> GoalCells;

        /// <summary>The cost-to-goal each goal cell starts at (0 for an order's own goal cells).</summary>
        public FixedList128Bytes<ushort> GoalCosts;

        /// <summary>Cells of the sector's exit portal, written with the crossing step.</summary>
        public FixedList128Bytes<int> ExitCells;

        /// <summary>The step every exit cell is written with, or <see cref="FlowDirections.None"/>.</summary>
        public byte ExitDirection;
    }

    /// <summary>
    /// The plan's Step 6 hierarchy: 32x32 sectors, a portal per maximal run of traversable cells along
    /// each shared sector edge, an intra-sector portal-to-portal cost table per sector (built with the
    /// same integration job restricted to the sector), and a search over the portal graph giving every
    /// portal its cost to the goal. Everything is per size class, on the clearance-filtered grid, and is
    /// materialized lazily per sector: a sector's portals, tile crop and cost table are only computed
    /// when a route touches it, and <see cref="Invalidate"/> drops them for one sector and its eight
    /// neighbours only.
    /// </summary>
    public sealed class SectorGraph : IDisposable
    {
        /// <summary>Tiles a sector spans on each side.</summary>
        public const int SectorSize = 32;

        /// <summary>Cells in a sector.</summary>
        public const int SectorCells = SectorSize * SectorSize;

        /// <summary>Portal slots a sector can hold; a 32-cell edge has at most 16 alternating runs.</summary>
        public const int MaxPortals = 64;

        /// <summary>Costs at least this large count as unreachable, as in the integration job.</summary>
        private const ushort Infinite = BuildIntegrationFieldJob.Unreachable;

        /// <summary>Step cost of crossing a sector boundary, one tile step.</summary>
        private const int CrossingCost = 10;

        /// <summary>Search nodes expanded before the route search gives up, per order.</summary>
        private const int MaxSearchNodes = 200000;

        // Edge indices, and (W, E, S, N) the direction byte a unit steps in to cross each edge.
        private const int West = 0, East = 1, South = 2, North = 3;
        private static readonly byte[] Crossing = { 4, 0, 6, 2 };
        private static readonly int[] Opposite = { East, West, North, South };

        /// <summary>A maximal run of traversable cells along one of a sector's edges.</summary>
        public struct Portal
        {
            /// <summary>Which edge the run lies on: 0 W, 1 E, 2 S, 3 N.</summary>
            public int Edge;

            /// <summary>First cell of the run along the edge (y on W/E, x on S/N).</summary>
            public int Start;

            /// <summary>Cells in the run.</summary>
            public int Length;
        }

        private sealed class SectorState
        {
            public Portal[] Portals;
            public int[] PortalCells;    // flat: portal p's cells are at PortalOffsets[p] ..
            public int[] PortalOffsets;
            public ushort[] Costs;       // Portals.Length^2, Infinite when the sector cannot connect them
            public bool CostsReady;
            public NativeArray<byte> Crop;
            public int[] Matching;       // portal -> matching portal across the edge; -2 not looked up yet
        }

        private sealed class Layer
        {
            public NativeArray<byte> Grid;
            public SectorState[] Sectors;
            public ushort[] Distance;    // node = sector * MaxPortals + portal
            public bool[] Settled;
            public List<int> Heap;       // one entry per relaxation, so it can repeat a node
        }

        private readonly int width, height, sectorsX, sectorsY, sectorCount;
        private readonly Allocator allocator;
        private readonly NativeArray<byte> tiles;      // the map's grid, not owned
        private readonly NativeArray<byte> largeGrid;  // the large class's filtered grid, owned
        private readonly NativeArray<byte> clearance;
        private readonly Layer[] layers = new Layer[FlowSizeClass.Count];

        private bool disposed;

        /// <summary>
        /// Builds the graph for both size classes. <paramref name="map"/>'s tile array stays shared: the
        /// caller flips tiles in it and calls <see cref="Invalidate"/>, which also refreshes the
        /// clearance window and the large class's filtered grid.
        /// </summary>
        public SectorGraph(in SpikeMap map, Allocator allocator)
        {
            width = map.Width;
            height = map.Height;
            sectorsX = (width + SectorSize - 1) / SectorSize;
            sectorsY = (height + SectorSize - 1) / SectorSize;
            sectorCount = sectorsX * sectorsY;
            this.allocator = allocator;
            tiles = map.Tiles;

            clearance = new NativeArray<byte>(width * height, allocator);
            largeGrid = new NativeArray<byte>(width * height, allocator);
            new ClearanceJob { Width = width, Height = height, Tiles = tiles, Clearance = clearance }.Run();
            new FilterTilesJob
            {
                Width = width, Height = height, Tiles = tiles, Clearance = clearance,
                MinClearance = FlowSizeClass.MinClearance(FlowSizeClass.Large), Filtered = largeGrid,
            }.Run(width * height);

            layers[FlowSizeClass.Small] = NewLayer(tiles);
            layers[FlowSizeClass.Large] = NewLayer(largeGrid);
        }

        /// <summary>Clearance per tile, capped at <see cref="ClearanceJob.MaxClearance"/>.</summary>
        public NativeArray<byte> Clearance => clearance;

        /// <summary>The large size class's filtered grid.</summary>
        public NativeArray<byte> LargeGrid => largeGrid;

        /// <summary>The map's width in tiles.</summary>
        public int Width => width;

        /// <summary>The map's height in tiles.</summary>
        public int Height => height;

        /// <summary>The map's own tile grid, the small class's grid.</summary>
        public NativeArray<byte> Tiles => tiles;

        /// <summary>The grid a size class paths on: the map's tiles, or the large class's filtered copy.</summary>
        public NativeArray<byte> GridOf(int sizeClass) => layers[sizeClass].Grid;

        /// <summary>A sector's tile crop, materializing it if needed. The graph owns it.</summary>
        public NativeArray<byte> CropOf(int sizeClass, int sector)
        {
            Materialize(layers[sizeClass], sector);
            return layers[sizeClass].Sectors[sector].Crop;
        }

        /// <summary>Sectors across.</summary>
        public int SectorsX => sectorsX;

        /// <summary>Sectors down.</summary>
        public int SectorsY => sectorsY;

        /// <summary>Sector index of a tile: <c>(y / 32) * SectorsX + x / 32</c>.</summary>
        public int SectorOf(int2 tile) => tile.y / SectorSize * sectorsX + tile.x / SectorSize;

        /// <summary>Sector index of a cell index.</summary>
        public int SectorOfCell(int cell) => cell / width / SectorSize * sectorsX + cell % width / SectorSize;

        /// <summary>Bytes the graph itself holds: the large grid, the sector crops and cost tables.</summary>
        public long GraphBytes
        {
            get
            {
                long bytes = width * height * 2L; // clearance + large grid
                foreach (Layer layer in layers)
                {
                    if (layer == null) continue;
                    foreach (SectorState state in layer.Sectors)
                    {
                        if (state == null) continue;
                        bytes += SectorCells;
                        bytes += (long)state.Portals.Length * state.Portals.Length * 2;
                    }
                }
                return bytes;
            }
        }

        /// <summary>Sectors whose portals and crops are materialized, both classes together.</summary>
        public int MaterializedSectors
        {
            get
            {
                int count = 0;
                foreach (Layer layer in layers)
                {
                    if (layer == null) continue;
                    foreach (SectorState state in layer.Sectors) if (state != null) count++;
                }
                return count;
            }
        }

        /// <summary>The portals of a sector, materializing its crop and portal runs on first use.</summary>
        public Portal[] PortalsOf(int sizeClass, int sector)
        {
            ThrowIfDisposed();
            Materialize(layers[sizeClass], sector);
            return layers[sizeClass].Sectors[sector].Portals;
        }

        /// <summary>Crop-local cells of one portal.</summary>
        public int[] PortalCellsOf(int sizeClass, int sector, int portal)
        {
            ThrowIfDisposed();
            Materialize(layers[sizeClass], sector);
            SectorState state = layers[sizeClass].Sectors[sector];
            int length = state.Portals[portal].Length;
            var cells = new int[length];
            Array.Copy(state.PortalCells, state.PortalOffsets[portal], cells, 0, length);
            return cells;
        }

        /// <summary>The cost of walking from one portal of a sector to another inside it, or 65535.</summary>
        public ushort PortalCost(int sizeClass, int sector, int from, int to)
        {
            Layer layer = layers[sizeClass];
            EnsureCosts(layer, sector);
            SectorState state = layer.Sectors[sector];
            return state.Costs[from * state.Portals.Length + to];
        }

        /// <summary>
        /// The local field specs for one order: a field for each goal sector, seeded with the order's
        /// goal cells at cost 0, and a field for every sector on the route from a start sector to a goal
        /// sector, seeded with that sector's exit portal at its cost to the goal.
        /// </summary>
        public SectorFieldSpec[] BuildRoute(
            int sizeClass, NativeArray<int> goalCells, NativeArray<int> startCells, out int coveredSectors)
        {
            ThrowIfDisposed();
            Layer layer = layers[sizeClass];
            var specs = new List<SectorFieldSpec>();
            var covered = new HashSet<int>();

            // 1. The goal sectors: one spec each, seeded with the goal cells that fall inside them.
            var goalSpecs = new Dictionary<int, SectorFieldSpec>();
            var goalCellsBySector = new Dictionary<int, List<int>>();
            for (int i = 0; i < goalCells.Length; i++)
            {
                int cell = goalCells[i];
                int sector = SectorOfCell(cell);
                if (!goalCellsBySector.TryGetValue(sector, out List<int> list))
                {
                    list = new List<int>(8);
                    goalCellsBySector[sector] = list;
                    goalSpecs[sector] = NewSpec(sector);
                }
                list.Add(LocalCell(cell, sector));
            }
            foreach (KeyValuePair<int, List<int>> pair in goalCellsBySector)
            {
                SectorFieldSpec spec = goalSpecs[pair.Key];
                foreach (int local in pair.Value)
                {
                    if (spec.GoalCells.Length >= spec.GoalCells.Capacity) break;
                    spec.GoalCells.Add(local);
                    spec.GoalCosts.Add(0);
                }
                goalSpecs[pair.Key] = spec;
            }

            // 2. Seed the search from every goal sector: its portals start at their in-sector distance
            //    to that sector's goal cells, which the same integration job gives us.
            ResetSearch(layer);
            foreach (KeyValuePair<int, List<int>> pair in goalCellsBySector)
            {
                // BuildInSectorField returns, per portal of the sector, the cost from its cells to the
                // goal cells: exactly the seed each portal's node starts at.
                ushort[] toGoals = BuildInSectorField(layer, pair.Key, pair.Value);
                for (int p = 0; p < toGoals.Length; p++) Seed(layer, pair.Key, p, toGoals[p]);
            }

            // 3. Expand the abstract graph until every start sector can leave, then walk each start
            //    sector's route to a goal sector, collecting the fields that route needs.
            int[] startSectors = StartSectors(startCells);
            ExpandUntilSettled(layer, startSectors);
            foreach (int start in startSectors)
            {
                int sector = start;
                while (!goalSpecs.ContainsKey(sector))
                {
                    Materialize(layer, sector);
                    SectorState state = layer.Sectors[sector];
                    int exit = BestPortal(layer, sector);
                    if (exit < 0 || !covered.Add(sector)) break; // nowhere to leave, or a cycle
                    specs.Add(RouteSpec(layer, sector, exit));
                    int neighbour = NeighbourSector(sector, state.Portals[exit].Edge);
                    if (neighbour < 0) break;
                    sector = neighbour;
                }
            }

            foreach (KeyValuePair<int, SectorFieldSpec> pair in goalSpecs)
            {
                covered.Add(pair.Key);
                specs.Add(pair.Value);
            }
            coveredSectors = covered.Count;
            return specs.ToArray();
        }

        /// <summary>
        /// Drops the portals, crops and cost tables of the tile's sector and its eight neighbours, and
        /// refreshes the clearance and large-class grid around the tile. Everything else in the graph is
        /// left alone: a terrain change touches nine sectors, not the map.
        /// </summary>
        public void Invalidate(int2 changedTile)
        {
            ThrowIfDisposed();
            int2 low = math.max(changedTile - ClearanceJob.MaxClearance, int2.zero);
            int2 high = math.min(changedTile + ClearanceJob.MaxClearance, new int2(width - 1, height - 1));
            int2 origin = low, size = high - low + 1;
            new ClearanceJob
            {
                Width = width, Height = height, Tiles = tiles,
                WindowOrigin = origin, WindowSize = size, Clearance = clearance,
            }.Run();
            new FilterTilesJob
            {
                Width = width, Height = height, Origin = origin, Size = size, Tiles = tiles,
                Clearance = clearance, MinClearance = FlowSizeClass.MinClearance(FlowSizeClass.Large),
                Filtered = largeGrid,
            }.Run(size.x * size.y);

            int sx = changedTile.x / SectorSize, sy = changedTile.y / SectorSize;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = sx + dx, y = sy + dy;
                if ((uint)x >= (uint)sectorsX || (uint)y >= (uint)sectorsY) continue;
                DropSector(layers[FlowSizeClass.Small], y * sectorsX + x);
                DropSector(layers[FlowSizeClass.Large], y * sectorsX + x);
            }
        }

        /// <summary>Frees every sector crop.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (Layer layer in layers)
            {
                if (layer == null) continue;
                foreach (SectorState state in layer.Sectors)
                    if (state != null && state.Crop.IsCreated) state.Crop.Dispose();
            }
            clearance.Dispose();
            largeGrid.Dispose();
        }

        // ---- sector state --------------------------------------------------------------------

        private Layer NewLayer(NativeArray<byte> grid) => new Layer
        {
            Grid = grid,
            Sectors = new SectorState[sectorCount],
            Distance = new ushort[sectorCount * MaxPortals],
            Settled = new bool[sectorCount * MaxPortals],
            Heap = new List<int>(sectorCount),
        };

        private void DropSector(Layer layer, int sector)
        {
            // A neighbour's matching cache holds portal indices into this sector, so it goes stale with
            // it: forget those caches too.
            for (int edge = West; edge <= North; edge++)
            {
                int neighbour = NeighbourSector(sector, edge);
                if (neighbour < 0) continue;
                SectorState other = layer.Sectors[neighbour];
                if (other == null) continue;
                for (int i = 0; i < other.Matching.Length; i++) other.Matching[i] = NotLookedUp;
            }

            SectorState state = layer.Sectors[sector];
            if (state == null) return;
            if (state.Crop.IsCreated) state.Crop.Dispose();
            layer.Sectors[sector] = null;
        }

        private void Materialize(Layer layer, int sector)
        {
            if (layer.Sectors[sector] != null) return;

            int sx = sector % sectorsX, sy = sector / sectorsX;
            int x0 = sx * SectorSize, y0 = sy * SectorSize;
            int x1 = math.min(x0 + SectorSize - 1, width - 1), y1 = math.min(y0 + SectorSize - 1, height - 1);

            var crop = new NativeArray<byte>(SectorCells, Allocator.Persistent);
            for (int i = 0; i < SectorCells; i++) crop[i] = SpikeMap.Rock; // outside a partial sector: blocked
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                crop[(y - y0) * SectorSize + (x - x0)] = layer.Grid[y * width + x];

            var portals = new List<Portal>(8);
            var cells = new List<int>(SectorSize * 4);
            var offsets = new List<int>(9) { 0 };
            if (x0 > 0) AddRuns(layer.Grid, portals, cells, offsets, West, x0, x0 - 1, y0, y1, alongY: true, x0, y0);
            if (x1 + 1 < width) AddRuns(layer.Grid, portals, cells, offsets, East, x1, x1 + 1, y0, y1, alongY: true, x0, y0);
            if (y0 > 0) AddRuns(layer.Grid, portals, cells, offsets, South, y0, y0 - 1, x0, x1, alongY: false, x0, y0);
            if (y1 + 1 < height) AddRuns(layer.Grid, portals, cells, offsets, North, y1, y1 + 1, x0, x1, alongY: false, x0, y0);

            var matching = new int[portals.Count];
            for (int i = 0; i < matching.Length; i++) matching[i] = NotLookedUp;
            layer.Sectors[sector] = new SectorState
            {
                Crop = crop,
                Portals = portals.ToArray(),
                PortalCells = cells.ToArray(),
                PortalOffsets = offsets.ToArray(),
                Matching = matching,
            };
        }

        private const int NotLookedUp = -2;

        /// <summary>
        /// Appends the maximal runs of cells that are open on both sides of one edge. The cell on this
        /// sector's side of the edge is the portal cell; a unit steps one tile across from it.
        /// </summary>
        private int AddRuns(
            NativeArray<byte> grid, List<Portal> portals, List<int> cells, List<int> offsets,
            int edge, int edgeLine, int acrossLine, int from, int to, bool alongY, int x0, int y0)
        {
            int runStart = -1;
            for (int along = from; along <= to + 1; along++)
            {
                bool open = false;
                if (along <= to)
                {
                    int edgeCell = alongY ? along * width + edgeLine : edgeLine * width + along;
                    int acrossCell = alongY ? along * width + acrossLine : acrossLine * width + along;
                    open = grid[edgeCell] == SpikeMap.Floor && grid[acrossCell] == SpikeMap.Floor;
                }
                if (open)
                {
                    if (runStart < 0) runStart = along;
                    continue;
                }
                if (runStart >= 0 && portals.Count < MaxPortals)
                {
                    int length = along - runStart;
                    portals.Add(new Portal { Edge = edge, Start = runStart, Length = length });
                    for (int i = 0; i < length; i++)
                    {
                        int value = runStart + i;
                        int px = alongY ? edgeLine : value;
                        int py = alongY ? value : edgeLine;
                        cells.Add((py - y0) * SectorSize + (px - x0));
                    }
                    offsets.Add(cells.Count);
                }
                runStart = -1;
            }
            return cells.Count;
        }

        private void EnsureCosts(Layer layer, int sector)
        {
            Materialize(layer, sector);
            SectorState state = layer.Sectors[sector];
            if (state.CostsReady) return;

            int n = state.Portals.Length;
            state.Costs = new ushort[math.max(1, n * n)];
            for (int i = 0; i < state.Costs.Length; i++) state.Costs[i] = Infinite;

            if (n > 0)
            {
                var fields = new NativeArray<ushort>[n];
                var goals = new NativeArray<int>[n];
                var handles = new NativeArray<JobHandle>(n, Allocator.Temp);
                for (int p = 0; p < n; p++)
                {
                    fields[p] = new NativeArray<ushort>(SectorCells, Allocator.TempJob);
                    goals[p] = GoalsOf(state, p);
                    handles[p] = new BuildIntegrationFieldJob
                    {
                        Width = SectorSize, Height = SectorSize, Tiles = state.Crop,
                        Goals = goals[p], Cost = fields[p],
                    }.Schedule();
                }
                JobHandle.CombineDependencies(handles).Complete();
                handles.Dispose();
                foreach (NativeArray<int> goal in goals) goal.Dispose();
                for (int p = 0; p < n; p++)
                for (int q = 0; q < n; q++)
                    state.Costs[p * n + q] = MinOverPortal(state, q, fields[p]);
                foreach (NativeArray<ushort> field in fields) field.Dispose();
            }
            state.CostsReady = true;
        }

        private static NativeArray<int> GoalsOf(SectorState state, int portal)
        {
            int length = state.Portals[portal].Length;
            var goals = new NativeArray<int>(length, Allocator.TempJob);
            for (int i = 0; i < length; i++) goals[i] = state.PortalCells[state.PortalOffsets[portal] + i];
            return goals;
        }

        private static ushort MinOverPortal(SectorState state, int portal, NativeArray<ushort> field)
        {
            ushort best = Infinite;
            int offset = state.PortalOffsets[portal];
            for (int i = 0; i < state.Portals[portal].Length; i++)
                best = (ushort)math.min((int)best, (int)field[state.PortalCells[offset + i]]);
            return best;
        }

        /// <summary>
        /// Runs the integration job over one sector, seeded by <paramref name="goals"/> at cost 0, and
        /// returns the cost at each of the sector's portals. The field is thrown away unless
        /// <paramref name="into"/> is created, in which case the field is left there for the caller.
        /// </summary>
        private ushort[] BuildInSectorField(Layer layer, int sector, List<int> goals)
        {
            Materialize(layer, sector);
            SectorState state = layer.Sectors[sector];
            var goalArray = new NativeArray<int>(goals.Count, Allocator.TempJob);
            for (int i = 0; i < goals.Count; i++) goalArray[i] = goals[i];
            var field = new NativeArray<ushort>(SectorCells, Allocator.TempJob);
            new BuildIntegrationFieldJob
            {
                Width = SectorSize, Height = SectorSize, Tiles = state.Crop, Goals = goalArray, Cost = field,
            }.Run();
            goalArray.Dispose();

            var mins = new ushort[state.Portals.Length];
            for (int p = 0; p < mins.Length; p++) mins[p] = MinOverPortal(state, p, field);
            field.Dispose();
            return mins;
        }

        // ---- the abstract search -------------------------------------------------------------

        private void ResetSearch(Layer layer)
        {
            Array.Fill(layer.Distance, Infinite);
            Array.Fill(layer.Settled, false);
            layer.Heap.Clear();
        }

        private void Seed(Layer layer, int sector, int portal, ushort cost)
        {
            if (cost >= Infinite) return;
            int node = sector * MaxPortals + portal;
            if (cost >= layer.Distance[node]) return;
            layer.Distance[node] = cost;
            HeapPush(layer, node);
        }

        /// <summary>The distinct sectors the order's start cells fall in, at most 64 of them.</summary>
        private int[] StartSectors(NativeArray<int> startCells)
        {
            if (startCells.Length == 0) return Array.Empty<int>();
            var sectors = new List<int>(8);
            var seen = new HashSet<int>();
            for (int i = 0; i < startCells.Length && sectors.Count < 64; i++)
            {
                int sector = SectorOfCell(startCells[i]);
                if (seen.Add(sector)) sectors.Add(sector);
            }
            return sectors.ToArray();
        }

        private void ExpandUntilSettled(Layer layer, int[] startSectors)
        {
            if (startSectors.Length == 0) return;
            int remaining = startSectors.Length;
            for (int expanded = 0; expanded < MaxSearchNodes && layer.Heap.Count > 0 && remaining > 0; expanded++)
            {
                int node = HeapPop(layer);
                if (layer.Settled[node]) continue;
                int sector = node / MaxPortals, portal = node % MaxPortals;
                bool reachedStart = !HasSettledPortal(layer, sector) && IsStartSector(startSectors, sector);
                layer.Settled[node] = true;
                if (reachedStart) remaining--;

                EnsureCosts(layer, sector);
                SectorState state = layer.Sectors[sector];
                int n = state.Portals.Length;
                ushort distance = layer.Distance[node];
                for (int q = 0; q < n; q++)
                {
                    if (q == portal) continue;
                    ushort viaSector = state.Costs[portal * n + q];
                    if (viaSector >= Infinite) continue;
                    Relax(layer, sector, q, distance + viaSector);
                }
                int matching = Matching(layer, sector, portal);
                if (matching >= 0)
                {
                    int neighbour = NeighbourSector(sector, state.Portals[portal].Edge);
                    if (neighbour >= 0) Relax(layer, neighbour, matching, distance + CrossingCost);
                }
            }
        }

        private static bool IsStartSector(int[] startSectors, int sector)
        {
            for (int i = 0; i < startSectors.Length; i++)
                if (startSectors[i] == sector) return true;
            return false;
        }

        private bool HasSettledPortal(Layer layer, int sector)
        {
            // A sector counts as reached once any portal of it is settled; Dijkstra settles the
            // cheapest portal of a sector first, so its best exit is known then.
            for (int p = 0; p < MaxPortals; p++)
                if (layer.Settled[sector * MaxPortals + p]) return true;
            return false;
        }

        private void Relax(Layer layer, int sector, int portal, int cost)
        {
            if (cost >= Infinite) return;
            Materialize(layer, sector);
            int node = sector * MaxPortals + portal;
            if (layer.Settled[node] || cost >= layer.Distance[node]) return;
            layer.Distance[node] = (ushort)cost;
            HeapPush(layer, node);
        }

        private int BestPortal(Layer layer, int sector)
        {
            int best = -1;
            ushort bestCost = Infinite;
            for (int p = 0; p < MaxPortals; p++)
            {
                ushort cost = layer.Distance[sector * MaxPortals + p];
                if (cost < bestCost) { bestCost = cost; best = p; }
            }
            return best;
        }

        /// <summary>The matching portal on the other side of a portal's edge, or -1.</summary>
        private int Matching(Layer layer, int sector, int portal)
        {
            SectorState state = layer.Sectors[sector];
            if (state.Matching[portal] != NotLookedUp)
            {
                int cachedNeighbour = NeighbourSector(sector, state.Portals[portal].Edge);
                SectorState cachedOther = cachedNeighbour >= 0 ? layer.Sectors[cachedNeighbour] : null;
                if (cachedOther != null && state.Matching[portal] < cachedOther.Portals.Length)
                    return state.Matching[portal];
            }

            int edge = state.Portals[portal].Edge;
            int neighbour = NeighbourSector(sector, edge);
            if (neighbour < 0) { state.Matching[portal] = -1; return -1; }
            Materialize(layer, neighbour);
            SectorState other = layer.Sectors[neighbour];
            int opposite = Opposite[edge];
            int from = state.Portals[portal].Start, to = from + state.Portals[portal].Length;
            int result = -1;
            for (int q = 0; q < other.Portals.Length; q++)
            {
                if (other.Portals[q].Edge != opposite) continue;
                int qFrom = other.Portals[q].Start, qTo = qFrom + other.Portals[q].Length;
                if (qFrom < to && from < qTo) { result = q; break; } // the runs overlap along the edge
            }
            state.Matching[portal] = result;
            return result;
        }

        private int NeighbourSector(int sector, int edge)
        {
            int sx = sector % sectorsX, sy = sector / sectorsX;
            switch (edge)
            {
                case West: return sx > 0 ? sector - 1 : -1;
                case East: return sx + 1 < sectorsX ? sector + 1 : -1;
                case South: return sy > 0 ? sector - sectorsX : -1;
                default: return sy + 1 < sectorsY ? sector + sectorsX : -1;
            }
        }

        private void HeapPush(Layer layer, int node)
        {
            List<int> heap = layer.Heap;
            heap.Add(node);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (layer.Distance[heap[parent]] <= layer.Distance[heap[i]]) break;
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                i = parent;
            }
        }

        private int HeapPop(Layer layer)
        {
            List<int> heap = layer.Heap;
            int top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, smallest = i;
                if (left < heap.Count && layer.Distance[heap[left]] < layer.Distance[heap[smallest]]) smallest = left;
                if (right < heap.Count && layer.Distance[heap[right]] < layer.Distance[heap[smallest]]) smallest = right;
                if (smallest == i) break;
                (heap[smallest], heap[i]) = (heap[i], heap[smallest]);
                i = smallest;
            }
            return top;
        }

        // ---- specs ---------------------------------------------------------------------------

        private SectorFieldSpec NewSpec(int sector) => new SectorFieldSpec
        {
            Sector = sector,
            ExitDirection = FlowDirections.None,
        };

        /// <summary>
        /// A route sector's field: seeded with its exit portal's cells at the portal's cost to the goal,
        /// which is the plan's seeding. The exit cells are also written with the crossing step, so a unit
        /// standing on them steps into the next sector instead of turning around inside this one.
        /// </summary>
        private SectorFieldSpec RouteSpec(Layer layer, int sector, int exit)
        {
            SectorState state = layer.Sectors[sector];
            ushort cost = layer.Distance[sector * MaxPortals + exit];
            SectorFieldSpec spec = NewSpec(sector);
            int offset = state.PortalOffsets[exit];
            for (int i = 0; i < state.Portals[exit].Length; i++)
            {
                int cell = state.PortalCells[offset + i];
                if (spec.GoalCells.Length >= spec.GoalCells.Capacity) break;
                spec.GoalCells.Add(cell);
                spec.GoalCosts.Add(cost);
                spec.ExitCells.Add(cell);
            }
            spec.ExitDirection = Crossing[state.Portals[exit].Edge];
            return spec;
        }

        /// <summary>Crop-local index of a cell in a sector.</summary>
        private int LocalCell(int cell, int sector)
        {
            int sx = sector % sectorsX, sy = sector / sectorsX;
            int x = cell % width - sx * SectorSize;
            int y = cell / width - sy * SectorSize;
            return y * SectorSize + x;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(SectorGraph));
        }
    }

    /// <summary>
    /// Builds one sector's local field: an integration field whose goals start at the given costs (0 for
    /// an order's goal cells, the exit portal's cost to the goal for a route sector), then the direction
    /// byte per cell, then the crossing step on the sector's exit cells. Dial's algorithm again, but the
    /// seed costs are not all zero, so the ring starts at the cheapest seed and seeds are pushed in cost
    /// order as the drain reaches them.
    /// </summary>
    [BurstCompile]
    public struct BuildSectorFieldJob : IJob
    {
        private const int Straight = 10, Diagonal = 14, Ring = 16;

        public int Width, Height;
        [ReadOnly] public NativeArray<byte> Tiles;
        public FixedList128Bytes<int> Goals;
        public FixedList128Bytes<ushort> GoalCosts;

        /// <summary>False to only fill <see cref="Cost"/> (the route search's seeding pass).</summary>
        public bool WriteDirection;

        /// <summary>Cells the crossing step is written on; ignored when <see cref="ExitDirection"/> is None.</summary>
        public FixedList128Bytes<int> ExitCells;
        public byte ExitDirection;

        public NativeArray<ushort> Cost;
        public NativeArray<byte> Direction;

        public void Execute()
        {
            int cells = Width * Height;
            for (int i = 0; i < cells; i++) Cost[i] = BuildIntegrationFieldJob.Unreachable;

            // The seeds, cheapest first: Dial's ring only drains correctly when every entry in a slot
            // has a cost at least one ring ahead of the drain, which seeded entries do not satisfy.
            int seedCount = Goals.Length;
            var seedCell = new NativeArray<int>(seedCount, Allocator.Temp);
            var seedCost = new NativeArray<ushort>(seedCount, Allocator.Temp);
            for (int i = 0; i < seedCount; i++)
            {
                seedCell[i] = Goals[i];
                seedCost[i] = GoalCosts[i];
            }
            for (int i = 1; i < seedCount; i++)
            {
                int cell = seedCell[i];
                ushort cost = seedCost[i];
                int j = i - 1;
                while (j >= 0 && seedCost[j] > cost)
                {
                    seedCell[j + 1] = seedCell[j];
                    seedCost[j + 1] = seedCost[j];
                    j--;
                }
                seedCell[j + 1] = cell;
                seedCost[j + 1] = cost;
            }

            var buckets = new NativeArray<UnsafeList<int>>(Ring, Allocator.Temp);
            for (int b = 0; b < Ring; b++) buckets[b] = new UnsafeList<int>(256, Allocator.Temp);
            int pending = 0, cursor = 0;
            int start = seedCount > 0 ? seedCost[0] : 0;

            for (int c = start; pending > 0 || cursor < seedCount; c++)
            {
                // Seeds whose cost is this one are pushed now, so the ring never holds an entry from
                // more than one ring ahead.
                for (int i = cursor; i < seedCount && seedCost[i] == c; i++)
                {
                    int cell = seedCell[i];
                    if (Tiles[cell] != 0) { cursor++; continue; }
                    if (Cost[cell] != BuildIntegrationFieldJob.Unreachable && Cost[cell] <= c) { cursor++; continue; }
                    Cost[cell] = (ushort)c;
                    Push(ref buckets, c & (Ring - 1), cell);
                    pending++;
                    cursor++;
                }

                int slot = c & (Ring - 1);
                UnsafeList<int> list = buckets[slot];
                for (int k = 0; k < list.Length; k++)
                {
                    int i = list[k];
                    pending--;
                    if (Cost[i] != c) continue; // stale entry, a cheaper route was found later
                    int x = i % Width, y = i / Width;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                        int n = ny * Width + nx;
                        if (Tiles[n] != 0) continue;
                        bool diagonal = dx != 0 && dy != 0;
                        if (diagonal && (Tiles[y * Width + nx] != 0 || Tiles[ny * Width + x] != 0)) continue;
                        int next = c + (diagonal ? Diagonal : Straight);
                        if (next >= BuildIntegrationFieldJob.Unreachable || next >= Cost[n]) continue;
                        Cost[n] = (ushort)next;
                        Push(ref buckets, next & (Ring - 1), n);
                        pending++;
                    }
                }
                list.Clear();
                buckets[slot] = list;
            }

            for (int b = 0; b < Ring; b++) buckets[b].Dispose();
            buckets.Dispose();
            seedCell.Dispose();
            seedCost.Dispose();

            if (!WriteDirection) return;

            for (int i = 0; i < cells; i++)
            {
                if (Tiles[i] != 0 || Cost[i] == BuildIntegrationFieldJob.Unreachable)
                {
                    Direction[i] = FlowDirections.None;
                    continue;
                }
                if (Cost[i] == 0)
                {
                    Direction[i] = FlowDirections.AtGoal;
                    continue;
                }
                int x = i % Width, y = i / Width;
                int best = int.MaxValue;
                byte bestDirection = FlowDirections.None;
                for (int d = 0; d < 8; d++)
                {
                    int dx = Dx(d), dy = Dy(d);
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                    int n = ny * Width + nx;
                    if (Tiles[n] != 0) continue;
                    int cost = Cost[n];
                    if (cost == BuildIntegrationFieldJob.Unreachable) continue;
                    if (dx != 0 && dy != 0 && (Tiles[y * Width + nx] != 0 || Tiles[ny * Width + x] != 0)) continue;
                    if (cost >= best) continue;
                    best = cost;
                    bestDirection = (byte)d;
                }
                Direction[i] = bestDirection;
            }

            if (ExitDirection == FlowDirections.None) return;
            for (int i = 0; i < ExitCells.Length; i++) Direction[ExitCells[i]] = ExitDirection;
        }

        private static void Push(ref NativeArray<UnsafeList<int>> buckets, int slot, int cell)
        {
            UnsafeList<int> list = buckets[slot];
            list.Add(cell);
            buckets[slot] = list;
        }

        private static int Dx(int d) => d == 0 || d == 1 || d == 7 ? 1 : d == 3 || d == 4 || d == 5 ? -1 : 0;

        private static int Dy(int d) => d == 1 || d == 2 || d == 3 ? 1 : d == 5 || d == 6 || d == 7 ? -1 : 0;
    }
}
