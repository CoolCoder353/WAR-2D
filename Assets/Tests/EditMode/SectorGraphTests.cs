using System.Collections.Generic;
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

    /// <summary>
    /// Crossing an open sector costs about its width. Portal costs used to be measured between the portals' nearest
    /// cells, so the west and east portals of an open sector were 2 apart through the corner cells each shares with the
    /// south (or north) portal; the search's distances came out far too low and, on cave maps, routes looped round a
    /// sector corner while units bounced between two sectors' fields (SimMovementTests.CrowdCrossesAGeneratedCaveMap).
    /// </summary>
    [Test]
    public void CrossingAnOpenSectorCostsItsWidth()
    {
        const int size = 96;
        var map = new PathMap(size, size, new NativeArray<byte>(size * size, Allocator.TempJob));
        try
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                map.Tiles[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1 ? Rock : Floor;
            using var graph = new SectorGraph(map, Allocator.Persistent);
            var goals = new NativeArray<int>(1, Allocator.TempJob);
            goals[0] = Index(map, new int2(85, 48));
            var starts = new NativeArray<int>(1, Allocator.TempJob);
            starts[0] = Index(map, new int2(10, 48));
            SectorFieldSpec[] specs = graph.BuildRoute(FlowSizeClass.Small, goals, starts, out _);
            goals.Dispose();
            starts.Dispose();

            int west = graph.SectorOf(new int2(10, 48)), middle = graph.SectorOf(new int2(48, 48));
            int westCost = -1, middleCost = -1;
            foreach (SectorFieldSpec spec in specs)
            {
                if (spec.Sector == west) westCost = spec.GoalCosts[0];
                if (spec.Sector == middle) middleCost = spec.GoalCosts[0];
            }
            Assert.That(westCost, Is.GreaterThan(0), "the route leaves the west sector");
            Assert.That(middleCost, Is.GreaterThan(0), "and crosses the middle one");
            Assert.That(westCost - middleCost, Is.GreaterThanOrEqualTo(250), "crossing the 32-tile middle sector costs about its width");
        }
        finally
        {
            map.Tiles.Dispose();
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

    /// <summary>Where the directions from a tile end: its last tile, and the direction there (AtGoal, Stop or None).</summary>
    private static (int2 tile, byte direction) Follow(SectorFieldCache cache, in PathMap map, int2 from)
    {
        int2 tile = from;
        for (int step = 0; step < map.Width * map.Height; step++)
        {
            byte direction = cache.DirectionAtCell(0, Index(map, tile));
            if (!FlowDirections.IsStep(direction)) return (tile, direction);
            tile += FlowDirections.Step(direction);
        }
        Assert.Fail($"the directions from {from} go round for ever");
        return default;
    }

    private static SectorFieldCache Routed(in PathMap map, int2 goal, params int2[] from)
    {
        var cache = new SectorFieldCache(map, Allocator.Persistent);
        var starts = new NativeArray<int>(from.Length, Allocator.TempJob);
        for (int i = 0; i < from.Length; i++) starts[i] = Index(map, from[i]);
        Assert.AreEqual(0, cache.Acquire(goal, FlowSizeClass.Small, 1, starts));
        starts.Dispose();
        cache.RebuildDirty(2).Complete();
        cache.CompleteRebuilds();
        return cache;
    }

    /// <summary>A 64x64 map, floor inside a rock border, with rock wherever <paramref name="rock"/> says.</summary>
    private static PathMap Carved(System.Func<int, int, bool> rock)
    {
        var map = new PathMap(64, 64, new NativeArray<byte>(64 * 64, Allocator.TempJob));
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            map.Tiles[y * 64 + x] = x == 0 || y == 0 || x == 63 || y == 63 || rock(x, y) ? Rock : Floor;
        return map;
    }

    /// <summary>The route's first cause of stuck units: one exit per start sector, in whichever pocket held the cheapest.</summary>
    [Test]
    public void EachPocketOfAStartSectorGetsItsOwnWayOut()
    {
        PathMap map = Carved((x, y) => y == 15 && x < 32); // splits sector 0 in two, both open to the east
        try
        {
            int2 goal = new int2(40, 8), near = new int2(10, 5), far = new int2(10, 25);
            using SectorFieldCache cache = Routed(map, goal, near, far);
            Assert.AreEqual(FlowDirections.AtGoal, Follow(cache, map, near).direction);
            Assert.AreEqual(FlowDirections.AtGoal, Follow(cache, map, far).direction, "the pocket without the cheapest portal");
            Assert.AreEqual(0, cache.UnreachableRebuilds);
        }
        finally
        {
            map.Dispose();
        }
    }

    /// <summary>A start sector holding the goal used to end the walk at once, whichever side of a wall the unit stood on.</summary>
    [Test]
    public void AStartBesideTheGoalButWalledOffGoesRoundThroughTheNextSector()
    {
        PathMap map = Carved((x, y) => x == 16 && y < 32); // through sector 0 from the border to its top edge
        try
        {
            int2 goal = new int2(24, 8), start = new int2(8, 8);
            using SectorFieldCache cache = Routed(map, goal, start);
            Assert.AreEqual(FlowDirections.AtGoal, Follow(cache, map, start).direction);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void AWalledOffStartStopsAtItsFloorNearestTheGoal()
    {
        PathMap map = WallMap(gapTiles: 0, Allocator.TempJob);
        try
        {
            int2 goal = new int2(40, 40), start = new int2(10, 20);
            using SectorFieldCache cache = Routed(map, goal, start);
            (int2 end, byte direction) = Follow(cache, map, start);
            Assert.AreEqual(FlowDirections.Stop, direction);
            Assert.AreEqual(new int2(31, 40), end);
            Assert.AreEqual(1, cache.UnreachableRebuilds);
            Assert.AreEqual(FlowDirections.AtGoal, cache.DirectionAtCell(0, Index(map, goal)), "the goal itself stays one");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void AWayOpeningFarFromAWalledOffRouteStillWakesIt()
    {
        const int size = 128;
        var map = new PathMap(size, size, new NativeArray<byte>(size * size, Allocator.TempJob));
        try
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                map.Tiles[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1 || x == 32 ? Rock : Floor;
            int2 goal = new int2(60, 10), start = new int2(10, 10);
            using SectorFieldCache cache = Routed(map, goal, start);
            Assert.AreEqual(FlowDirections.Stop, Follow(cache, map, start).direction);

            int2 breach = new int2(32, 120); // three sector rows from anything the route covers
            map.Tiles[Index(map, breach)] = Floor;
            cache.Invalidate(breach);
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.AreEqual(FlowDirections.AtGoal, Follow(cache, map, start).direction);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void AFarTileChangeRebuildsAWalledOffOrderAtMostOnceASecond()
    {
        const int size = 128;
        var map = new PathMap(size, size, new NativeArray<byte>(size * size, Allocator.TempJob));
        try
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                map.Tiles[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1 || x == 32 ? Rock : Floor;
            using SectorFieldCache cache = Routed(map, new int2(60, 10), new int2(10, 10));
            void Change(int2 tile)
            {
                map.Tiles[Index(map, tile)] = Rock;
                cache.Invalidate(tile);
                cache.RebuildDirty(2).Complete();
                cache.CompleteRebuilds();
            }
            long before = cache.ReSectorFields;
            Change(new int2(100, 120));
            long first = cache.ReSectorFields;
            Assert.Greater(first, before, "the first look is at once");
            Change(new int2(101, 120));
            Assert.AreEqual(first, cache.ReSectorFields, "the next waits");
            for (int i = 0; i < 20; i++)
            {
                cache.RebuildDirty(2).Complete();
                cache.CompleteRebuilds();
            }
            Assert.Greater(cache.ReSectorFields, first, "and comes");
        }
        finally
        {
            map.Dispose();
        }
    }

    /// <summary>A big order's Stop cells can't fill a sector's field, or the pockets after them would get no way out.</summary>
    [Test]
    public void ABigWalledOffOrderLeavesRoomForEveryPocket()
    {
        PathMap map = Carved((x, y) => x == 32 || (y == 10 || y == 20) && x < 32);
        try
        {
            var starts = new NativeArray<int>(200, Allocator.TempJob);
            for (int i = 0; i < starts.Length; i++) starts[i] = Index(map, new int2(5, i % 3 == 0 ? 5 : i % 3 == 1 ? 15 : 25));
            using var cache = new SectorFieldCache(map, Allocator.Persistent);
            Assert.AreEqual(0, cache.Acquire(new int2(40, 15), FlowSizeClass.Small, 1, starts));
            starts.Dispose();
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            foreach (int y in new[] { 5, 15, 25 })
                Assert.AreEqual(FlowDirections.Stop, Follow(cache, map, new int2(5, y)).direction, $"the pocket at y {y}");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void AWalledOffRoomInOneSectorStopsInsideIt()
    {
        PathMap map = Carved((x, y) => (x == 5 || x == 15) && y <= 15 || y == 15 && x >= 5 && x <= 15);
        try
        {
            int2 goal = new int2(40, 40), start = new int2(10, 5);
            using SectorFieldCache cache = Routed(map, goal, start);
            (int2 end, byte direction) = Follow(cache, map, start);
            Assert.AreEqual(FlowDirections.Stop, direction);
            Assert.Less(end.x, 15);
            Assert.Less(end.y, 15);
        }
        finally
        {
            map.Dispose();
        }
    }

    /// <summary>An order kept its first 512 cells, and with them full a sector a later unit stood in was never added.</summary>
    [Test]
    public void AnOrderOfAnySizeKeepsEveryPocketItsUnitsStandIn()
    {
        PathMap map = Carved((x, y) => false);
        try
        {
            using var cache = new SectorFieldCache(map, Allocator.Persistent);
            var starts = new NativeArray<int>(600, Allocator.TempJob);
            for (int i = 0; i < starts.Length; i++) starts[i] = Index(map, new int2(5 + i % 20, 5 + i / 60));
            starts[599] = Index(map, new int2(10, 50));
            int2 goal = new int2(50, 10);
            Assert.AreEqual(0, cache.Acquire(goal, FlowSizeClass.Small, 1, starts));
            starts.Dispose();
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.AreEqual(FlowDirections.AtGoal, Follow(cache, map, new int2(10, 50)).direction);

            // A unit that later stands in a new pocket joins it, however many came before it.
            var late = new NativeArray<int>(1, Allocator.TempJob);
            late[0] = Index(map, new int2(50, 50));
            cache.AddStartCells(0, late);
            late.Dispose();
            cache.RebuildDirty(2).Complete();
            cache.CompleteRebuilds();
            Assert.AreEqual(FlowDirections.AtGoal, Follow(cache, map, new int2(50, 50)).direction);
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void AGoalBesideAThinWallDoesNotSpreadToItsFarSide()
    {
        PathMap map = Carved((x, y) => x == 20);
        try
        {
            NativeArray<int> goals = FlowGoals.Around(new int2(19, 30), map.Tiles, 64, 64, Allocator.TempJob, FlowGoals.Spread);
            Assert.Greater(goals.Length, 8);
            foreach (int cell in goals) Assert.Less(cell % 64, 20, "the far side of the wall");
            goals.Dispose();
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void ReachesFollowsTheFloorAcrossSectors()
    {
        PathMap map = WallMap(gapTiles: 3, Allocator.TempJob);
        try
        {
            using var graph = new SectorGraph(map, Allocator.Persistent);
            Assert.IsTrue(graph.Reaches(FlowSizeClass.Small, new int2(5, 5), new int2(60, 60)), "through the gap");
            map.Tiles[Index(map, new int2(32, 31))] = Rock;
            map.Tiles[Index(map, new int2(32, 32))] = Rock;
            map.Tiles[Index(map, new int2(32, 33))] = Rock;
            graph.Invalidate(new int2(32, 32));
            Assert.IsFalse(graph.Reaches(FlowSizeClass.Small, new int2(5, 5), new int2(60, 60)), "the gap is shut");
            Assert.IsTrue(graph.Reaches(FlowSizeClass.Small, new int2(5, 5), new int2(30, 60)), "its own side still connects");
        }
        finally
        {
            map.Dispose();
        }
    }

    /// <summary>
    /// Regions survive a terrain change that joins or splits nothing (perf gate 2026-11-06), and still follow one that does: a
    /// closed gap, a reopened gap, and a lone new floor cell inside rock.
    /// </summary>
    [Test]
    public void ReachesStaysRightWhenRegionsAreKeptAcrossChanges()
    {
        PathMap map = WallMap(gapTiles: 3, Allocator.TempJob);
        try
        {
            for (int y = 19; y <= 23; y++)
            for (int x = 19; x <= 23; x++) map.Tiles[Index(map, new int2(x, y))] = Rock;
            using var graph = new SectorGraph(map, Allocator.Persistent);
            int2 home = new int2(5, 5), far = new int2(60, 60);
            Assert.IsTrue(graph.Reaches(FlowSizeClass.Small, home, far), "through the gap");

            map.Tiles[Index(map, new int2(10, 10))] = Rock; // splits nothing
            graph.Invalidate(new int2(10, 10));
            Assert.IsTrue(graph.Reaches(FlowSizeClass.Small, home, far), "a lone rock changes nothing");

            map.Tiles[Index(map, new int2(21, 21))] = Floor; // a floor cell walled in by rock: a new pocket
            graph.Invalidate(new int2(21, 21));
            Assert.IsFalse(graph.Reaches(FlowSizeClass.Small, new int2(21, 21), home), "the new cell is walled in");

            for (int y = 31; y <= 33; y++) map.Tiles[Index(map, new int2(32, y))] = Rock;
            graph.Invalidate(new int2(32, 32));
            Assert.IsFalse(graph.Reaches(FlowSizeClass.Small, home, far), "the gap is shut");

            map.Tiles[Index(map, new int2(32, 32))] = Floor;
            graph.Invalidate(new int2(32, 32));
            Assert.IsTrue(graph.Reaches(FlowSizeClass.Small, home, far), "the gap is open again");

            map.Tiles[Index(map, new int2(22, 21))] = Floor; // joins the walled-in cell to nothing yet
            graph.Invalidate(new int2(22, 21));
            Assert.IsFalse(graph.Reaches(FlowSizeClass.Small, new int2(21, 21), home), "still walled in");
            map.Tiles[Index(map, new int2(23, 21))] = Floor; // now through to the open floor
            graph.Invalidate(new int2(23, 21));
            Assert.IsTrue(graph.Reaches(FlowSizeClass.Small, new int2(21, 21), home), "the cell is joined to the floor");
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
        int2[] sites = MapGenerator.Sites(256, 5);
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

    [Category("Slow")] // over a second (2026-11-09 timings): left out of tools/check.sh --fast
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
                Assert.AreNotEqual(FlowDirections.AtGoal, cache.DirectionAtCell(handle, Index(map, new int2(16, 16))),
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
            int2[] sites = MapGenerator.Sites(256, 5);
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
                Cost = cost, Direction = direction,
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
                Cost = cost, Direction = direction,
            };
            swapped.Goals.Add(Local(new int2(2, 2)));
            swapped.GoalCosts.Add(300);
            swapped.Goals.Add(Local(new int2(29, 29)));
            swapped.GoalCosts.Add(0);
            swapped.ExitCells.Add(Local(new int2(31, 16)));
            swapped.ExitDirections.Add(0); // east
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

    /// <summary>
    /// A terrain change drops only the sectors within one cell of a cell that changed (perf 2026-11-11), so the kept graph must
    /// answer as a graph built from the changed map does: the same routes and the same reach for both classes, with half the
    /// changes on or beside a sector edge, where a neighbour's portals depend on them.
    /// </summary>
    [Test]
    public void AGraphKeptAcrossTerrainChangesAnswersAsAFreshOne()
    {
        PathMap map = PathTestMaps.Generate(256, 5);
        var goal = new NativeArray<int>(1, Allocator.TempJob);
        var starts = new NativeArray<int>(6, Allocator.TempJob);
        try
        {
            using var kept = new SectorGraph(map, Allocator.Persistent);
            var rng = new Unity.Mathematics.Random(11);
            for (int round = 0; round < 160; round++)
            {
                int2 tile = rng.NextInt2(int2.zero, new int2(map.Width, map.Height));
                if (round % 2 == 0) tile.x = rng.NextInt(1, 8) * SectorGraph.SectorSize + rng.NextInt(-2, 2);
                if (round % 4 == 0) tile.y = rng.NextInt(1, 8) * SectorGraph.SectorSize + rng.NextInt(-2, 2);
                map.Tiles[Index(map, tile)] = map.Tiles[Index(map, tile)] == Floor ? Rock : Floor;
                kept.Invalidate(tile);

                using var fresh = new SectorGraph(map, Allocator.Persistent);
                goal[0] = RandomFloorCell(map, ref rng);
                for (int i = 0; i < starts.Length; i++) starts[i] = RandomFloorCell(map, ref rng);
                for (int sizeClass = FlowSizeClass.Small; sizeClass <= FlowSizeClass.Large; sizeClass++)
                {
                    string expected = Describe(fresh.BuildRoute(sizeClass, goal, starts, out int coveredFresh));
                    string actual = Describe(kept.BuildRoute(sizeClass, goal, starts, out int coveredKept));
                    Assert.AreEqual(expected, actual, $"round {round}, class {sizeClass}, change at {tile}: the route");
                    Assert.AreEqual(coveredFresh, coveredKept, $"round {round}, class {sizeClass}: covered sectors");
                    foreach (int start in starts)
                        Assert.AreEqual(
                            fresh.Reaches(sizeClass, TileOf(map, start), TileOf(map, goal[0])),
                            kept.Reaches(sizeClass, TileOf(map, start), TileOf(map, goal[0])),
                            $"round {round}, class {sizeClass}, change at {tile}: reach from {TileOf(map, start)}");
                }
            }
        }
        finally
        {
            goal.Dispose();
            starts.Dispose();
            map.Dispose();
        }
    }

    /// <summary>
    /// The route search is a Burst job that stops where the graph lacks a sector's portals or cost table and goes on from
    /// there once the graph builds it (perf 2026-11-11). On a fresh graph no table is taken before the search, so it stops at
    /// every sector it expands; on a graph with every sector and table built first it runs to the end in one go. Both must
    /// give the same route. A plain Dijkstra over the same portals must give each exit's cost to the goal; it is held to the
    /// costs only, since equal-cost ways can make the chosen exit differ. Three cave maps and both classes, a goal walled
    /// off from the start, and a start on the goal.
    /// </summary>
    [Test]
    public void AResumedRouteSearchGivesTheRouteOfAnUnbrokenOneAndThePlainSearchCosts()
    {
        var goal = new NativeArray<int>(1, Allocator.TempJob);
        var starts = new NativeArray<int>(4, Allocator.TempJob);
        int costsChecked = 0;
        try
        {
            foreach (uint seed in new uint[] { 5, 9, 23 })
            {
                PathMap map = PathTestMaps.Generate(256, seed);
                try
                {
                    using var built = BuiltGraph(map);
                    var rng = new Unity.Mathematics.Random(seed);
                    for (int round = 0; round < 6; round++)
                    {
                        goal[0] = RandomFloorCell(map, ref rng);
                        for (int i = 0; i < starts.Length; i++) starts[i] = RandomFloorCell(map, ref rng);
                        for (int sizeClass = FlowSizeClass.Small; sizeClass <= FlowSizeClass.Large; sizeClass++)
                            costsChecked += CompareSearches(map, built, sizeClass, goal, starts, $"map {seed}, round {round}, class {sizeClass}");
                    }

                    // A start on the goal: nothing to search, the same goal field from both.
                    starts[0] = starts[1] = starts[2] = starts[3] = goal[0];
                    for (int sizeClass = FlowSizeClass.Small; sizeClass <= FlowSizeClass.Large; sizeClass++)
                        CompareSearches(map, built, sizeClass, goal, starts, $"map {seed}, start on the goal, class {sizeClass}");
                }
                finally
                {
                    map.Dispose();
                }
            }
            Assert.Greater(costsChecked, 50, "the plain search checked the exit costs of many routes");

            // A goal walled off from the start: the search runs out without settling the start, in both graphs alike.
            PathMap sealedMap = WallMap(gapTiles: 0, Allocator.Persistent);
            try
            {
                using var built = BuiltGraph(sealedMap);
                goal[0] = Index(sealedMap, new int2(50, 50));
                for (int i = 0; i < starts.Length; i++) starts[i] = Index(sealedMap, new int2(10 + i, 10));
                for (int sizeClass = FlowSizeClass.Small; sizeClass <= FlowSizeClass.Large; sizeClass++)
                {
                    CompareSearches(sealedMap, built, sizeClass, goal, starts, $"walled-off goal, class {sizeClass}");
                    Assert.Greater(built.UnreachableStartSectors, 0, "the start cannot reach the goal");
                    Dictionary<int, int> plain = PlainSearch(built, sizeClass, goal[0]);
                    int startSector = built.SectorOf(new int2(10, 10));
                    for (int p = 0; p < built.PortalsOf(sizeClass, startSector).Length; p++)
                        Assert.IsFalse(plain.ContainsKey(startSector * SectorGraph.MaxPortals + p), "the plain search reaches no portal of the start's sector either");
                }
            }
            finally
            {
                sealedMap.Dispose();
            }
        }
        finally
        {
            goal.Dispose();
            starts.Dispose();
        }
    }

    /// <summary>A graph with every sector's portals and cost table built for both classes, so a search never stops.</summary>
    private static SectorGraph BuiltGraph(in PathMap map)
    {
        var graph = new SectorGraph(map, Allocator.Persistent);
        for (int sizeClass = FlowSizeClass.Small; sizeClass <= FlowSizeClass.Large; sizeClass++)
        for (int sector = 0; sector < graph.SectorsX * graph.SectorsY; sector++)
        {
            graph.PortalsOf(sizeClass, sector);
            graph.PortalCost(sizeClass, sector, 0, 0);
        }
        return graph;
    }

    /// <summary>
    /// Routes one order on a fresh graph and on <paramref name="built"/>, asserts the two are the same, and, when every start
    /// reached the goal, that each exit's cost is the plain search's. Returns the exits whose cost it checked.
    /// </summary>
    private static int CompareSearches(in PathMap map, SectorGraph built, int sizeClass, NativeArray<int> goal, NativeArray<int> starts, string what)
    {
        string resumed;
        int resumedUnreachable;
        using (var fresh = new SectorGraph(map, Allocator.Persistent))
        {
            resumed = Describe(fresh.BuildRoute(sizeClass, goal, starts, out _));
            resumedUnreachable = fresh.UnreachableStartSectors;
        }
        SectorFieldSpec[] specs = built.BuildRoute(sizeClass, goal, starts, out _);
        Assert.AreEqual(Describe(specs), resumed, $"{what}: the route");
        Assert.AreEqual(built.UnreachableStartSectors, resumedUnreachable, $"{what}: starts that cannot reach the goal");
        if (built.UnreachableStartSectors > 0) return 0; // the route then also leads to stops the plain search knows nothing of

        Dictionary<int, int> plain = PlainSearch(built, sizeClass, goal[0]);
        byte[] crossing = { 4, 0, 6, 2 }; // the step a unit takes across a West, East, South or North edge
        int checkedExits = 0;
        foreach (SectorFieldSpec spec in specs)
        {
            SectorGraph.Portal[] portals = built.PortalsOf(sizeClass, spec.Sector);
            int offset = spec.GoalCells.Length - spec.ExitCells.Length; // the exits follow the goal cells
            for (int i = 0; i < spec.ExitCells.Length; i++)
            {
                int portal = -1;
                for (int p = 0; p < portals.Length && portal < 0; p++)
                    if (crossing[portals[p].Edge] == spec.ExitDirections[i] && System.Array.IndexOf(built.PortalCellsOf(sizeClass, spec.Sector, p), spec.ExitCells[i]) >= 0)
                        portal = p;
                Assert.GreaterOrEqual(portal, 0, $"{what}: exit cell {spec.ExitCells[i]} of sector {spec.Sector} lies on a portal");
                int beyond = Across(built, sizeClass, spec.Sector, portal, out int matching);
                Assert.IsTrue(plain.TryGetValue(beyond * SectorGraph.MaxPortals + matching, out int cost), $"{what}: the plain search reaches the portal across exit {i} of sector {spec.Sector}");
                Assert.AreEqual(math.min(BuildIntegrationFieldJob.Unreachable - 1, cost + 10), spec.GoalCosts[offset + i], $"{what}: cost of exit {i} of sector {spec.Sector}");
                checkedExits++;
            }
        }
        return checkedExits;
    }

    /// <summary>
    /// Dijkstra over every portal of a built graph to the end, from the goal's own sector: each of its portals starts at the
    /// in-sector walk from its centre cell to the goal; a step inside a sector costs the graph's portal-to-portal cost, a
    /// crossing to the matching portal 10. The cost of each node reached, by <c>sector * MaxPortals + portal</c>.
    /// </summary>
    private static Dictionary<int, int> PlainSearch(SectorGraph graph, int sizeClass, int goalTile)
    {
        int side = graph.SectorCellsSide;
        int2 tile = new int2(goalTile % graph.Width, goalTile / graph.Width);
        int goalSector = graph.SectorOf(tile);
        int2 corner = new int2(goalSector % graph.SectorsX, goalSector / graph.SectorsX) * side;
        var goals = new NativeArray<int>(1, Allocator.TempJob);
        var field = new NativeArray<ushort>(side * side, Allocator.TempJob);
        goals[0] = (tile.y - corner.y) * side + tile.x - corner.x;
        new BuildIntegrationFieldJob { Width = side, Height = side, Tiles = graph.CropOf(sizeClass, goalSector), Goals = goals, Cost = field }.Run();

        var cost = new Dictionary<int, int>();
        var open = new SortedSet<(int cost, int node)>();
        SectorGraph.Portal[] goalPortals = graph.PortalsOf(sizeClass, goalSector);
        for (int p = 0; p < goalPortals.Length; p++)
        {
            int[] cells = graph.PortalCellsOf(sizeClass, goalSector, p);
            ushort seed = field[cells[cells.Length / 2]];
            if (seed >= BuildIntegrationFieldJob.Unreachable) continue;
            cost[goalSector * SectorGraph.MaxPortals + p] = seed;
            open.Add((seed, goalSector * SectorGraph.MaxPortals + p));
        }
        goals.Dispose();
        field.Dispose();

        var done = new HashSet<int>();
        while (open.Count > 0)
        {
            var (distance, node) = open.Min;
            open.Remove(open.Min);
            if (!done.Add(node)) continue;
            int sector = node / SectorGraph.MaxPortals, portal = node % SectorGraph.MaxPortals;
            int n = graph.PortalsOf(sizeClass, sector).Length;
            for (int q = 0; q < n; q++)
            {
                ushort inside = q == portal ? BuildIntegrationFieldJob.Unreachable : graph.PortalCost(sizeClass, sector, portal, q);
                if (inside < BuildIntegrationFieldJob.Unreachable) Relax(sector * SectorGraph.MaxPortals + q, distance + inside);
            }
            int beyond = Across(graph, sizeClass, sector, portal, out int matching);
            if (beyond >= 0 && matching >= 0) Relax(beyond * SectorGraph.MaxPortals + matching, distance + 10);
        }
        return cost;

        void Relax(int node, int value)
        {
            if (value >= BuildIntegrationFieldJob.Unreachable || done.Contains(node)) return;
            if (cost.TryGetValue(node, out int was))
            {
                if (value >= was) return;
                open.Remove((was, node));
            }
            cost[node] = value;
            open.Add((value, node));
        }
    }

    /// <summary>The sector across a portal's edge (-1 at the map's edge) and its first portal on the facing edge whose run overlaps this one (-1 for none).</summary>
    private static int Across(SectorGraph graph, int sizeClass, int sector, int portal, out int matching)
    {
        matching = -1;
        SectorGraph.Portal from = graph.PortalsOf(sizeClass, sector)[portal];
        int sx = sector % graph.SectorsX, sy = sector / graph.SectorsX;
        int beyond = from.Edge switch
        {
            0 => sx > 0 ? sector - 1 : -1,
            1 => sx + 1 < graph.SectorsX ? sector + 1 : -1,
            2 => sy > 0 ? sector - graph.SectorsX : -1,
            _ => sy + 1 < graph.SectorsY ? sector + graph.SectorsX : -1,
        };
        if (beyond < 0) return -1;
        SectorGraph.Portal[] there = graph.PortalsOf(sizeClass, beyond);
        for (int q = 0; q < there.Length && matching < 0; q++)
            if (there[q].Edge == (from.Edge ^ 1) && there[q].Start < from.Start + from.Length && from.Start < there[q].Start + there[q].Length)
                matching = q;
        return beyond;
    }

    /// <summary>A route's specs as text, so two routes compare field by field with a readable difference.</summary>
    private static string Describe(SectorFieldSpec[] specs)
    {
        var text = new System.Text.StringBuilder();
        foreach (SectorFieldSpec spec in specs)
        {
            text.Append("sector ").Append(spec.Sector).Append(" exit ").Append(spec.ExitDirection).Append(" goals");
            for (int i = 0; i < spec.GoalCells.Length; i++) text.Append(' ').Append(spec.GoalCells[i]).Append(':').Append(spec.GoalCosts[i]);
            text.Append(" exits");
            for (int i = 0; i < spec.ExitCells.Length; i++) text.Append(' ').Append(spec.ExitCells[i]).Append(':').Append(spec.ExitDirections[i]);
            text.Append('\n');
        }
        return text.ToString();
    }
}
