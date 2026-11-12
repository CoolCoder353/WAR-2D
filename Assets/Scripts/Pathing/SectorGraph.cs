using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Pathing
{
    /// <summary>One sector's local field to build: its goals, what each is seeded with, and its exit.</summary>
    public struct SectorFieldSpec
    {
        /// <summary>Sector index, <c>y * SectorsX + x</c>.</summary>
        public int Sector;

        /// <summary>Crop-local cell indices of the field's goals.</summary>
        public FixedList512Bytes<int> GoalCells;

        /// <summary>The cost-to-goal each goal cell starts at (0 for an order's own goal cells).</summary>
        public FixedList512Bytes<ushort> GoalCosts;

        /// <summary>
        /// Cells of the sector's exit portals, each written with its crossing step (<see cref="ExitDirections"/>).
        /// A route has one exit per pocket of floor it passes through: a cave sector can hold several.
        /// </summary>
        public FixedList512Bytes<int> ExitCells;

        /// <summary>The crossing step of each exit cell, parallel to <see cref="ExitCells"/>.</summary>
        public FixedList128Bytes<byte> ExitDirections;

        /// <summary>The first exit's crossing step, or <see cref="FlowDirections.None"/> for a sector the route ends in.</summary>
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

        /// <summary>Most Stop cells one walled-off region gets: a sector's field has room for about 127 goal and exit cells, shared with its other pockets.</summary>
        private const int MaxStopCells = 8;

        /// <summary>Cells a sector spans on each side.</summary>
        public const int SectorCellsPerSide = 32; // at one tile per cell

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
            public NativeArray<byte> Crop;
            public short[] PocketOf;     // cell -> its pocket of connected floor; -1 on rock
            public short[] PortalPocket; // portal -> the pocket its cells are in
            public int ExitPockets;      // pockets with a portal: the only ones a route can leave
        }

        private sealed class Layer
        {
            public NativeArray<byte> Grid;
            public SectorState[] Sectors;
            public NativeArray<ushort> Distance; // node = sector * MaxPortals + portal
            public NativeArray<bool> Settled;
            public NativeList<int> Heap;         // one entry per relaxation, so it can repeat a node
            // What the search job reads (ExpandJob): a copy of each built sector's portals, and the rest of a sector's state.
            public NativeArray<int> PortalCount;          // sector -> its portals, -1 until the sector is built
            public NativeArray<Portal> Portals;           // node
            public NativeArray<short> PortalPocket;       // node -> the pocket its cells are in
            public NativeArray<int> Matching;             // node -> matching portal across the edge; -2 not looked up yet
            public NativeArray<UnsafeList<ushort>> Costs; // sector -> portals^2 costs, Infinite when the sector cannot connect them; taken from its job when a search asks
            public UnsafeList<ushort>[] CostsToCome;      // sector -> the table its job fills (ScheduleCosts), until a search takes it
            public JobHandle[] CostJobs;                  // sector -> that job
            public readonly List<HashSet<int>> Regions = new List<HashSet<int>>(); // regions walked by RegionOf; dropped by a terrain change
            public readonly Dictionary<int, HashSet<int>> RegionByPocket = new Dictionary<int, HashSet<int>>(); // each walked pocket's region, so a lookup needn't scan them all
            public readonly Dictionary<int, List<HashSet<int>>> RegionsBySector = new Dictionary<int, List<HashSet<int>>>(); // the regions with a pocket in each sector
            public readonly Dictionary<int, SectorState> Suspect = new Dictionary<int, SectorState>(); // dropped sectors and their state when the regions were walked, checked before the next region use
        }

        private readonly int width, height, sectorsX, sectorsY, sectorCount;
        private readonly int cellSize, cellWidth, cellHeight, sectorCells;
        private readonly Allocator allocator;
        private readonly NativeArray<byte> tiles;      // the map's tile grid, not owned
        private readonly NativeArray<byte> smallCells; // the small class's cell grid, owned
        private readonly NativeArray<byte> largeCells; // the large class's cell grid, owned
        private readonly NativeArray<byte> clearance;
        private readonly Layer[] layers = new Layer[FlowSizeClass.Count];
        private NativeHashSet<int> searchWaiting; // the search job's copy of the pockets it runs for
        private NativeArray<int> searchState;     // ExpandJob.State

        private bool disposed;

        /// <summary>
        /// Builds the graph for both size classes. <paramref name="map"/>'s tile array stays shared: the
        /// caller flips tiles in it and calls <see cref="Invalidate"/>, which also refreshes the
        /// clearance window and the large class's filtered grid.
        /// </summary>
        public SectorGraph(in PathMap map, Allocator allocator, int cellSize = 1)
        {
            if (cellSize < 1 || SectorSize % cellSize != 0)
                throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, $"cell size must divide {SectorSize}");
            width = map.Width;
            height = map.Height;
            sectorsX = (width + SectorSize - 1) / SectorSize;
            sectorsY = (height + SectorSize - 1) / SectorSize;
            sectorCount = sectorsX * sectorsY;
            this.cellSize = cellSize;
            cellWidth = (width + cellSize - 1) / cellSize;
            cellHeight = (height + cellSize - 1) / cellSize;
            sectorCells = SectorSize / cellSize;
            this.allocator = allocator;
            tiles = map.Tiles;

            clearance = new NativeArray<byte>(width * height, allocator);
            smallCells = new NativeArray<byte>(cellWidth * cellHeight, allocator);
            largeCells = new NativeArray<byte>(cellWidth * cellHeight, allocator);
            new ClearanceJob { Width = width, Height = height, Tiles = tiles, Clearance = clearance }.Run();
            FillCells(smallCells, tiles, 1, false, int2.zero, new int2(cellWidth - 1, cellHeight - 1), out _, out _);
            FillCells(largeCells, tiles, FlowSizeClass.MinClearance(FlowSizeClass.Large), true,
                int2.zero, new int2(cellWidth - 1, cellHeight - 1), out _, out _);

            layers[FlowSizeClass.Small] = NewLayer(smallCells);
            layers[FlowSizeClass.Large] = NewLayer(largeCells);
            searchWaiting = new NativeHashSet<int>(64, allocator);
            searchState = new NativeArray<int>(3, allocator);
        }

        /// <summary>
        /// Writes the cell grid over a cell-coordinate rect: a cell is blocked when any tile it covers
        /// blocks for the class (or falls outside the map), which is the conservative half-resolution
        /// rule. With one tile per cell this is the tile grid itself. Reports the bounds of the cells whose value
        /// changed (<paramref name="changedFrom"/> above <paramref name="changedTo"/> when none did).
        /// </summary>
        private void FillCells(
            NativeArray<byte> into, NativeArray<byte> tileGrid, byte minClearance, bool large,
            int2 from, int2 to, out int2 changedFrom, out int2 changedTo)
        {
            int x0 = math.max(0, from.x), y0 = math.max(0, from.y);
            int x1 = math.min(cellWidth - 1, to.x), y1 = math.min(cellHeight - 1, to.y);
            changedFrom = new int2(int.MaxValue);
            changedTo = new int2(int.MinValue);
            for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                byte value = PathMap.Floor;
                for (int ty = 0; ty < cellSize && value == PathMap.Floor; ty++)
                for (int tx = 0; tx < cellSize; tx++)
                {
                    int x = cx * cellSize + tx, y = cy * cellSize + ty;
                    if (x >= width || y >= height) { value = PathMap.Rock; break; }
                    int tile = y * width + x;
                    if (tileGrid[tile] != PathMap.Floor) { value = PathMap.Rock; break; }
                    if (large && clearance[tile] < minClearance) { value = PathMap.Rock; break; }
                }
                if (into[cy * cellWidth + cx] == value) continue;
                into[cy * cellWidth + cx] = value;
                changedFrom = math.min(changedFrom, new int2(cx, cy));
                changedTo = math.max(changedTo, new int2(cx, cy));
            }
        }

        /// <summary>
        /// Start pockets the last <see cref="BuildRoute"/> found no way to the goal from, so their units
        /// head for the nearest floor to the goal they can reach instead (<see cref="FlowDirections.Stop"/>).
        /// Non-zero means the goal is walled off from them, or the class grid severed them.
        /// </summary>
        public int UnreachableStartSectors { get; private set; }

        /// <summary>
        /// One number per pocket of connected floor per sector: the pocket a unit of the class at <paramref name="tile"/>
        /// stands in, or the nearest one within two cells (a large unit's tile can be too narrow for its own grid), or -1.
        /// A terrain change can renumber pockets, so don't keep the number across one.
        /// </summary>
        public int PocketOf(int sizeClass, int2 tile)
        {
            ThrowIfDisposed();
            Layer layer = layers[sizeClass];
            int2 cell = tile / cellSize;
            for (int ring = 0; ring <= 2; ring++)
            for (int i = 0; i < math.max(1, 8 * ring); i++)
            {
                int2 at = FlowGoals.RingTile(cell, ring, i);
                if ((uint)at.x >= (uint)cellWidth || (uint)at.y >= (uint)cellHeight) continue;
                int pocket = PocketAtCell(layer, at.y * cellWidth + at.x);
                if (pocket >= 0) return pocket;
            }
            return -1;
        }

        /// <summary>True when a unit of the class at <paramref name="from"/> can walk to <paramref name="to"/>.</summary>
        public bool Reaches(int sizeClass, int2 from, int2 to) => Connects(sizeClass, PocketOf(sizeClass, from), PocketOf(sizeClass, to));

        /// <summary>True when two pockets (<see cref="PocketOf"/>, -1 for none) of the class are joined by floor.</summary>
        public bool Connects(int sizeClass, int a, int b) => a >= 0 && b >= 0 && (a == b || RegionOf(layers[sizeClass], a).Contains(b));

        /// <summary>Clearance per tile, capped at <see cref="ClearanceJob.MaxClearance"/>.</summary>
        public NativeArray<byte> Clearance => clearance;

        /// <summary>Tiles per cell: 1, or 2 for the half-resolution design.</summary>
        public int CellSize => cellSize;

        /// <summary>Cells across the cell grid.</summary>
        public int CellWidth => cellWidth;

        /// <summary>Cells down the cell grid.</summary>
        public int CellHeight => cellHeight;

        /// <summary>Cells on a side of a sector.</summary>
        public int SectorCellsSide => sectorCells;

        /// <summary>Cells in a sector's local field.</summary>
        public int SectorCells => sectorCells * sectorCells;

        /// <summary>Cell index of a tile: the cell that covers it.</summary>
        public int CellOf(int2 tile) => tile.y / cellSize * cellWidth + tile.x / cellSize;

        /// <summary>The top-left tile of a cell, as a map cell index.</summary>
        public int TileOfCell(int cell) =>
            cell / cellWidth * cellSize * width + cell % cellWidth * cellSize;

        /// <summary>The small size class's cell grid.</summary>
        public NativeArray<byte> SmallCells => smallCells;

        /// <summary>The large size class's cell grid.</summary>
        public NativeArray<byte> LargeCells => largeCells;

        /// <summary>The map's width in tiles.</summary>
        public int Width => width;

        /// <summary>The map's height in tiles.</summary>
        public int Height => height;

        /// <summary>The map's own tile grid.</summary>
        public NativeArray<byte> Tiles => tiles;

        /// <summary>The cell grid a size class paths on.</summary>
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

        /// <summary>Sector index of a cell of the cell grid.</summary>
        public int SectorOfCell(int cell) =>
            cell / cellWidth / sectorCells * sectorsX + cell % cellWidth / sectorCells;

        /// <summary>Bytes the graph itself holds: the large grid, the sector crops and cost tables.</summary>
        public long GraphBytes
        {
            get
            {
                long bytes = width * height + cellWidth * cellHeight * 2L; // tiles, clearance, both cell grids
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
            return layer.Costs[sector][from * layer.PortalCount[sector] + to];
        }

        /// <summary>
        /// The local field specs for one order: a field for each goal sector, seeded with the order's
        /// goal cells at cost 0, and a field for every sector on the route from a start sector to a goal
        /// sector, seeded with that sector's exit portal at its cost to the goal.
        /// </summary>
        public SectorFieldSpec[] BuildRoute(
            int sizeClass, NativeArray<int> goalCells, NativeArray<int> startCells, out int coveredSectors, int2? aim = null)
        {
            ThrowIfDisposed();
            Layer layer = layers[sizeClass];
            var specs = new List<SectorFieldSpec>();
            var covered = new HashSet<int>();

            // 1. The goal sectors: one spec each, seeded with the goal cells that fall inside them.
            var goalSpecs = new Dictionary<int, SectorFieldSpec>();
            var goalCellsBySector = new Dictionary<int, List<int>>();
            var goalPockets = new HashSet<int>();
            for (int i = 0; i < goalCells.Length; i++)
            {
                // The caller passes map cell indices (tiles); routes live in cell-grid coordinates.
                int cell = CellOf(new int2(goalCells[i] % width, goalCells[i] / width));
                int sector = SectorOfCell(cell);
                if (!goalCellsBySector.TryGetValue(sector, out List<int> list))
                {
                    list = new List<int>(8);
                    goalCellsBySector[sector] = list;
                    goalSpecs[sector] = NewSpec(sector);
                }
                list.Add(LocalCell(cell, sector));
                int goalPocket = PocketAtCell(layer, cell);
                if (goalPocket >= 0) goalPockets.Add(goalPocket);
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
            var goalSeeds = new Dictionary<int, ushort[]>();
            foreach (KeyValuePair<int, List<int>> pair in goalCellsBySector)
            {
                // BuildInSectorField returns, per portal of the sector, the cost from its cells to the
                // goal cells: exactly the seed each portal's node starts at.
                ushort[] toGoals = BuildInSectorField(layer, pair.Key, pair.Value);
                goalSeeds[pair.Key] = toGoals;
                for (int p = 0; p < toGoals.Length; p++) Seed(layer, pair.Key, p, toGoals[p]);
            }

            // 3. Expand the abstract graph until every start pocket can leave. A sector can hold pockets of floor walled
            //    off from each other, and a pocket with the goal in it needs no exit. A pocket no way leads from gets its
            //    own end to walk to: the floor of its region nearest the goal, marked Stop.
            List<int> startPockets = StartPockets(sizeClass, startCells);
            var waiting = new HashSet<int>(startPockets);
            waiting.ExceptWith(goalPockets);
            ExpandUntilSettled(layer, waiting);
            UnreachableStartSectors = waiting.Count;
            if (waiting.Count > 0 && (aim.HasValue || goalCells.Length > 0))
            {
                int2 target = (aim ?? new int2(goalCells[0] % width, goalCells[0] / width)) / cellSize;
                foreach (int pocket in startPockets)
                {
                    if (!waiting.Contains(pocket)) continue;
                    AddStop(layer, NearestCellOfRegion(layer, pocket, target), math.clamp(goalCells.Length, 1, MaxStopCells), goalSpecs, goalCellsBySector, goalSeeds, goalPockets, waiting);
                    ExpandUntilSettled(layer, waiting);
                }
            }

            // 4. Walk each start pocket's route to a goal sector, collecting the fields it needs. A sector's field gets
            //    one exit per pocket of floor the route passes through: past the start the walk leaves by the exit it can
            //    reach from where it came in, and a route that comes back through a sector in another pocket adds that
            //    pocket's exit to the same field. A (sector, exit) pair already walked ends the walk.
            var routeSpecs = new Dictionary<int, SectorFieldSpec>();
            var walked = new HashSet<int>();
            foreach (int start in startPockets)
            {
                if (goalPockets.Contains(start)) continue;
                int sector = start / SectorCells;
                Materialize(layer, sector);
                int exit = BestCrossingOf(layer, sector, start % SectorCells);
                while (exit >= 0 && walked.Add(sector * MaxPortals + exit))
                {
                    if (goalSpecs.ContainsKey(sector)) goalSpecs[sector] = WithExit(goalSpecs[sector], layer, sector, exit);
                    else routeSpecs[sector] = WithExit(routeSpecs.TryGetValue(sector, out SectorFieldSpec spec) ? spec : NewSpec(sector), layer, sector, exit);
                    int neighbour = NeighbourSector(sector, layer.Sectors[sector].Portals[exit].Edge);
                    if (neighbour < 0) break;
                    int entry = Matching(layer, sector, exit);
                    sector = neighbour;
                    Materialize(layer, sector);
                    // The goal sector ends the walk unless the route came in through a pocket its goal cells aren't in.
                    if (goalSpecs.ContainsKey(sector) && goalSeeds[sector][entry] < Infinite) break;
                    exit = BestExitFrom(layer, sector, entry);
                }
            }

            foreach (KeyValuePair<int, SectorFieldSpec> pair in routeSpecs)
            {
                covered.Add(pair.Key);
                specs.Add(pair.Value);
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
        /// Refreshes the clearance and the class grids around the tile, and drops the portals, crops and cost
        /// tables of the sectors a changed cell can alter: per class, a sector reads its own cells and the one
        /// row of cells across each edge (its portals), so only the sectors within one cell of a changed cell go
        /// (most often one, never more than the tile's sector and its eight neighbours). Everything else in the
        /// graph is left alone.
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
            // A cell covers cellSize tiles, so the cells that may have changed are the window's own
            // cells plus one on each side of them.
            int2 cellFrom = new int2(origin.x / cellSize - 1, origin.y / cellSize - 1);
            int2 cellTo = new int2((origin.x + size.x - 1) / cellSize + 1, (origin.y + size.y - 1) / cellSize + 1);
            FillCells(smallCells, tiles, 1, false, cellFrom, cellTo, out int2 from, out int2 to);
            DropSectorsNear(layers[FlowSizeClass.Small], from, to);
            FillCells(largeCells, tiles, FlowSizeClass.MinClearance(FlowSizeClass.Large), true, cellFrom, cellTo, out from, out to);
            DropSectorsNear(layers[FlowSizeClass.Large], from, to);
        }

        /// <summary>Drops the layer's sectors that hold a cell within one cell of the changed cells' bounds (none when no cell changed).</summary>
        private void DropSectorsNear(Layer layer, int2 changedFrom, int2 changedTo)
        {
            if (changedFrom.x > changedTo.x) return;
            int2 low = math.max(changedFrom - 1, int2.zero) / sectorCells;
            int2 high = math.min(changedTo + 1, new int2(cellWidth - 1, cellHeight - 1)) / sectorCells;
            for (int y = low.y; y <= high.y; y++)
            for (int x = low.x; x <= high.x; x++)
                DropSector(layer, y * sectorsX + x);
        }

        /// <summary>Frees every sector crop.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (Layer layer in layers)
            {
                if (layer == null) continue;
                for (int sector = 0; sector < sectorCount; sector++) DropCostsToCome(layer, sector);
                foreach (SectorState state in layer.Sectors)
                    if (state != null && state.Crop.IsCreated) state.Crop.Dispose();
                for (int sector = 0; sector < sectorCount; sector++)
                    if (layer.Costs[sector].IsCreated) layer.Costs[sector].Dispose();
                layer.Distance.Dispose();
                layer.Settled.Dispose();
                layer.Heap.Dispose();
                layer.PortalCount.Dispose();
                layer.Portals.Dispose();
                layer.PortalPocket.Dispose();
                layer.Matching.Dispose();
                layer.Costs.Dispose();
            }
            searchWaiting.Dispose();
            searchState.Dispose();
            clearance.Dispose();
            smallCells.Dispose();
            largeCells.Dispose();
        }

        // ---- sector state --------------------------------------------------------------------

        private Layer NewLayer(NativeArray<byte> grid)
        {
            int nodes = sectorCount * MaxPortals;
            var layer = new Layer
            {
                Grid = grid,
                Sectors = new SectorState[sectorCount],
                Distance = new NativeArray<ushort>(nodes, allocator),
                Settled = new NativeArray<bool>(nodes, allocator),
                Heap = new NativeList<int>(sectorCount, allocator),
                PortalCount = new NativeArray<int>(sectorCount, allocator),
                Portals = new NativeArray<Portal>(nodes, allocator),
                PortalPocket = new NativeArray<short>(nodes, allocator),
                Matching = new NativeArray<int>(nodes, allocator),
                Costs = new NativeArray<UnsafeList<ushort>>(sectorCount, allocator),
                CostsToCome = new UnsafeList<ushort>[sectorCount],
                CostJobs = new JobHandle[sectorCount],
            };
            for (int sector = 0; sector < sectorCount; sector++) layer.PortalCount[sector] = -1;
            return layer;
        }

        /// <summary>Counts terrain changes, so a caller keeping pocket numbers (<see cref="PocketOf"/>) knows when they went stale.</summary>
        public int TerrainVersion { get; private set; }

        private void DropSector(Layer layer, int sector)
        {
            // The regions stay until the next region use checks whether this sector's pockets and portals really changed
            // (CheckSuspects): most terrain changes in a fight don't join or split anything (perf gate 2026-11-06). With no
            // state to compare against, the regions near it go now.
            if (layer.Regions.Count > 0 && !layer.Suspect.ContainsKey(sector))
            {
                if (layer.Sectors[sector] != null) layer.Suspect[sector] = layer.Sectors[sector];
                else DropRegionsNear(layer, sector);
            }
            TerrainVersion++;
            // A neighbour's matching cache holds portal indices into this sector, so it goes stale with
            // it: forget those caches too.
            for (int edge = West; edge <= North; edge++)
            {
                int neighbour = NeighbourSector(sector, edge);
                if (neighbour < 0) continue;
                for (int i = 0; i < layer.PortalCount[neighbour]; i++) layer.Matching[neighbour * MaxPortals + i] = NotLookedUp;
            }

            SectorState state = layer.Sectors[sector];
            if (state == null) return;
            DropCostsToCome(layer, sector);
            if (state.Crop.IsCreated) state.Crop.Dispose();
            if (layer.Costs[sector].IsCreated) layer.Costs[sector].Dispose();
            layer.Costs[sector] = default;
            layer.PortalCount[sector] = -1;
            layer.Sectors[sector] = null;
        }

        /// <summary>
        /// Forgets the regions a change to <paramref name="sector"/> can split or join: those with a pocket in it or in a sector beside it
        /// (two regions can only join through its floor, from its edges). The rest of the map's regions stay, so a wall going up or down
        /// doesn't make the next reach check flood the whole map again (perf gate 2026-11-06).
        /// </summary>
        private void DropRegionsNear(Layer layer, int sector)
        {
            if (layer.Regions.Count == 0) return;
            dropRegions.Clear();
            for (int edge = -1; edge <= North; edge++)
            {
                int at = edge < 0 ? sector : NeighbourSector(sector, edge);
                if (at >= 0 && layer.RegionsBySector.TryGetValue(at, out List<HashSet<int>> here)) dropRegions.UnionWith(here);
            }
            foreach (HashSet<int> region in dropRegions)
            {
                layer.Regions.Remove(region);
                foreach (int member in region)
                {
                    layer.RegionByPocket.Remove(member);
                    if (layer.RegionsBySector.TryGetValue(member / SectorCells, out List<HashSet<int>> list))
                    {
                        list.Remove(region);
                        if (list.Count == 0) layer.RegionsBySector.Remove(member / SectorCells);
                    }
                }
            }
        }

        private readonly HashSet<HashSet<int>> dropRegions = new HashSet<HashSet<int>>();
        private readonly List<int> suspectSectors = new List<int>();

        /// <summary>
        /// Rebuilds each sector dropped since the regions were walked and drops the regions near it only if its pockets or portals
        /// changed (<see cref="SamePockets"/>). The regions then match a fresh walk exactly.
        /// </summary>
        private void CheckSuspects(Layer layer)
        {
            if (layer.Suspect.Count == 0) return;
            suspectSectors.Clear();
            suspectSectors.AddRange(layer.Suspect.Keys);
            foreach (int sector in suspectSectors)
            {
                Materialize(layer, sector);
                if (!SamePockets(layer.Suspect[sector], layer.Sectors[sector])) DropRegionsNear(layer, sector);
            }
            layer.Suspect.Clear();
        }

        /// <summary>
        /// True when a sector rebuilt after a terrain change connects exactly as before: the same portals in the same pockets, the
        /// same number of pockets, every floor cell that stayed floor in the same pocket, and new floor only in pockets that kept
        /// an old cell. Then no region can have gained or lost a pocket or a crossing through it.
        /// </summary>
        private bool SamePockets(SectorState was, SectorState now)
        {
            if (was.Portals.Length != now.Portals.Length) return false;
            for (int p = 0; p < was.Portals.Length; p++)
            {
                Portal a = was.Portals[p], b = now.Portals[p];
                if (a.Edge != b.Edge || a.Start != b.Start || a.Length != b.Length || was.PortalPocket[p] != now.PortalPocket[p]) return false;
            }
            int count = 0, countNow = 0;
            for (int i = 0; i < was.PocketOf.Length; i++)
            {
                count = math.max(count, was.PocketOf[i] + 1);
                countNow = math.max(countNow, now.PocketOf[i] + 1);
            }
            if (count != countNow) return false;
            // Reused: a wall line checks a suspect sector per tile it changed, and this ran once per check.
            if (keptScratch.Length < count) keptScratch = new bool[math.max(count, keptScratch.Length * 2)];
            bool[] kept = keptScratch;
            System.Array.Clear(kept, 0, count);
            for (int i = 0; i < was.PocketOf.Length; i++)
            {
                short o = was.PocketOf[i], n = now.PocketOf[i];
                if (o >= 0 && n >= 0)
                {
                    if (o != n) return false;
                    kept[n] = true;
                }
            }
            for (int i = 0; i < was.PocketOf.Length; i++)
                if (was.PocketOf[i] < 0 && now.PocketOf[i] >= 0 && !kept[now.PocketOf[i]]) return false;
            return true;
        }

        private bool[] keptScratch = new bool[16];

        private void Materialize(Layer layer, int sector)
        {
            if (layer.Sectors[sector] != null) return;

            int sx = sector % sectorsX, sy = sector / sectorsX;
            int x0 = sx * sectorCells, y0 = sy * sectorCells;
            int x1 = math.min(x0 + sectorCells - 1, cellWidth - 1), y1 = math.min(y0 + sectorCells - 1, cellHeight - 1);

            var crop = new NativeArray<byte>(SectorCells, Allocator.Persistent);
            for (int i = 0; i < SectorCells; i++) crop[i] = PathMap.Rock; // outside a partial sector: blocked
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                crop[(y - y0) * sectorCells + (x - x0)] = layer.Grid[y * cellWidth + x];

            var portals = new List<Portal>(8);
            var cells = new List<int>(sectorCells * 4);
            var offsets = new List<int>(9) { 0 };
            if (x0 > 0) AddRuns(layer.Grid, portals, cells, offsets, West, x0, x0 - 1, y0, y1, alongY: true, x0, y0);
            if (x1 + 1 < cellWidth) AddRuns(layer.Grid, portals, cells, offsets, East, x1, x1 + 1, y0, y1, alongY: true, x0, y0);
            if (y0 > 0) AddRuns(layer.Grid, portals, cells, offsets, South, y0, y0 - 1, x0, x1, alongY: false, x0, y0);
            if (y1 + 1 < cellHeight) AddRuns(layer.Grid, portals, cells, offsets, North, y1, y1 + 1, x0, x1, alongY: false, x0, y0);

            short[] pocketOf = LabelPockets(crop);
            var portalPocket = new short[portals.Count];
            for (int p = 0; p < portalPocket.Length; p++)
            {
                portalPocket[p] = pocketOf[cells[offsets[p]]];
                int node = sector * MaxPortals + p;
                layer.Portals[node] = portals[p];
                layer.PortalPocket[node] = portalPocket[p];
                layer.Matching[node] = NotLookedUp;
            }
            layer.PortalCount[sector] = portals.Count;
            layer.Sectors[sector] = new SectorState
            {
                Crop = crop,
                Portals = portals.ToArray(),
                PortalCells = cells.ToArray(),
                PortalOffsets = offsets.ToArray(),
                PocketOf = pocketOf,
                PortalPocket = portalPocket,
                ExitPockets = new HashSet<short>(portalPocket).Count,
            };
            ScheduleCosts(layer, sector);
        }

        /// <summary>
        /// Numbers a crop's pockets of floor. Straight steps are enough to join them: a diagonal step needs both of the
        /// straight cells beside it open, so the fields can't cross between two pockets either.
        /// </summary>
        private short[] LabelPockets(NativeArray<byte> crop)
        {
            var labels = new short[crop.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = -1;
            var open = new Stack<int>();
            short next = 0;
            for (int first = 0; first < labels.Length; first++)
            {
                if (labels[first] >= 0 || crop[first] != PathMap.Floor) continue;
                labels[first] = next;
                open.Push(first);
                while (open.Count > 0)
                {
                    int cell = open.Pop();
                    int x = cell % sectorCells, y = cell / sectorCells;
                    if (x > 0) Join(cell - 1);
                    if (x < sectorCells - 1) Join(cell + 1);
                    if (y > 0) Join(cell - sectorCells);
                    if (y < sectorCells - 1) Join(cell + sectorCells);
                }
                next++;
            }
            return labels;

            void Join(int cell)
            {
                if (labels[cell] >= 0 || crop[cell] != PathMap.Floor) return;
                labels[cell] = next;
                open.Push(cell);
            }
        }

        /// <summary>The pocket a cell of the cell grid is in, as <see cref="PocketOf"/> numbers it, or -1 on rock.</summary>
        private int PocketAtCell(Layer layer, int cell)
        {
            int sector = SectorOfCell(cell);
            Materialize(layer, sector);
            short pocket = layer.Sectors[sector].PocketOf[LocalCell(cell, sector)];
            return pocket < 0 ? -1 : sector * SectorCells + pocket;
        }

        /// <summary>
        /// Every pocket a unit in <paramref name="pocket"/> can walk to, sector by sector through the portals. Kept until a
        /// terrain change, because nearly every unit stands in the same big region and work orders ask for it often. Every region
        /// walked is kept, so asking about a walled-off pocket doesn't cost the next unit in the main region a new walk.
        /// </summary>
        private HashSet<int> RegionOf(Layer layer, int pocket)
        {
            CheckSuspects(layer);
            if (layer.RegionByPocket.TryGetValue(pocket, out HashSet<int> known)) return known;
            var region = new HashSet<int> { pocket };
            var open = new Stack<int>();
            open.Push(pocket);
            while (open.Count > 0)
            {
                int key = open.Pop();
                int sector = key / SectorCells;
                Materialize(layer, sector);
                SectorState state = layer.Sectors[sector];
                for (int p = 0; p < state.Portals.Length; p++)
                {
                    if (state.PortalPocket[p] != key % SectorCells) continue;
                    int matching = Matching(layer, sector, p);
                    int neighbour = matching >= 0 ? NeighbourSector(sector, state.Portals[p].Edge) : -1;
                    if (neighbour < 0) continue;
                    int across = neighbour * SectorCells + layer.Sectors[neighbour].PortalPocket[matching];
                    if (region.Add(across)) open.Push(across);
                }
            }
            layer.Regions.Add(region);
            foreach (int member in region)
            {
                layer.RegionByPocket[member] = region;
                int sector = member / SectorCells;
                if (!layer.RegionsBySector.TryGetValue(sector, out List<HashSet<int>> list)) layer.RegionsBySector[sector] = list = new List<HashSet<int>>(1);
                if (!list.Contains(region)) list.Add(region);
            }
            return region;
        }

        /// <summary>The floor cell of a pocket's region nearest <paramref name="target"/> (a cell of the cell grid, as the result is).</summary>
        private int NearestCellOfRegion(Layer layer, int pocket, int2 target)
        {
            int best = -1;
            long bestDistance = long.MaxValue;
            foreach (int key in RegionOf(layer, pocket))
            {
                int sector = key / SectorCells;
                int2 origin = new int2(sector % sectorsX, sector / sectorsX) * sectorCells;
                int2 inside = math.clamp(target, origin, origin + sectorCells - 1) - target;
                if ((long)inside.x * inside.x + (long)inside.y * inside.y >= bestDistance) continue; // nothing here can be nearer
                short[] labels = layer.Sectors[sector].PocketOf;
                for (int local = 0; local < labels.Length; local++)
                {
                    if (labels[local] != key % SectorCells) continue;
                    int2 at = origin + new int2(local % sectorCells, local / sectorCells) - target;
                    long distance = (long)at.x * at.x + (long)at.y * at.y;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = (origin.y + local / sectorCells) * cellWidth + origin.x + local % sectorCells;
                }
            }
            return best;
        }

        /// <summary>
        /// Gives a region the goal is walled off from its own end: Stop cells around <paramref name="cell"/>, seeded like goal
        /// cells. Regions don't connect, so the search from them can't disturb what it has already settled.
        /// </summary>
        private void AddStop(
            Layer layer, int cell, int spread, Dictionary<int, SectorFieldSpec> goalSpecs, Dictionary<int, List<int>> goalCellsBySector,
            Dictionary<int, ushort[]> goalSeeds, HashSet<int> goalPockets, HashSet<int> waiting)
        {
            NativeArray<int> around = FlowGoals.Around(new int2(cell % cellWidth, cell / cellWidth), layer.Grid, cellWidth, cellHeight, Allocator.Temp, spread);
            var sectors = new HashSet<int>();
            for (int i = 0; i < around.Length; i++)
            {
                int sector = SectorOfCell(around[i]), local = LocalCell(around[i], sector);
                if (!goalCellsBySector.TryGetValue(sector, out List<int> list))
                {
                    goalCellsBySector[sector] = list = new List<int>(8);
                    goalSpecs[sector] = NewSpec(sector);
                }
                SectorFieldSpec spec = goalSpecs[sector];
                if (spec.GoalCells.Length >= spec.GoalCells.Capacity || spec.ExitDirections.Length >= spec.ExitDirections.Capacity) continue;
                list.Add(local);
                spec.GoalCells.Add(local);
                spec.GoalCosts.Add(0);
                spec.ExitCells.Add(local);
                spec.ExitDirections.Add(FlowDirections.Stop);
                goalSpecs[sector] = spec;
                sectors.Add(sector);
                int pocket = PocketAtCell(layer, around[i]);
                goalPockets.Add(pocket);
                waiting.Remove(pocket);
            }
            around.Dispose();
            foreach (int sector in sectors)
            {
                ushort[] toGoals = BuildInSectorField(layer, sector, goalCellsBySector[sector]);
                goalSeeds[sector] = toGoals;
                for (int p = 0; p < toGoals.Length; p++) Seed(layer, sector, p, toGoals[p]);
            }
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
                    int edgeCell = alongY ? along * cellWidth + edgeLine : edgeLine * cellWidth + along;
                    int acrossCell = alongY ? along * cellWidth + acrossLine : acrossLine * cellWidth + along;
                    open = grid[edgeCell] == PathMap.Floor && grid[acrossCell] == PathMap.Floor;
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
                        cells.Add((py - y0) * sectorCells + (px - x0));
                    }
                    offsets.Add(cells.Count);
                }
                runStart = -1;
            }
            return cells.Count;
        }

        /// <summary>
        /// Schedules a newly built sector's cost table as a job. A search needs the table of every sector it crosses,
        /// and terrain changes drop a few sectors each tick: built when the search asked, their tables cost its tick about
        /// 3 ms of main thread (100,000-unit benchmark, 2026-11-11). A sector is most often built ticks before a search
        /// crosses it (by a pocket lookup), so the table is ready by then.
        /// </summary>
        private void ScheduleCosts(Layer layer, int sector)
        {
            SectorState state = layer.Sectors[sector];
            int n = state.Portals.Length;
            var costs = new UnsafeList<ushort>(math.max(1, n * n), allocator);
            costs.Resize(math.max(1, n * n));
            costs[0] = Infinite;
            if (n == 0)
            {
                layer.Costs[sector] = costs;
                return;
            }
            var centres = new NativeArray<int>(n, Allocator.Persistent); // the job frees it
            for (int p = 0; p < n; p++) centres[p] = CentreCell(state, p);
            layer.CostsToCome[sector] = costs;
            layer.CostJobs[sector] = new PortalCostsJob { Side = sectorCells, Crop = state.Crop, Centres = centres, Costs = costs }.Schedule();
        }

        /// <summary>Builds the sector if it is not, and takes its cost table from its job (a wait only when the job has not finished).</summary>
        private void EnsureCosts(Layer layer, int sector)
        {
            Materialize(layer, sector);
            if (layer.Costs[sector].IsCreated) return;
            layer.CostJobs[sector].Complete();
            layer.Costs[sector] = layer.CostsToCome[sector];
            layer.CostsToCome[sector] = default;
        }

        /// <summary>Waits for a sector's cost table job when no search took its table, and frees the table. The job reads the sector's crop.</summary>
        private static void DropCostsToCome(Layer layer, int sector)
        {
            if (!layer.CostsToCome[sector].IsCreated) return;
            layer.CostJobs[sector].Complete();
            layer.CostsToCome[sector].Dispose();
            layer.CostsToCome[sector] = default;
        }

        /// <summary>
        /// The cell a portal is measured from: the middle of its run. A portal is one contiguous run of open cells along
        /// the edge, so its centre is reachable from wherever any of its cells is.
        /// </summary>
        private static int CentreCell(SectorState state, int portal) =>
            state.PortalCells[state.PortalOffsets[portal] + state.Portals[portal].Length / 2];

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
                Width = sectorCells, Height = sectorCells, Tiles = state.Crop, Goals = goalArray, Cost = field,
            }.Run();
            goalArray.Dispose();

            var mins = new ushort[state.Portals.Length];
            for (int p = 0; p < mins.Length; p++) mins[p] = field[CentreCell(state, p)]; // centre, as the cost table
            field.Dispose();
            return mins;
        }

        // ---- the abstract search -------------------------------------------------------------

        private void ResetSearch(Layer layer)
        {
            layer.Distance.AsSpan().Fill(Infinite);
            layer.Settled.AsSpan().Clear();
            layer.Heap.Clear();
        }

        private void Seed(Layer layer, int sector, int portal, ushort cost)
        {
            if (cost >= Infinite) return;
            int node = sector * MaxPortals + portal;
            if (cost >= layer.Distance[node]) return;
            layer.Distance[node] = cost;
            HeapPush(layer.Heap, layer.Distance, node);
        }

        /// <summary>The distinct pockets the order's start cells stand in, in the cells' order.</summary>
        private List<int> StartPockets(int sizeClass, NativeArray<int> startCells)
        {
            var pockets = new List<int>(8);
            var seen = new HashSet<int>();
            for (int i = 0; i < startCells.Length; i++)
            {
                int pocket = PocketOf(sizeClass, new int2(startCells[i] % width, startCells[i] / width));
                if (pocket >= 0 && seen.Add(pocket)) pockets.Add(pocket);
            }
            return pockets;
        }

        /// <summary>
        /// Runs the search until a portal of every <paramref name="waiting"/> pocket is settled. Dijkstra settles the cheapest
        /// portal of a pocket first, and only a crossing can reach it, so the pocket's way out is known then.
        /// ponytail: a pocket the goal is walled off from runs this to the end of the goal's region: on a 1024 map about 7 ms
        /// a time, and about 390 ms the first time if no order has filled the cost tables yet. Check the region first
        /// (<see cref="RegionOf"/>) if that ever shows.
        /// </summary>
        private void ExpandUntilSettled(Layer layer, HashSet<int> waiting)
        {
            if (waiting.Count == 0) return;
            searchWaiting.Clear();
            foreach (int pocket in waiting) searchWaiting.Add(pocket);
            searchState[0] = MaxSearchNodes;
            var search = new ExpandJob
            {
                SectorsX = sectorsX, SectorsY = sectorsY, PocketsPerSector = SectorCells,
                PortalCount = layer.PortalCount, Portals = layer.Portals, PortalPocket = layer.PortalPocket, Costs = layer.Costs,
                Matching = layer.Matching, Distance = layer.Distance, Settled = layer.Settled, Heap = layer.Heap,
                Waiting = searchWaiting, State = searchState,
            };
            // The job stops at a sector that lacks what it reads; with that built it goes on from where it stopped.
            for (search.Run(); searchState[1] >= 0; search.Run())
            {
                if (searchState[2] == 1) EnsureCosts(layer, searchState[1]);
                else Materialize(layer, searchState[1]);
            }
            waiting.Clear();
            foreach (int pocket in searchWaiting) waiting.Add(pocket);
        }

        /// <summary>
        /// The exit of <paramref name="sector"/> on the cheapest way to the goal from its portal <paramref name="entry"/>:
        /// in-sector cost from the entry plus the cost of crossing the exit (<see cref="CrossingCostOf"/>), over exits the
        /// sector connects to the entry. -1 when none is.
        /// </summary>
        private int BestExitFrom(Layer layer, int sector, int entry)
        {
            EnsureCosts(layer, sector);
            SectorState state = layer.Sectors[sector];
            int n = state.Portals.Length;
            if (entry >= n) return -1;
            UnsafeList<ushort> costs = layer.Costs[sector];
            int best = -1, bestCost = int.MaxValue;
            for (int q = 0; q < n; q++)
            {
                if (q == entry) continue;
                int inside = costs[entry * n + q], onward = CrossingCostOf(layer, sector, q);
                if (inside >= Infinite || onward >= Infinite) continue;
                if (inside + onward < bestCost) { bestCost = inside + onward; best = q; }
            }
            return best;
        }

        /// <summary>The portal of a sector's <paramref name="pocket"/> with the cheapest way to the goal by crossing it, or -1.</summary>
        private int BestCrossingOf(Layer layer, int sector, int pocket)
        {
            SectorState state = layer.Sectors[sector];
            int n = state.Portals.Length, best = -1, bestCost = Infinite;
            for (int q = 0; q < n; q++)
            {
                if (state.PortalPocket[q] != pocket) continue;
                int cost = CrossingCostOf(layer, sector, q);
                if (cost < bestCost) { bestCost = cost; best = q; }
            }
            return best;
        }

        /// <summary>
        /// Cost to the goal of leaving <paramref name="sector"/> by crossing <paramref name="portal"/>: the crossing plus the
        /// settled (exact) cost of the matching portal beyond it; <see cref="Infinite"/> when that isn't known. A portal's
        /// own <see cref="Layer.Distance"/> can come from a way through its sector to another portal instead, so seeding an
        /// exit with it (and writing the crossing step on it) could send units across where the cheap way doesn't cross, and
        /// two sectors' fields could then point at each other.
        /// </summary>
        private int CrossingCostOf(Layer layer, int sector, int portal)
        {
            int matching = Matching(layer, sector, portal);
            int neighbour = matching >= 0 ? NeighbourSector(sector, layer.Sectors[sector].Portals[portal].Edge) : -1;
            if (neighbour < 0) return Infinite;
            int node = neighbour * MaxPortals + matching;
            if (!layer.Settled[node] || layer.Distance[node] >= Infinite) return Infinite;
            return math.min(Infinite - 1, layer.Distance[node] + CrossingCost);
        }

        /// <summary>The matching portal on the other side of a portal's edge, or -1.</summary>
        private int Matching(Layer layer, int sector, int portal)
        {
            int neighbour = NeighbourSector(sector, layer.Sectors[sector].Portals[portal].Edge);
            if (neighbour >= 0) Materialize(layer, neighbour);
            return Match(layer.Matching, layer.Portals, layer.PortalCount, sector * MaxPortals + portal, neighbour);
        }

        /// <summary>
        /// <see cref="Matching"/> for a node whose <paramref name="neighbour"/> sector across its edge (-1 for none) is built:
        /// the kept answer, or the neighbour's portal on the facing edge whose run overlaps this one.
        /// </summary>
        private static int Match(NativeArray<int> matching, NativeArray<Portal> portals, NativeArray<int> portalCount, int node, int neighbour)
        {
            if (neighbour < 0) { matching[node] = -1; return -1; }
            int count = portalCount[neighbour];
            if (matching[node] != NotLookedUp && matching[node] < count) return matching[node];

            Portal portal = portals[node];
            int opposite = portal.Edge ^ 1; // West and East are 0 and 1, South and North 2 and 3
            int from = portal.Start, to = from + portal.Length;
            int result = -1;
            for (int q = 0; q < count; q++)
            {
                Portal other = portals[neighbour * MaxPortals + q];
                if (other.Edge != opposite) continue;
                if (other.Start < to && from < other.Start + other.Length) { result = q; break; } // the runs overlap along the edge
            }
            matching[node] = result;
            return result;
        }

        private int NeighbourSector(int sector, int edge) => NeighbourSector(sector, edge, sectorsX, sectorsY);

        private static int NeighbourSector(int sector, int edge, int sectorsX, int sectorsY)
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

        private static void HeapPush(NativeList<int> heap, NativeArray<ushort> distance, int node)
        {
            heap.Add(node);
            int i = heap.Length - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (distance[heap[parent]] <= distance[heap[i]]) break;
                int swap = heap[parent];
                heap[parent] = heap[i];
                heap[i] = swap;
                i = parent;
            }
        }

        private static int HeapPop(NativeList<int> heap, NativeArray<ushort> distance)
        {
            int top = heap[0];
            int last = heap.Length - 1;
            heap[0] = heap[last];
            heap.Length = last;
            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, smallest = i;
                if (left < last && distance[heap[left]] < distance[heap[smallest]]) smallest = left;
                if (right < last && distance[heap[right]] < distance[heap[smallest]]) smallest = right;
                if (smallest == i) break;
                int swap = heap[smallest];
                heap[smallest] = heap[i];
                heap[i] = swap;
                i = smallest;
            }
            return top;
        }

        /// <summary>
        /// The route search: Dijkstra over the portals, from the seeded nodes until a portal of every waiting pocket is
        /// settled (it settles the cheapest portal of a pocket first, and only a crossing can reach it, so the pocket's way
        /// out is known then). It reads only what the graph has built: at a sector that lacks its cost table, or whose
        /// neighbour across the portal lacks its portals, it stops and says which, and the next run goes on from there.
        /// In C# this loop was most of a route's cost on the main thread (perf 2026-11-11).
        /// </summary>
        [BurstCompile]
        private struct ExpandJob : IJob
        {
            public int SectorsX, SectorsY, PocketsPerSector;
            [ReadOnly] public NativeArray<int> PortalCount;
            [ReadOnly] public NativeArray<Portal> Portals;
            [ReadOnly] public NativeArray<short> PortalPocket;
            [ReadOnly] public NativeArray<UnsafeList<ushort>> Costs;
            public NativeArray<int> Matching;
            public NativeArray<ushort> Distance;
            public NativeArray<bool> Settled;
            public NativeList<int> Heap;
            public NativeHashSet<int> Waiting;

            /// <summary>
            /// [0] the nodes the search may still take off the heap; [1] the sector it stopped for, or -1 when it ended;
            /// [2] 1 when that sector needs its cost table, 0 when it needs its portals.
            /// </summary>
            public NativeArray<int> State;

            public void Execute()
            {
                int left = State[0];
                State[1] = -1;
                while (left > 0 && Heap.Length > 0 && Waiting.Count > 0)
                {
                    int node = Heap[0];
                    if (Settled[node])
                    {
                        HeapPop(Heap, Distance);
                        left--;
                        continue;
                    }
                    int sector = node / MaxPortals, portal = node % MaxPortals;
                    int neighbour = NeighbourSector(sector, Portals[node].Edge, SectorsX, SectorsY);
                    bool noCosts = !Costs[sector].IsCreated;
                    if (noCosts || (neighbour >= 0 && PortalCount[neighbour] < 0))
                    {
                        State[0] = left;
                        State[1] = noCosts ? sector : neighbour;
                        State[2] = noCosts ? 1 : 0;
                        return;
                    }

                    HeapPop(Heap, Distance);
                    left--;
                    Settled[node] = true;
                    Waiting.Remove(sector * PocketsPerSector + PortalPocket[node]);
                    int n = PortalCount[sector];
                    UnsafeList<ushort> costs = Costs[sector];
                    int distance = Distance[node];
                    for (int q = 0; q < n; q++)
                    {
                        if (q == portal) continue;
                        ushort viaSector = costs[portal * n + q];
                        if (viaSector < Infinite) Relax(sector * MaxPortals + q, distance + viaSector);
                    }
                    int matching = Match(Matching, Portals, PortalCount, node, neighbour);
                    if (matching >= 0) Relax(neighbour * MaxPortals + matching, distance + CrossingCost);
                }
                State[0] = left;
            }

            private void Relax(int node, int cost)
            {
                if (cost >= Infinite || Settled[node] || cost >= Distance[node]) return;
                Distance[node] = (ushort)cost;
                HeapPush(Heap, Distance, node);
            }
        }

        /// <summary>
        /// A sector's cost table: the walk inside the sector between every two of its portals, from one integration field
        /// per portal. One job for the table (it was a job and two arrays per portal), scheduled by <see cref="ScheduleCosts"/>.
        /// </summary>
        [BurstCompile]
        private struct PortalCostsJob : IJob
        {
            public int Side;
            [ReadOnly] public NativeArray<byte> Crop;

            /// <summary>Each portal's centre cell (<see cref="CentreCell"/>).</summary>
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<int> Centres;

            /// <summary>The table, <c>from * portals + to</c>.</summary>
            public UnsafeList<ushort> Costs;

            public void Execute()
            {
                int n = Centres.Length;
                var goal = new NativeArray<int>(1, Allocator.Temp);
                var field = new NativeArray<ushort>(Crop.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int p = 0; p < n; p++)
                {
                    goal[0] = Centres[p];
                    new BuildIntegrationFieldJob { Width = Side, Height = Side, Tiles = Crop, Goals = goal, Cost = field }.Execute();
                    for (int q = 0; q < n; q++)
                    {
                        // Centre to centre: a true distance between two points, so the costs obey the triangle
                        // inequality. Measured between the nearest cells of two portals instead, a wide portal acted
                        // as a teleporter (west → south → east across an open sector cost 2 through the corner cells
                        // they share), the search's distances came out far too low, and the route walk looped round
                        // a sector corner while units bounced between two sectors' fields. At least 1 between
                        // distinct portals keeps the distances strictly ordered.
                        ushort cost = field[Centres[q]];
                        Costs[p * n + q] = p != q && cost < Infinite ? (ushort)math.max(1, (int)cost) : cost;
                    }
                }
            }
        }

        // ---- specs ---------------------------------------------------------------------------

        private SectorFieldSpec NewSpec(int sector) => new SectorFieldSpec
        {
            Sector = sector,
            ExitDirection = FlowDirections.None,
        };

        /// <summary>
        /// Adds an exit to a sector's field: seeded with the exit portal's cells at the portal's cost to the goal,
        /// which is the plan's seeding. The exit cells are also written with the crossing step, so a unit
        /// standing on them steps into the next sector instead of turning around inside this one.
        /// </summary>
        private SectorFieldSpec WithExit(SectorFieldSpec spec, Layer layer, int sector, int exit)
        {
            SectorState state = layer.Sectors[sector];
            int crossingCost = CrossingCostOf(layer, sector, exit);
            ushort cost = crossingCost < Infinite ? (ushort)crossingCost : layer.Distance[sector * MaxPortals + exit];
            byte crossing = Crossing[state.Portals[exit].Edge];
            int offset = state.PortalOffsets[exit], length = state.Portals[exit].Length;
            // A sector's lists are short, and a wide exit could fill them: each pocket keeps a share, or a later one has no way out.
            int room = math.min(spec.GoalCells.Capacity - spec.GoalCells.Length, spec.ExitDirections.Capacity - spec.ExitDirections.Length);
            int share = math.max(1, room / math.max(1, state.ExitPockets));
            if (length > share) { offset += (length - share) / 2; length = share; }
            length = math.min(length, room);
            for (int i = 0; i < length; i++)
            {
                int cell = state.PortalCells[offset + i];
                spec.GoalCells.Add(cell);
                spec.GoalCosts.Add(cost);
                spec.ExitCells.Add(cell);
                spec.ExitDirections.Add(crossing);
            }
            if (spec.ExitDirection == FlowDirections.None) spec.ExitDirection = crossing;
            return spec;
        }

        /// <summary>Crop-local index of a cell of the cell grid in a sector.</summary>
        private int LocalCell(int cell, int sector)
        {
            int sx = sector % sectorsX, sy = sector / sectorsX;
            int x = cell % cellWidth - sx * sectorCells;
            int y = cell / cellWidth - sy * sectorCells;
            return y * sectorCells + x;
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
        public FixedList512Bytes<int> Goals;
        public FixedList512Bytes<ushort> GoalCosts;

        /// <summary>False to only fill <see cref="Cost"/> (the route search's seeding pass).</summary>
        public bool WriteDirection;

        /// <summary>Cells a crossing step is written on, each with its step in <see cref="ExitDirections"/>.</summary>
        public FixedList512Bytes<int> ExitCells;
        public FixedList128Bytes<byte> ExitDirections;

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

            for (int i = 0; i < ExitCells.Length && i < ExitDirections.Length; i++) Direction[ExitCells[i]] = ExitDirections[i];
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
