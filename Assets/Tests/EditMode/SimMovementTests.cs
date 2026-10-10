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
