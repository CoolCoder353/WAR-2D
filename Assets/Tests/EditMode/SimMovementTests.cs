using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Pathing;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>Flow following, separation, off-route recovery and holding to fight.</summary>
public class SimMovementTests
{
    /// <summary>64×64 with a wall down x = 32 that has one 3-tile gap near the top.</summary>
    private static MapStore WallWithGap()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                bool border = x == 0 || y == 0 || x == 63 || y == 63;
                bool wall = x == 32 && !(y >= 50 && y <= 52);
                row[x] = border ? 'B' : wall ? '#' : '.';
            }
            rows[r] = new string(row);
        }
        return MapStore.FromAscii(rows);
    }

    [Test]
    public void UnitReachesGoalAroundAWall()
    {
        using var sim = new SimHarness(WallWithGap());
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(54, 10), a[0]);
        var goal = new float2(54.5f, 10.5f);
        for (int t = 0; t < 20 * 20; t++)
        {
            sim.Tick();
            if (math.distance(sim.UnitById(a[0]).Position, goal) <= 1.5f) return;
        }
        Assert.Fail($"unit ended at {sim.UnitById(a[0]).Position}");
    }

    /// <summary>
    /// Owner report (2026-10-10): units got stuck on walls. Tile x = 33 is floor, but its 2×2 field cell
    /// (tiles 32–33) holds wall, so the field has no direction there; the unit must step back onto the
    /// field rather than head straight at the goal through the wall.
    /// </summary>
    [Test]
    public void OrderOntoRockStopsAtTheNearestFloorOnTheUnitsSide()
    {
        using var sim = new SimHarness(WallWithGap());
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(32, 10), a[0]); // the wall itself (a minimap click on unknown rock)
        var goal = new float2(31.5f, 10.5f);
        for (int t = 0; t < 20 * 20; t++) sim.Tick();
        Assert.LessOrEqual(math.distance(sim.UnitById(a[0]).Position, goal), 1.5f, $"unit ended at {sim.UnitById(a[0]).Position}");
    }

    /// <summary>A diagonal staircase of rock from (16, 8) to (40, 32) with a 1-tile gap, odd-aligned against the 2×2 field cells.</summary>
    private static MapStore Staircase()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                bool border = x == 0 || y == 0 || x == 63 || y == 63;
                bool stair = x >= 17 && x <= 47 && (x - y == 9 || x - y == 10) && y != 25;
                row[x] = border ? 'B' : stair ? '#' : '.';
            }
            rows[r] = new string(row);
        }
        return MapStore.FromAscii(rows);
    }

    [Test]
    public void CrowdCrossesAStaircaseWallThroughAOneTileGap()
    {
        using var sim = new SimHarness(Staircase());
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int k = 0; k < 12; k++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(40.5f + k % 4, 8.5f + k / 4)));
        sim.Tick();
        int[] ids = boxes.ConvertAll(b => b[0]).ToArray();
        sim.Move(SimHarness.OwnerA, new int2(14, 40), ids);
        for (int t = 0; t < 20 * 60; t++) sim.Tick();
        int stuck = 0;
        string where = "";
        foreach (int id in ids)
        {
            float2 p = sim.UnitById(id).Position;
            if (math.distance(p, new float2(14.5f, 40.5f)) > 4f) { stuck++; where += $" {p}"; }
        }
        Assert.AreEqual(0, stuck, $"stuck at{where}");
    }

    [Test]
    public void CrowdCrossesAGeneratedCaveMap([Values(1u, 7u, 42u)] uint seed)
    {
        const int size = 128;
        using var sim = new SimHarness(MapStore.Generate(size, seed, 0.05f));
        int2[] sites = WAR2D.World.MapGenerator.Sites(size, seed);
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int k = 0; k < 40; k++) boxes.Add(sim.Spawn(SimHarness.OwnerA, (float2)sites[0] + new float2(k % 8 - 3.5f, k / 8 - 2f)));
        sim.Tick();
        int[] ids = boxes.ConvertAll(b => b[0]).ToArray();
        float2 goal = (float2)sites[sites.Length - 1] + 0.5f;
        sim.Move(SimHarness.OwnerA, sites[sites.Length - 1], ids);
        for (int t = 0; t < 20 * 120; t++) sim.Tick();
        int stuck = 0;
        string where = "";
        foreach (Unit u in sim.Units())
        {
            if (math.distance(u.Position, goal) <= 8f) continue;
            stuck++;
            if (stuck <= 6) where += $" {u.Position}";
        }
        Assert.AreEqual(0, stuck, $"{stuck} of {ids.Length} short of {goal}, e.g.{where}");
    }

    [Test]
    public void OneOwnerCannotFillTheOrderTable()
    {
        using MapStore open = WallWithGap();
        using var book = new WAR2D.Sim.OrderBook(open.Grid);
        var starts = new Unity.Collections.NativeArray<int>(1, Unity.Collections.Allocator.Temp);
        try
        {
            starts[0] = 10 * open.Grid.Width + 40;
            var granted = new System.Collections.Generic.HashSet<int>();
            for (int x = 0; x < 2 * WAR2D.Sim.OrderBook.MaxOrdersPerOwner; x++)
            {
                int handle = book.Issue(new int2(2 + x % 40, 2 + x / 40 * 3), 0, starts, 1, owner: 7);
                if (handle >= 0) granted.Add(handle);
            }
            Assert.AreEqual(WAR2D.Sim.OrderBook.MaxOrdersPerOwner, granted.Count);
            Assert.GreaterOrEqual(book.Issue(new int2(60, 10), 0, starts, 1, owner: 8), 0, "another owner is unaffected");
        }
        finally { starts.Dispose(); }
    }

    [Test]
    public void TheGamesOwnOrdersLeaveRoomForAPlayersClick()
    {
        using MapStore open = WallWithGap();
        using var book = new WAR2D.Sim.OrderBook(open.Grid);
        var starts = new Unity.Collections.NativeArray<int>(1, Unity.Collections.Allocator.Temp);
        try
        {
            starts[0] = 10 * open.Grid.Width + 40;
            var granted = new System.Collections.Generic.HashSet<int>();
            for (int x = 0; x < WAR2D.Sim.OrderBook.MaxOrdersPerOwner; x++)
            {
                int handle = book.Issue(new int2(2 + x % 25, 2 + x / 25 * 3), 0, starts, 1, owner: 7, background: true);
                if (handle >= 0) granted.Add(handle);
            }
            Assert.AreEqual(WAR2D.Sim.OrderBook.MaxBackgroundOrdersPerOwner, granted.Count);
            Assert.GreaterOrEqual(book.Issue(new int2(40, 40), 0, starts, 1, owner: 7), 0, "the player's own order");
        }
        finally { starts.Dispose(); }
    }

    /// <summary>A rock wall three thick down x = 30..32 with one gap of <paramref name="tiles"/> tiles around y = 32.</summary>
    private static MapStore GapMap(int tiles)
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                bool border = x == 0 || y == 0 || x == 63 || y == 63;
                bool gap = y >= 32 - tiles / 2 && y < 32 - tiles / 2 + tiles;
                row[x] = border ? 'B' : x >= 30 && x <= 32 && !gap ? '#' : '.';
            }
            rows[r] = new string(row);
        }
        return MapStore.FromAscii(rows);
    }

    /// <summary>
    /// Two bodies wider than a tile, heading for the tile at a gap's mouth from either side of it: with equal pushes they
    /// balanced just short of it and stood there for good.
    /// </summary>
    [TestCase(UnitType.Heavy)]
    [TestCase(UnitType.Artillery)]
    public void TwoBigUnitsMeetingAtAGapBothGetThrough(UnitType type)
    {
        using var sim = new SimHarness(GapMap(3));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(28.68f, 31.98f), type);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(28.69f, 33.03f), type);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(50, 32), a[0], b[0]);
        sim.TickSeconds(30f);
        Assert.Greater(sim.UnitById(a[0]).Position.x, 33f, $"ended at {sim.UnitById(a[0]).Position}");
        Assert.Greater(sim.UnitById(b[0]).Position.x, 33f, $"ended at {sim.UnitById(b[0]).Position}");
    }

    /// <summary>Two bodies side by side at a one-tile gap: pushed apart, neither was on the line that fits through it.</summary>
    [TestCase(UnitType.Tank)]
    [TestCase(UnitType.Builder)]
    public void TwoUnitsSideBySideAtAOneTileGapBothGetThrough(UnitType type)
    {
        using var sim = new SimHarness(GapMap(1));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(29.6f, 32.25f), type);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(29.6f, 32.75f), type);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(50, 32), a[0], b[0]);
        sim.TickSeconds(10f);
        Assert.Greater(sim.UnitById(a[0]).Position.x, 33f, $"ended at {sim.UnitById(a[0]).Position}");
        Assert.Greater(sim.UnitById(b[0]).Position.x, 33f, $"ended at {sim.UnitById(b[0]).Position}");
    }

    /// <summary>64x64 cut in two by a rock wall down x = 32 with no gap.</summary>
    private static MapStore Sealed()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++) rows[r] = r == 0 || r == 63 ? new string('B', 64) : "B" + new string('.', 31) + "#" + new string('.', 30) + "B";
        return MapStore.FromAscii(rows);
    }

    [Test]
    public void AMoveToAWalledOffGoalEndsAtTheNearestFloor()
    {
        using var sim = new SimHarness(Sealed());
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 40.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(50, 20), a[0]);
        sim.TickSeconds(15f);
        Unit unit = sim.UnitById(a[0]);
        Assert.AreEqual(Stances.Idle, unit.Stance, "the order is over: it holds no order slot, and the unit can take new work");
        Assert.AreEqual(-1, unit.OrderSlot);
        Assert.Less(math.distance(unit.Position, new float2(31.5f, 20.5f)), 2f, $"ended at {unit.Position}");
    }

    [Test]
    public void AnAttackMoveWaitsAtAWallAndGoesOnThroughWhenItFalls()
    {
        using var sim = new SimHarness(Sealed());
        int2 gap = new int2(32, 40);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = gap, TileType = TileType.Ground });
        sim.PlaceWall(SimHarness.OwnerB, gap);
        sim.Tick();
        sim.ForceBuiltWall(gap);
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 40.5f));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(50, 40), a[0]);
        sim.TickSeconds(8f);
        Assert.AreEqual(Stances.AttackMove, sim.UnitById(a[0]).Stance, "still on its way");
        Assert.Less(sim.UnitById(a[0]).Position.x, 32f);
        sim.Em.CompleteAllTrackedJobs();
        sim.Context.Data.WallDamage.Enqueue(new TileWork { Tile = sim.Map.Grid.Index(gap), Amount = 1e6f });
        sim.TickSeconds(8f);
        Assert.Greater(sim.UnitById(a[0]).Position.x, 40f, "no new order needed");
    }

    /// <summary>A unit pushed into a pocket of a sector its route already covers: the route used to count the sector as done.</summary>
    [Test]
    public void AUnitInAnotherPocketOfACoveredSectorStillFindsItsWay()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++) row[x] = x == 0 || y == 0 || x == 63 || y == 63 ? 'B' : y == 15 && x < 32 ? '#' : '.';
            rows[r] = new string(row);
        }
        using var sim = new SimHarness(MapStore.FromAscii(rows));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 5.5f));
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(11.5f, 5.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(50, 8), a[0], b[0]);
        for (int i = 0; i < 3; i++) sim.Tick();
        sim.Teleport(a[0], new float2(10.5f, 25.5f));
        sim.TickSeconds(15f);
        Assert.Less(math.distance(sim.UnitById(a[0]).Position, new float2(50.5f, 8.5f)), 3f, $"ended at {sim.UnitById(a[0]).Position}");
    }

    [Test]
    public void SnapGoalFindsNearestFloorOrNone()
    {
        using MapStore open = WallWithGap();
        Assert.IsTrue(WAR2D.Sim.OrderBook.SnapGoal(open.Grid, new int2(32, 10), new int2(40, 10), 12, out int2 snapped));
        Assert.AreEqual(new int2(33, 10), snapped, "ties go to the units' side");
        Assert.IsTrue(WAR2D.Sim.OrderBook.SnapGoal(open.Grid, new int2(5, 5), new int2(40, 10), 12, out snapped));
        Assert.AreEqual(new int2(5, 5), snapped, "a walkable goal is kept");

        using MapStore rock = MapStore.FromAscii("#####", "#####", "#####");
        Assert.IsFalse(WAR2D.Sim.OrderBook.SnapGoal(rock.Grid, new int2(2, 1), new int2(0, 0), 12, out _));
    }

    [Test]
    public void UnitBesideAWallInABlockedFieldCellStillGoesAround()
    {
        using var sim = new SimHarness(WallWithGap());
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(33.5f, 10.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(10, 10), a[0]);
        var goal = new float2(10.5f, 10.5f);
        for (int t = 0; t < 20 * 40; t++)
        {
            sim.Tick();
            if (math.distance(sim.UnitById(a[0]).Position, goal) <= 1.5f) return;
        }
        Assert.Fail($"unit ended at {sim.UnitById(a[0]).Position}");
    }

    [Test]
    public void UnitsShareOneFieldPerOrder()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        var ids = new int[50];
        var boxes = new int[50][];
        for (int i = 0; i < 50; i++) boxes[i] = sim.Spawn(SimHarness.OwnerA, new float2(5.5f + i % 10, 5.5f + i / 10));
        sim.Tick();
        for (int i = 0; i < 50; i++) ids[i] = boxes[i][0];
        sim.Move(SimHarness.OwnerA, new int2(80, 80), ids);
        sim.Tick();
        Assert.AreEqual(1, sim.Context.Orders.LiveCount);
        Assert.AreEqual(1, sim.Context.Orders.Cache.LiveCountOfClass(FlowSizeClass.Small));
    }

    [Test]
    public void SeparationPushesOverlappingUnitsApart()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(20.5f, 20.5f));
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(20.5f, 20.5f));
        for (int i = 0; i < 10; i++) sim.Tick();
        Unit ua = sim.UnitById(a[0]), ub = sim.UnitById(b[0]);
        Assert.GreaterOrEqual(math.distance(ua.Position, ub.Position), 0.9f * (ua.Radius + ub.Radius));
    }

    [Test]
    public void PushedOffRouteRequestsTheSector()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(160));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(11.5f, 10.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(20, 20), a[0], b[0]);
        for (int i = 0; i < 3; i++) sim.Tick();

        var far = new float2(140.5f, 140.5f);
        int handle = sim.UnitById(a[0]).OrderSlot;
        Assert.GreaterOrEqual(handle, 0);
        int sector = sim.Context.Orders.Table.SectorOf((int2)far);
        Assert.IsFalse(sim.Context.Orders.Cache.CoversSector(handle, sector));
        sim.Teleport(a[0], far);
        bool covered = false;
        for (int i = 0; i < 40 && !covered; i++)
        {
            sim.Tick();
            covered = sim.Context.Orders.Cache.CoversSector(handle, sector);
        }
        Assert.IsTrue(covered, "the route should grow to cover the unit's sector");
        float before = math.distance(sim.UnitById(a[0]).Position, new float2(20.5f, 20.5f));
        for (int i = 0; i < 20; i++) sim.Tick();
        Assert.Less(math.distance(sim.UnitById(a[0]).Position, new float2(20.5f, 20.5f)), before);
    }

    [Test]
    public void StoppedToFightHoldsPosition()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Spawn(SimHarness.OwnerB, new float2(13.5f, 10.5f));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(50, 50), a[0]);
        for (int i = 0; i < 5; i++) sim.Tick();
        Unit unit = sim.UnitById(a[0]);
        Assert.Less(math.length(unit.Velocity), 1e-3f);
        Assert.AreEqual(new float2(10.5f, 10.5f), unit.Position);
    }
}
