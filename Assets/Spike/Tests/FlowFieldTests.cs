using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Spike;

/// <summary>
/// Correctness tests for the spike's flow fields: the integration field (Dial's algorithm), the
/// direction field, the clearance pass and the size-class filter, and the cache's invalidation.
/// </summary>
public class FlowFieldTests
{
    private const byte Floor = SpikeMap.Floor;
    private const byte Rock = SpikeMap.Rock;

    private static int Index(in SpikeMap map, int2 tile) => tile.y * map.Width + tile.x;

    /// <summary>A size x size map of floor with a blocked border ring, so fields are never open at the edge.</summary>
    private static SpikeMap FlatMap(int size, Allocator allocator)
    {
        var map = new SpikeMap(size, size, new NativeArray<byte>(size * size, allocator));
        for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Floor;
        for (int k = 0; k < size; k++)
        {
            map.Tiles[k] = Rock;
            map.Tiles[(size - 1) * size + k] = Rock;
            map.Tiles[k * size] = Rock;
            map.Tiles[k * size + size - 1] = Rock;
        }
        return map;
    }

    /// <summary>
    /// Two rock walls across a 32-wide map. The first runs from the top border to y=20 with a 1-tile
    /// gap at y=15, so a wide passage rounds its south end. The second spans the whole height with a
    /// gap at y=15 that is 1 tile wide, or 3 when <paramref name="threeTileGap"/> is set.
    /// </summary>
    private static SpikeMap WallMap(int size, bool threeTileGap, Allocator allocator)
    {
        var map = new SpikeMap(size, size, new NativeArray<byte>(size * size, allocator));
        for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Floor;
        for (int k = 0; k < size; k++)
        {
            map.Tiles[k] = Rock;
            map.Tiles[(size - 1) * size + k] = Rock;
            map.Tiles[k * size] = Rock;
            map.Tiles[k * size + size - 1] = Rock;
        }
        for (int y = 1; y <= 20; y++)
            if (y != 15) map.Tiles[y * size + 10] = Rock;
        for (int y = 1; y < size - 1; y++)
        {
            bool gap = threeTileGap && y >= 14 && y <= 16;
            if (!gap) map.Tiles[y * size + 20] = Rock;
        }
        return map;
    }

    private static NativeArray<ushort> Cost(in SpikeMap map, NativeArray<byte> tiles, Allocator allocator, params int2[] goals)
    {
        var cost = new NativeArray<ushort>(map.Width * map.Height, allocator);
        var goalIndices = new NativeArray<int>(goals.Length, Allocator.TempJob);
        for (int i = 0; i < goals.Length; i++) goalIndices[i] = Index(map, goals[i]);
        new BuildIntegrationFieldJob
        {
            Width = map.Width, Height = map.Height, Tiles = tiles, Goals = goalIndices, Cost = cost,
        }.Run();
        goalIndices.Dispose();
        return cost;
    }

    private static NativeArray<byte> Directions(in SpikeMap map, NativeArray<byte> tiles, NativeArray<ushort> cost, Allocator allocator)
    {
        var direction = new NativeArray<byte>(map.Width * map.Height, allocator);
        new BuildDirectionFieldJob
        {
            Width = map.Width, Height = map.Height, Tiles = tiles, Cost = cost, Direction = direction,
        }.Run(map.Width * map.Height);
        return direction;
    }

    private static NativeArray<byte> Clearance(in SpikeMap map, NativeArray<byte> tiles, Allocator allocator)
    {
        var clearance = new NativeArray<byte>(map.Width * map.Height, allocator);
        new ClearanceJob
        {
            Width = map.Width, Height = map.Height, Tiles = tiles, Clearance = clearance,
        }.Run();
        return clearance;
    }

    [Test]
    public void OpenMapCostIsOctileDistance()
    {
        using SpikeMap map = FlatMap(64, Allocator.TempJob);
        using var cost = Cost(map, map.Tiles, Allocator.TempJob, new int2(32, 32));

        int mismatches = 0;
        int2 firstBad = default;
        int firstBadCost = -1, firstBadExpected = -1;
        for (int y = 1; y < map.Height - 1; y++)
        for (int x = 1; x < map.Width - 1; x++)
        {
            int dx = math.abs(x - 32), dy = math.abs(y - 32);
            int expected = 10 * math.max(dx, dy) + 4 * math.min(dx, dy);
            if (cost[Index(map, new int2(x, y))] == expected) continue;
            if (mismatches == 0)
            {
                firstBad = new int2(x, y);
                firstBadCost = cost[Index(map, firstBad)];
                firstBadExpected = expected;
            }
            mismatches++;
        }
        Assert.AreEqual(0, mismatches, $"{mismatches} cells are not octile; first {firstBad}: got {firstBadCost}, expected {firstBadExpected}");
    }

