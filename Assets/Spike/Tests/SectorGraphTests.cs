using NUnit.Framework;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Spike;

/// <summary>
/// Tests for the Step 6 hierarchy: sector portals along shared edges, the route a start sector takes to
/// a goal sector, and the seeded sector field job the route's local fields are built with.
/// </summary>
public class SectorGraphTests
{
    private const byte Floor = SpikeMap.Floor;
    private const byte Rock = SpikeMap.Rock;
    private const int Width = 32;

    private static int Index(in SpikeMap map, int2 tile) => tile.y * map.Width + tile.x;
    private static int Local(int2 tile) => tile.y * Width + tile.x;

    /// <summary>A 64x64 map: floor inside a rock border, and a rock wall on the x=32 sector boundary whose
    /// gap at y=31..33 is <paramref name="gapTiles"/> tiles wide (0 seals it).</summary>
    private static SpikeMap WallMap(int gapTiles, Allocator allocator)
    {
        var map = new SpikeMap(64, 64, new NativeArray<byte>(64 * 64, allocator));
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
        SpikeMap map = WallMap(gapTiles: 0, Allocator.TempJob);
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
        SpikeMap map = WallMap(gapTiles: 3, Allocator.TempJob);
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
        SpikeMap map = WallMap(gapTiles: 0, Allocator.TempJob);
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
        SpikeMap map = WallMap(gapTiles: 0, Allocator.TempJob);
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
        SpikeMap map = MapGenerator.Generate(256, 5, Allocator.TempJob);
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
        SpikeMap map = MapGenerator.Generate(512, 9, Allocator.TempJob);
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

    private static int2 TileOf(in SpikeMap map, int cell) => new int2(cell % map.Width, cell / map.Width);

    private static int RandomFloorCell(in SpikeMap map, ref Unity.Mathematics.Random rng)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int cell = rng.NextInt(map.Tiles.Length);
            if (map.Tiles[cell] == Floor) return cell;
        }
        return 0;
    }

    [Test]
    public void StressTheBenchLoadExactly()
    {
        // The bench's hierarchical refload, tick for tick, in managed code so a failure has line numbers.
        SpikeMap map = MapGenerator.Generate(512, 1, Allocator.TempJob);
        try
        {
            var config = SpikeScenarioConfig.Defaults;
            config.Players = 8;
            config.UnitsPerPlayer = 10000;
            config.OrderIntervalTicks = 10;
            config.ScriptTicks = 0;
            using var scenario = SpikeScenario.Create(config, map, Allocator.Persistent);
            // The bench runs the full-field design first, so the hierarchy is built on a map the full
            // design's terrain script has already churned.
            using (var warm = new NativeList<int2>(8, Allocator.TempJob))
                for (int tick = 0; tick < 600; tick++)
                {
                    warm.Clear();
                    scenario.TerrainChangesAt(tick, warm);
                    for (int i = 0; i < warm.Length; i++)
                    {
                        int cell = warm[i].y * map.Width + warm[i].x;
                        map.Tiles[cell] = map.Tiles[cell] == Floor ? Rock : Floor;
                    }
                }
            using var cache = new SectorFieldCache(map, Allocator.Persistent);
            var rng = new Unity.Mathematics.Random(1u + 44u);
            var terrain = new NativeList<int2>(8, Allocator.TempJob);
            var live = new System.Collections.Generic.List<(int small, int large, int release)>();
            int orders = 0;

            for (int tick = 0; tick < 600; tick++)
            {
                terrain.Clear();
                scenario.TerrainChangesAt(tick, terrain);
                for (int i = 0; i < terrain.Length; i++)
                {
                    int2 tile = terrain[i];
                    int cell = tile.y * map.Width + tile.x;
                    map.Tiles[cell] = map.Tiles[cell] == Floor ? Rock : Floor;
                    cache.Invalidate(tile);
                }

                foreach ((int player, int2 goal, float fraction) in scenario.OrdersAt(tick))
                {
                    int units = (int)math.round(fraction * scenario.UnitsPerPlayer);
                    int2 click = goal + new int2(rng.NextInt(-8, 9), rng.NextInt(-8, 9));
                    var startCells = new NativeArray<int>(64, Allocator.TempJob);
                    int baseIndex = player * scenario.UnitsPerPlayer;
                    int stride = math.max(1, units / 64);
                    for (int i = 0; i < 64; i++)
                    {
                        int2 tile = (int2)math.floor(scenario.UnitPositions[baseIndex + math.min(i * stride, units - 1)]);
                        startCells[i] = tile.y * map.Width + tile.x;
                    }
                    int small = cache.Acquire(click, FlowSizeClass.Small, units, startCells);
                    int large = cache.Acquire(click, FlowSizeClass.Large, units / 10, startCells);
                    startCells.Dispose();
                    live.Add((small, large, tick + 80));
                    orders++;
                }

                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (tick < live[i].release) continue;
                    cache.Release(live[i].small, 2500);
                    cache.Release(live[i].large, 250);
                    live.RemoveAt(i);
                }

                cache.RebuildDirty(2).Complete();
                cache.CompleteRebuilds();
            }
            Assert.Greater(orders, 100);
            Assert.Greater(cache.LiveCount, 10);
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
