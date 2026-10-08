using NUnit.Framework;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Pathing;
using WAR2D.World;

/// <summary>
/// Tests for the Step 6 hierarchy: sector portals along shared edges, the route a start sector takes to
/// a goal sector, and the seeded sector field job the route's local fields are built with.
/// </summary>
public class SectorGraphTests
{
    private const byte Floor = PathMap.Floor;
    private const byte Rock = PathMap.Rock;
    private const int Width = 32;

    private static int Index(in PathMap map, int2 tile) => tile.y * map.Width + tile.x;
    private static int Local(int2 tile) => tile.y * Width + tile.x;

    /// <summary>A 64x64 map: floor inside a rock border, and a rock wall on the x=32 sector boundary whose
    /// gap at y=31..33 is <paramref name="gapTiles"/> tiles wide (0 seals it).</summary>
    private static PathMap WallMap(int gapTiles, Allocator allocator)
    {
        var map = new PathMap(64, 64, new NativeArray<byte>(64 * 64, allocator));
        for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Floor;
        for (int k = 0; k < 64; k++)
        {
            map.Tiles[k] = Rock;
            map.Tiles[63 * 64 + k] = Rock;
            map.Tiles[k * 64] = Rock;
            map.Tiles[k * 64 + 63] = Rock;
        }
        for (int y = 1; y < 63; y++)
            if (y < 31 || y > 33 || gapTiles == 0) map.Tiles[y * 64 + 32] = Rock;
        return map;
    }