    [Test]
    public void BlockedAndEnclosedCellsAreUnreachable()
    {
        SpikeMap map = FlatMap(64, Allocator.TempJob);
        try
        {
            var rock = new int2(10, 10);
            map.Tiles[Index(map, rock)] = Rock;
            var enclosed = new int2(20, 20);
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0) map.Tiles[Index(map, enclosed + new int2(dx, dy))] = Rock;

            using var blocked = Cost(map, map.Tiles, Allocator.TempJob, new int2(32, 32));
            Assert.AreEqual(BuildIntegrationFieldJob.Unreachable, blocked[Index(map, rock)], "a rock cell must be unreachable");
            Assert.AreEqual(BuildIntegrationFieldJob.Unreachable, blocked[Index(map, enclosed)], "a floor cell inside a rock ring must be unreachable");
            Assert.AreEqual(70, blocked[Index(map, new int2(25, 32))], "a cell next to the enclosed one must still be reachable");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void NoCornerCutting()
    {
        SpikeMap map = FlatMap(64, Allocator.TempJob);
        try
        {
            map.Tiles[Index(map, new int2(11, 10))] = Rock;
            using var cost = Cost(map, map.Tiles, Allocator.TempJob, new int2(11, 11));
            Assert.AreEqual(10, cost[Index(map, new int2(10, 11))], "the goal's west neighbour is one straight step");
            Assert.AreEqual(20, cost[Index(map, new int2(10, 10))],
                "(10,10) must go around the rock corner through (10,11), not diagonally past it");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void DirectionsDescend()
    {
        using SpikeMap map = MapGenerator.Generate(128, 11, Allocator.TempJob);
        int2 goal = MapGenerator.HqSites(128)[0];
        using var cost = Cost(map, map.Tiles, Allocator.TempJob, goal);
        using var direction = Directions(map, map.Tiles, cost, Allocator.TempJob);

        int descending = 0, violations = 0;
        int2 firstBad = default;
        byte firstBadDirection = 0;
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            int cell = y * map.Width + x;
            byte dir = direction[cell];
            if (map.Tiles[cell] != Floor) { if (dir != FlowDirections.None) violations++; continue; }
            if (cost[cell] == BuildIntegrationFieldJob.Unreachable) { if (dir != FlowDirections.None) violations++; continue; }
            if (cost[cell] == 0) { if (dir != FlowDirections.AtGoal) violations++; continue; }
            if (dir >= 8)
            {
                violations++;
                if (violations == 1) firstBad = new int2(x, y);
                continue;
            }
            int2 next = new int2(x, y) + FlowDirections.Step(dir);
            bool inside = (uint)next.x < (uint)map.Width && (uint)next.y < (uint)map.Height;
            if (!inside || map.Tiles[next.y * map.Width + next.x] != Floor || cost[next.y * map.Width + next.x] >= cost[cell])
            {
                if (violations == 0) { firstBad = new int2(x, y); firstBadDirection = dir; }
                violations++;
                continue;
            }
            descending++;
        }
        Assert.AreEqual(0, violations, $"{violations} direction cells do not descend; first {firstBad} direction {firstBadDirection}");
        Assert.Greater(descending, 1000, "the map should have plenty of reachable interior cells");
    }

    [Test]
    public void MultipleGoalsSpreadTheField()
    {
        SpikeMap map = FlatMap(64, Allocator.TempJob);
        try
        {
            map.Tiles[Index(map, new int2(20, 20))] = Rock;
            var first = new int2(10, 10);
            var second = new int2(50, 45);
            using var a = Cost(map, map.Tiles, Allocator.TempJob, first);
            using var b = Cost(map, map.Tiles, Allocator.TempJob, second);
            using var both = Cost(map, map.Tiles, Allocator.TempJob, first, second);

            int mismatches = 0;
            int2 firstBad = default;
            for (int i = 0; i < a.Length; i++)
            {
                int expected = math.min((int)a[i], (int)b[i]);
                if (both[i] == expected) continue;
                if (mismatches == 0) firstBad = new int2(i % map.Width, i / map.Width);
                mismatches++;
            }
            Assert.AreEqual(0, mismatches, $"a two-goal field is not the minimum of the two single-goal fields; first {firstBad}");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void ClearanceIsDistanceToRock()
    {
        var map = new SpikeMap(32, 32, new NativeArray<byte>(32 * 32, Allocator.TempJob));
        for (int i = 0; i < map.Tiles.Length; i++) map.Tiles[i] = Floor;
        map.Tiles[Index(map, new int2(16, 16))] = Rock;

        using var clearance = Clearance(map, map.Tiles, Allocator.TempJob);
        Assert.AreEqual(0, clearance[Index(map, new int2(16, 16))], "a rock tile has no clearance");
        Assert.AreEqual(1, clearance[Index(map, new int2(17, 16))], "one tile from rock");
        Assert.AreEqual(1, clearance[Index(map, new int2(15, 15))], "diagonally one tile from rock");
        Assert.AreEqual(2, clearance[Index(map, new int2(18, 18))], "diagonally two tiles from rock");
        Assert.AreEqual(ClearanceJob.MaxClearance, clearance[Index(map, new int2(1, 1))], "far from rock it is capped");
        map.Dispose();
    }

    [Test]
    public void LargeUnitsAvoidNarrowGaps()
    {
        var start = new int2(5, 15);
        var goal = new int2(25, 15);
        byte largeMinimum = FlowSizeClass.MinClearance(FlowSizeClass.Large);

        // Both gaps open: the small field takes the 1-tile gap, the large field must take the 3-tile one.
        using (SpikeMap map = WallMap(32, true, Allocator.TempJob))
        {
            using var smallCost = Cost(map, map.Tiles, Allocator.TempJob, goal);
            using var smallDirection = Directions(map, map.Tiles, smallCost, Allocator.TempJob);
            Assert.AreEqual(200, smallCost[Index(map, start)], "the small field's straight route through both gaps");
            Assert.AreEqual(0, smallDirection[Index(map, new int2(10, 15))], "the small field steps east through the 1-tile gap");

            using var clearance = Clearance(map, map.Tiles, Allocator.TempJob);
            Assert.AreEqual(1, clearance[Index(map, new int2(10, 15))], "the 1-tile gap's clearance");
            Assert.AreEqual(2, clearance[Index(map, new int2(20, 15))], "the 3-tile gap's middle clearance");
            using var largeTiles = FlowFilter.Build(map, map.Tiles, clearance, largeMinimum, Allocator.TempJob);
            Assert.AreEqual(Rock, largeTiles[Index(map, new int2(10, 15))], "the 1-tile gap is sealed for large units");
            Assert.AreEqual(Floor, largeTiles[Index(map, new int2(20, 15))], "the 3-tile gap's middle stays open");

            using var largeCost = Cost(map, largeTiles, Allocator.TempJob, goal);
            using var largeDirection = Directions(map, largeTiles, largeCost, Allocator.TempJob);
            Assert.AreEqual(FlowDirections.None, largeDirection[Index(map, new int2(10, 15))], "a sealed cell has no direction");
            Assert.AreNotEqual(BuildIntegrationFieldJob.Unreachable, largeCost[Index(map, start)],
                "the large field reaches the start through the 3-tile gap");
            Assert.Greater(largeCost[Index(map, start)], 200,
                "the large field must round the first wall's south end instead of using the 1-tile gap");
            Assert.AreEqual(0, largeDirection[Index(map, new int2(20, 15))], "the large field steps east through the 3-tile gap");
        }

        // Only the 1-tile gap: the large class cannot reach the far side at all.
        using (SpikeMap map = WallMap(32, false, Allocator.TempJob))
        {
            using var clearance = Clearance(map, map.Tiles, Allocator.TempJob);
            using var largeTiles = FlowFilter.Build(map, map.Tiles, clearance, largeMinimum, Allocator.TempJob);
            using var cost = Cost(map, largeTiles, Allocator.TempJob, goal);
            using var direction = Directions(map, largeTiles, cost, Allocator.TempJob);
            Assert.AreEqual(BuildIntegrationFieldJob.Unreachable, cost[Index(map, start)],
                "with only the 1-tile gap the large field leaves the start unreachable");
            Assert.AreEqual(FlowDirections.None, direction[Index(map, start)]);
        }
    }

    [Test]
    public void LazyRebuildOnlyRebuildsFieldsWithFollowers()
    {
        SpikeMap map = FlatMap(64, Allocator.TempJob);
        try
        {
            using var cache = new FlowFieldCache(map, Allocator.Persistent);
            int followed = cache.Acquire(new int2(10, 10));
            int dropped = cache.Acquire(new int2(50, 50));
            cache.RebuildDirty(8).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(followed));
            Assert.IsTrue(cache.IsReady(dropped));
            long rebuilds = cache.RebuildCount;

            // A field nobody follows is released, so a terrain change must not spend a rebuild on it.
            cache.Release(dropped);
            map.Tiles[Index(map, new int2(32, 32))] = Rock;
            cache.Invalidate(new int2(32, 32));
            cache.RebuildDirty(8).Complete();
            cache.CompleteRebuilds();
            Assert.AreEqual(1, cache.LiveCount);
            Assert.AreEqual(rebuilds + 1, cache.RebuildCount, "only the followed field is rebuilt");
            Assert.IsTrue(cache.IsReady(followed), "the followed field is rebuilt");

            // A field released while its rebuild is in flight is freed by that rebuild, not rebuilt again.
            long beforeRelease = cache.RebuildCount;
            int inFlight = cache.Acquire(new int2(20, 20));
            map.Tiles[Index(map, new int2(40, 40))] = Rock;
            cache.Invalidate(new int2(40, 40));
            cache.RebuildDirty(8);
            cache.Release(inFlight);
            cache.CompleteRebuilds();
            Assert.AreEqual(1, cache.LiveCount, "the released field is gone, not left live");
            Assert.AreEqual(beforeRelease + 2, cache.RebuildCount,
                "the two dirty fields are rebuilt once each; the released one is not rebuilt again");
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public void CacheRebuildsFieldsAfterTerrainChanges()
    {
        SpikeMap map = FlatMap(64, Allocator.TempJob);
        try
        {
            using var cache = new FlowFieldCache(map, Allocator.Persistent);
            var goal = new int2(32, 32);
            var straight = new int2(32, 40);

            int field = cache.Acquire(goal);
            Assert.AreEqual(1, cache.LiveCount);
            Assert.IsFalse(cache.IsReady(field), "a fresh field is dirty until it is rebuilt");
            cache.RebuildDirty(8).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(field));
            Assert.IsTrue(cache.TryGetField(goal, FlowSizeClass.Small, out FlowField built));
            Assert.IsTrue(built.Ready);
            // The field's goals are the click plus its spread, which reaches (32,35): five straight steps.
            Assert.AreEqual(50, built.Cost[Index(map, straight)], "the cheapest goal is five straight steps away");
            Assert.AreEqual(FlowDirections.AtGoal, built.DirectionAt(goal));

            // Rock on the straight route to (32,35) means one diagonal step (14) replaces a straight one.
            var rock = new int2(32, 36);
            map.Tiles[Index(map, rock)] = Rock;
            cache.Invalidate(rock);
            Assert.IsFalse(cache.IsReady(field), "a terrain change dirties the field");
            cache.RebuildDirty(8).Complete();
            cache.CompleteRebuilds();
            Assert.IsTrue(cache.IsReady(field));
            Assert.IsTrue(cache.TryGetField(goal, FlowSizeClass.Small, out FlowField rebuilt));
            Assert.AreEqual(BuildIntegrationFieldJob.Unreachable, rebuilt.Cost[Index(map, rock)]);
            Assert.AreEqual(54, rebuilt.Cost[Index(map, straight)], "the detour around the rock adds one diagonal step");

            // The two size classes are separate entries, and release drops the refcount.
            int large = cache.Acquire(goal, FlowSizeClass.Large);
            Assert.AreEqual(2, cache.LiveCount);
            Assert.IsTrue(cache.TryGetField(goal, FlowSizeClass.Large, out FlowField largeField));
            Assert.AreEqual(FlowSizeClass.Large, largeField.SizeClass);
            cache.Release(large);
            cache.Release(field);
            Assert.AreEqual(0, cache.LiveCount, "releasing the last follower frees the field");
            Assert.Greater(cache.RebuildCount, 0);
        }
        finally
        {
            map.Dispose();
        }
    }
}