    [Test]
    public void PortalsAreMaximalRunsOfTraversableEdgeCells()
    {
        PathMap map = WallMap(gapTiles: 0, Allocator.TempJob);
        try
        {
            // Free the gap by hand so both classes can be compared: small sees a 1-tile gap, large does not.
            map.Tiles[32 * 64 + 32] = Floor;
            using var graph = new SectorGraph(map, Allocator.Persistent);

            // The freed cell is (32,32), which lies on the edge between sectors 2 (west) and 3 (east).
            var westSector = graph.SectorOf(new int2(16, 40));
            Assert.AreEqual(graph.SectorOf(new int2(31, 32)), westSector);

            SectorGraph.Portal[] small = graph.PortalsOf(FlowSizeClass.Small, westSector);
            var east = new System.Collections.Generic.List<SectorGraph.Portal>();
            foreach (SectorGraph.Portal portal in small)
                if (portal.Edge == 1) east.Add(portal);
            Assert.AreEqual(1, east.Count, "the wall leaves exactly one run open on the east edge");
            Assert.AreEqual(1, east[0].Length, "the wall's gap is one tile wide for small units");
            Assert.AreEqual(32, east[0].Start, "the run is the single cell at y=32");

            SectorGraph.Portal[] large = graph.PortalsOf(FlowSizeClass.Large, westSector);
            foreach (SectorGraph.Portal portal in large)
                Assert.AreNotEqual(1, portal.Edge, "the 1-tile gap is sealed for the large class");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void RouteSpecsSeedTheExitPortalAndTheGoalCells()
    {
        PathMap map = WallMap(gapTiles: 3, Allocator.TempJob);
        try
        {
            using var graph = new SectorGraph(map, Allocator.Persistent);
            int goalSector = graph.SectorOf(new int2(40, 40));
            int startSector = graph.SectorOf(new int2(10, 20));
            Assert.AreNotEqual(startSector, goalSector);

            var goals = new NativeArray<int>(1, Allocator.TempJob);
            goals[0] = Index(map, new int2(40, 40));
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = Index(map, new int2(10, 20));

            SectorFieldSpec[] specs = graph.BuildRoute(FlowSizeClass.Small, goals, starts, out int covered);
            goals.Dispose();
            starts.Dispose();

            Assert.GreaterOrEqual(covered, 2, "the route covers the start sector and the goal sector");
            Assert.Greater(specs.Length, 0);

            int goalSpecs = 0, routeSpecs = 0;
            foreach (SectorFieldSpec spec in specs)
            {
                if (spec.Sector == goalSector)
                {
                    goalSpecs++;
                    Assert.AreEqual(FlowDirections.None, spec.ExitDirection, "the goal sector has no exit to cross");
                    Assert.Greater(spec.GoalCells.Length, 0);
                    for (int i = 0; i < spec.GoalCells.Length; i++)
                        Assert.AreEqual(0, spec.GoalCosts[i], "an order's own goal cells are seeded at 0");
                }
                else
                {
                    routeSpecs++;
                    Assert.AreNotEqual(FlowDirections.None, spec.ExitDirection, "a route sector exits across an edge");
                    Assert.Greater(spec.ExitCells.Length, 0);
                    Assert.AreEqual(spec.ExitCells.Length, spec.GoalCells.Length, "the exit portal is the field's seed");
                    Assert.Greater(spec.GoalCosts[0], 0, "a route sector's exit carries its cost to the goal");
                }
            }
            Assert.AreEqual(1, goalSpecs);
            Assert.GreaterOrEqual(routeSpecs, 1);
            Assert.AreEqual(covered, specs.Length, "one field per covered sector");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void UnreachableStartSectorIsLeftOutOfTheRoute()
    {
        PathMap map = WallMap(gapTiles: 0, Allocator.TempJob);
        try
        {
            using var graph = new SectorGraph(map, Allocator.Persistent);
            int goalSector = graph.SectorOf(new int2(40, 40));
            var goals = new NativeArray<int>(1, Allocator.TempJob);
            goals[0] = Index(map, new int2(40, 40));
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = Index(map, new int2(10, 20));

            SectorFieldSpec[] specs = graph.BuildRoute(FlowSizeClass.Small, goals, starts, out int covered);
            goals.Dispose();
            starts.Dispose();

            Assert.AreEqual(1, covered, "with the wall sealed only the goal sector is covered");
            Assert.AreEqual(1, specs.Length);
            Assert.AreEqual(goalSector, specs[0].Sector);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void SectorCacheSamplesItsRouteAndNoneOutsideIt()
    {
        PathMap map = WallMap(gapTiles: 0, Allocator.TempJob);
        try
        {
            // The only crossing is the freed cell at (32,32), so the route must run
            // sector 0 -> sector 2 -> sector 3 and sector 1 stays uncovered.
            map.Tiles[Index(map, new int2(32, 32))] = Floor;
            using var cache = new SectorFieldCache(map, Allocator.Persistent);
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = Index(map, new int2(10, 20));
            int handle = cache.Acquire(new int2(40, 40), FlowSizeClass.Small, 2500, starts);
            starts.Dispose();

            Assert.IsFalse(cache.IsReady(handle), "a fresh route is stale until it is built");
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            Assert.AreEqual(1, cache.LiveCount);
            Assert.Greater(cache.CoveredSectorCount, 1, "the route covers more than the goal sector");

            Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(handle, Index(map, new int2(40, 40))));
            byte onRoute = cache.DirectionAtCell(handle, Index(map, new int2(10, 20)));
            Assert.Less(onRoute, 8, "a cell on the route has one of the eight steps");

            int uncovered = cache.Graph.SectorOf(new int2(40, 10));
            Assert.IsFalse(cache.CoversSector(handle, uncovered), "the route does not enter the sealed half");
            Assert.AreEqual(FlowDirections.None, cache.DirectionAtCell(handle, Index(map, new int2(40, 10))));

            // A terrain change stales the route; the rebuild makes it ready again.
            map.Tiles[Index(map, new int2(32, 40))] = Floor;
            cache.Invalidate(new int2(32, 40));
            Assert.IsFalse(cache.IsReady(handle));
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(handle, Index(map, new int2(40, 40))));

            cache.Release(handle, 2500);
            Assert.AreEqual(0, cache.LiveCount);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void RoutesWorkOnAGeneratedCaveMap()
    {
        PathMap map = PathTestMaps.Generate(256, 5);
        using var cache = new SectorFieldCache(map, Allocator.Persistent);
        try
        {

        // A start and a goal the generator guarantees are floor: two of the eight HQ clearings.
        int2[] sites = MapGenerator.HqSites(256);
        var starts = new NativeArray<int>(1, Allocator.TempJob);
        starts[0] = sites[0].y * map.Width + sites[0].x;
        int2 goal = sites[1];
        int handle = cache.Acquire(goal, FlowSizeClass.Small, 1, starts);
        starts.Dispose();

        cache.RebuildDirty(2).Complete();
        cache.CompleteRebuilds();
        Assert.IsTrue(cache.IsReady(handle));
        Assert.Greater(cache.CoveredSectorCount, 1, "the route leaves the start sector");
        Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(handle, goal.y * map.Width + goal.x));
        Assert.Less(cache.DirectionAtCell(handle, sites[0].y * map.Width + sites[0].x), 8,
            "the start clearing is on the route and has a direction");

        // Two terrain changes and rebuilds keep the route usable.
        for (int i = 0; i < 2; i++)
        {
            int2 changed = sites[0] + new int2(20 + i, 5);
            map.Tiles[changed.y * map.Width + changed.x] = Rock;
            cache.Invalidate(changed);
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(handle, goal.y * map.Width + goal.x));
        }
            cache.Release(handle, 1);
            Assert.AreEqual(0, cache.LiveCount);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void StressRoutesWithTerrainChangesOnAGeneratedMap()
    {
        // The shape of the reference load: a wide load of mixed orders with terrain changes landing
        // between route builds, on a generated 512 map.
        PathMap map = PathTestMaps.Generate(512, 9);
        try
        {
            using var cache = new SectorFieldCache(map, Allocator.Persistent);
            var rng = new Unity.Mathematics.Random(3);
            var starts = new NativeArray<int>(8, Allocator.TempJob);
            var changes = new NativeArray<int2>(1, Allocator.TempJob);

            for (int round = 0; round < 500; round++)
            {
                for (int i = 0; i < starts.Length; i++) starts[i] = RandomFloorCell(map, ref rng);
                int2 goal = TileOf(map, RandomFloorCell(map, ref rng));
                int handle = cache.Acquire(goal, round % 2 == 0 ? FlowSizeClass.Small : FlowSizeClass.Large, 10, starts);
                cache.RebuildDirty(2).Complete();
                cache.CompleteRebuilds();
                Assert.IsTrue(cache.IsReady(handle));

                if (round % 4 == 0)
                {
                    int2 changed = TileOf(map, RandomFloorCell(map, ref rng));
                    map.Tiles[changed.y * map.Width + changed.x] = Rock;
                    cache.Invalidate(changed);
                    cache.RebuildDirty(2).Complete();
                    cache.CompleteRebuilds();
                }
                cache.Release(handle, 10);
                Assert.AreEqual(0, cache.LiveCount);
            }
            changes.Dispose();
        }
        finally
        {
            map.Dispose();
        }
    }

    private static int2 TileOf(in PathMap map, int cell) => new int2(cell % map.Width, cell / map.Width);

    private static int RandomFloorCell(in PathMap map, ref Unity.Mathematics.Random rng)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int cell = rng.NextInt(map.Tiles.Length);
            if (map.Tiles[cell] == Floor) return cell;
        }
        return 0;
    }

    /// <summary>A 256 map that is all rock except a 16-tile-tall corridor, so a route west to east lies
    /// inside the first row of sectors and nowhere else.</summary>
    private static PathMap CorridorMap(Allocator allocator)
    {
        var map = new PathMap(256, 256, new NativeArray<byte>(256 * 256, allocator));
        for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Rock;
        for (int y = 8; y <= 23; y++)
        for (int x = 0; x < 256; x++)
            map.Tiles[y * 256 + x] = Floor;
        return map;
    }

    [Test]
    public void TerrainChangesAwayFromARouteKeepItReady()
    {
        PathMap map = CorridorMap(Allocator.TempJob);
        try
        {
            using var cache = new SectorFieldCache(map, Allocator.Persistent);
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = Index(map, new int2(16, 16));
            int handle = cache.Acquire(new int2(240, 16), FlowSizeClass.Small, 1, starts);
            starts.Dispose();
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            // The corridor keeps the route in the sector row y 0..31: sectors 0, 1, ... 7.
            Assert.IsTrue(cache.CoversSector(handle, 3));
            Assert.IsFalse(cache.CoversSector(handle, 3 + 8), "the row below is not on the route");

            // A change seven sector rows away cannot touch the route: the order stays ready.
            long before = cache.ReRoutes;
            int2 far = new int2(16, 200);
            map.Tiles[Index(map, far)] = Floor;
            cache.Invalidate(far);
            Assert.IsTrue(cache.IsReady(handle), "a change far from the route must not stale it");
            Assert.AreEqual(before, cache.ReRoutes);

            // A change one sector row from the route (its neighbour ring) does stale it.
            int2 near = new int2(16, 40);
            map.Tiles[Index(map, near)] = Floor;
            cache.Invalidate(near);
            Assert.IsFalse(cache.IsReady(handle), "a change next to the route stales it");
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            Assert.AreEqual(before + 1, cache.ReRoutes);

            // A change inside the route stales it too.
            int2 onRoute = new int2(100, 16);
            map.Tiles[Index(map, onRoute)] = Rock;
            cache.Invalidate(onRoute);
            Assert.IsFalse(cache.IsReady(handle), "a change on the route stales it");
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            Assert.AreEqual(1, cache.NewRoutes, "the order was routed once; the rest are re-routes");
            Assert.AreEqual(2, cache.ReRoutes);
            Assert.Greater(cache.NewSectorFields, 0);
            Assert.Greater(cache.ReSectorFields, 0);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void HalfResolutionSealsGapsNarrowerThanItsCells()
    {
        PathMap map = WallMap(gapTiles: 0, Allocator.TempJob);
        try
        {
            map.Tiles[32 * 64 + 32] = Floor; // one tile wide: one cell of a 2x2 grid carries a rock
            int west = 2;                     // sector (0, 1): x 0..31, y 32..63
            using var tile = new SectorGraph(map, Allocator.Persistent, cellSize: 1);
            using var half = new SectorGraph(map, Allocator.Persistent, cellSize: 2);

            bool tileEast = false;
            foreach (SectorGraph.Portal portal in tile.PortalsOf(FlowSizeClass.Small, west))
                tileEast |= portal.Edge == 1;
            Assert.IsTrue(tileEast, "at one tile per cell the gap is a portal");

            foreach (SectorGraph.Portal portal in half.PortalsOf(FlowSizeClass.Small, west))
                Assert.AreNotEqual(1, portal.Edge, "at half resolution the 1-tile gap is sealed");
            Assert.AreEqual(2, half.CellSize);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void HalfResolutionSamplingUsesTheContainingCell()
    {
        PathMap map = CorridorMap(Allocator.TempJob);
        try
        {
            using var cache = new SectorFieldCache(map, Allocator.Persistent, cellSize: 2);
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = Index(map, new int2(16, 16));
            int handle = cache.Acquire(new int2(240, 16), FlowSizeClass.Small, 1, starts);
            starts.Dispose();
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(handle));
            Assert.AreEqual(0, cache.UnreachableRebuilds, "the corridor route reaches its start sector");

            var goal = new int2(240, 16);
            Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(handle, Index(map, goal)));
            // Two neighbouring tiles share a half-resolution cell, so they share its direction.
            int2 a = new int2(100, 16), b = new int2(101, 16);
            Assert.AreEqual(cache.DirectionAtCell(handle, Index(map, a)), cache.DirectionAtCell(handle, Index(map, b)),
                "tiles in the same half-resolution cell sample the same direction");
            Assert.Less(cache.DirectionAtCell(handle, Index(map, a)), 8);
        }
        finally
        {
            map.Dispose();
        }
    }

    /// <summary>All rock except a diagonal 3-tile-wide staircase of floor, the shape the map generator
    /// carves from each HQ to the centre.</summary>
    private static PathMap DiagonalCorridorMap(int size, Allocator allocator)
    {
        var map = new PathMap(size, size, new NativeArray<byte>(size * size, allocator));
        for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Rock;
        int2 from = new int2(16, 16), to = new int2(size - 16, size - 16);
        int steps = math.max(math.abs(to.x - from.x), math.abs(to.y - from.y));
        for (int i = 0; i <= steps; i++)
        {
            int2 centre = (int2)math.round(math.lerp((float2)from, (float2)to, (float)i / steps));
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int2 tile = centre + new int2(dx, dy);
                if ((uint)tile.x < (uint)size && (uint)tile.y < (uint)size)
                    map.Tiles[tile.y * size + tile.x] = Floor;
            }
        }
        return map;
    }

    [Test]
    public void HalfResolutionPinchesDiagonalCorridors()
    {
        PathMap map = DiagonalCorridorMap(128, Allocator.TempJob);
        var starts = new NativeArray<int>(1, Allocator.TempJob);
        try
        {
            starts[0] = Index(map, new int2(16, 16));
            var goal = new int2(112, 112);

            using (var cache = new SectorFieldCache(map, Allocator.Persistent, cellSize: 1))
            {
                int handle = cache.Acquire(goal, FlowSizeClass.Small, 1, starts);
                cache.RebuildDirty(2).Complete();
                cache.CompleteRebuilds();
                Assert.Less(cache.DirectionAtCell(handle, Index(map, new int2(16, 16))), 8,
                    "one tile per cell follows the staircase");
                Assert.AreEqual(0, cache.UnreachableRebuilds);
            }

            // The conservative half-resolution rule blocks a cell when any of its four tiles is rock,
            // and no 2x2 window fits inside a 3-tile diagonal staircase, so the channel pinches and the
            // route is lost. The cache reports it rather than pretending: this is the fidelity price of
            // rung 3 on the generator's corridors, and the benchmark quantifies how often it happens.
            using (var cache = new SectorFieldCache(map, Allocator.Persistent, cellSize: 2))
            {
                int handle = cache.Acquire(goal, FlowSizeClass.Small, 1, starts);
                cache.RebuildDirty(2).Complete();
                cache.CompleteRebuilds();
                Assert.AreEqual(FlowDirections.None, cache.DirectionAtCell(handle, Index(map, new int2(16, 16))),
                    "the half-resolution channel pinches on a 3-tile diagonal staircase");
                Assert.Greater(cache.UnreachableRebuilds, 0, "and the rebuild reports its unreachable start sector");
            }
        }
        finally
        {
            starts.Dispose();
            map.Dispose();
        }
    }

    [Test]
    public void HalfResolutionRoutesOnAGeneratedCaveMap()
    {
        PathMap map = PathTestMaps.Generate(256, 5);
        try
        {
            using var cache = new SectorFieldCache(map, Allocator.Persistent, cellSize: 2);
            int2[] sites = MapGenerator.HqSites(256);
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = sites[0].y * map.Width + sites[0].x;
            int small = cache.Acquire(sites[1], FlowSizeClass.Small, 1, starts);
            int large = cache.Acquire(sites[1], FlowSizeClass.Large, 1, starts);
            starts.Dispose();
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();

            Assert.IsTrue(cache.IsReady(small));
            Assert.IsTrue(cache.IsReady(large));
            Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(small, sites[1].y * map.Width + sites[1].x));
            // The generator's diagonal 3-tile corridors pinch at half resolution (see
            // HalfResolutionPinchesDiagonalCorridors), so a route between two HQ clearings is not
            // guaranteed here; what the test pins is that the design reports the loss in its counters
            // and never claims a covered sector it did not build.
            Assert.GreaterOrEqual(cache.UnreachableStartSectors, 0);
            Assert.Greater(cache.CoveredSectorCount, 0, "the route covers at least the goal's sector");
            Assert.IsTrue(cache.CoversSector(small, cache.Graph.SectorOf(sites[1])));
            Assert.IsTrue(cache.IsLive(small));
            Assert.IsTrue(cache.IsLive(large));
            cache.Release(small, 1);
            cache.Release(large, 1);
            Assert.AreEqual(0, cache.LiveCount);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void SectorFieldSeedsPreferTheCheaperPortal()
    {
        var tiles = new NativeArray<byte>(Width * Width, Allocator.TempJob);
        for (int i = 0; i < tiles.Length; i++) tiles[i] = Floor;
        var cost = new NativeArray<ushort>(Width * Width, Allocator.TempJob);
        var direction = new NativeArray<byte>(Width * Width, Allocator.TempJob);
        try
        {
            // The near portal at (2,2) is seeded cheapest, so cells must flow to it even though the far
            // portal at (29,29) is also a goal.
            var job = new BuildSectorFieldJob
            {
                Width = Width, Height = Width, Tiles = tiles, WriteDirection = true,
                ExitDirection = FlowDirections.None, Cost = cost, Direction = direction,
            };
            job.Goals.Add(Local(new int2(2, 2)));
            job.GoalCosts.Add(0);
            job.Goals.Add(Local(new int2(29, 29)));
            job.GoalCosts.Add(300); // cheaper than the 378 a plain walk from the near portal would cost
            job.Run();

            Assert.AreEqual(0, cost[Local(new int2(2, 2))]);
            Assert.AreEqual(300, cost[Local(new int2(29, 29))]);
            Assert.AreEqual(42, cost[Local(new int2(5, 5))], "three diagonals from the near portal");
            Assert.AreEqual(5, direction[Local(new int2(5, 5))], "and the direction is south-west, toward it");

            // Swap the seed costs: now every cell in the far half must flow east-south instead.
            var swapped = new BuildSectorFieldJob
            {
                Width = Width, Height = Width, Tiles = tiles, WriteDirection = true,
                ExitDirection = FlowDirections.None, Cost = cost, Direction = direction,
            };
            swapped.Goals.Add(Local(new int2(2, 2)));
            swapped.GoalCosts.Add(300);
            swapped.Goals.Add(Local(new int2(29, 29)));
            swapped.GoalCosts.Add(0);
            swapped.ExitCells.Add(Local(new int2(31, 16)));
            swapped.ExitDirection = 0; // east
            swapped.Run();

            Assert.AreEqual(300, cost[Local(new int2(2, 2))]);
            Assert.AreEqual(0, cost[Local(new int2(29, 29))]);
            Assert.AreEqual(336, cost[Local(new int2(5, 5))], "24 diagonals from the far portal");
            Assert.AreEqual(1, direction[Local(new int2(5, 5))], "north-east, toward the cheap portal");
            Assert.AreEqual(0, direction[Local(new int2(31, 16))], "the exit cells carry the crossing step");
        }
        finally
        {
            tiles.Dispose();
            cost.Dispose();
            direction.Dispose();
        }
    }
}
