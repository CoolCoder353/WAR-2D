using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>Dig areas (a Dig drag): Diggers share out a box of rock until it is clear.</summary>
public class SimDigAreaTests
{
    /// <summary>Open floor with a 4×3 rock block at x 10..13, y 6..8.</summary>
    private static MapStore RockBlock()
    {
        var rows = new string[16];
        for (int r = 0; r < 16; r++)
        {
            if (r == 0 || r == 15) { rows[r] = new string('B', 24); continue; }
            char[] row = ("B" + new string('.', 22) + "B").ToCharArray();
            int y = 15 - r; // FromAscii's first row is the top
            if (y >= 6 && y <= 8) for (int x = 10; x <= 13; x++) row[x] = '#';
            rows[r] = new string(row);
        }
        return MapStore.FromAscii(rows);
    }

    private static void DigArea(SimHarness sim, int owner, int2 min, int2 max, bool queue, params int[] ids) =>
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DigArea, OwnerId = owner, BoxMin = min, BoxMax = max, Queue = queue, Ids = ids });

    private static int RockLeft(SimHarness sim)
    {
        int n = 0;
        for (int y = 6; y <= 8; y++)
        for (int x = 10; x <= 13; x++)
            if (sim.Map.Grid.TileAt(new int2(x, y)) == TileType.Wall) n++;
        return n;
    }

    [Test]
    public void TwoDiggersClearTheWholeArea()
    {
        using var sim = new SimHarness(RockBlock());
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(8.5f, 7.5f), UnitType.Digger);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(15.5f, 7.5f), UnitType.Digger);
        sim.Tick();
        DigArea(sim, SimHarness.OwnerA, new int2(9, 5), new int2(14, 9), false, a[0], b[0]);
        sim.TickSeconds(sim.Config.Siege.DigTime * 12f + 10f);
        Assert.AreEqual(0, RockLeft(sim), "every rock tile in the box is dug");
        Assert.AreEqual(0, sim.Context.DigAreas.Count, "a finished area is dropped");
        Assert.AreEqual(WorkKinds.None, sim.UnitById(a[0]).WorkKind, "the Diggers stop when it is clear");
    }

    [Test]
    public void OwnWallsInTheBoxAreLeftStanding()
    {
        using var sim = new SimHarness(RockBlock());
        sim.PlaceWall(SimHarness.OwnerA, new int2(9, 7));
        sim.Tick();
        sim.ForceBuiltWall(new int2(9, 7));
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(8.5f, 5.5f), UnitType.Digger);
        sim.Tick();
        DigArea(sim, SimHarness.OwnerA, new int2(9, 6), new int2(10, 8), false, digger[0]);
        sim.TickSeconds(sim.Config.Siege.DigTime * 4f + 8f);
        Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(new int2(9, 7)), "the Digger's own wall is not dug");
        for (int y = 6; y <= 8; y++) Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(10, y)), $"rock at y = {y} is dug");
    }

    [Test]
    public void ANewOrderTakesTheDiggerOutOfTheArea()
    {
        using var sim = new SimHarness(RockBlock());
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(8.5f, 7.5f), UnitType.Digger);
        sim.Tick();
        DigArea(sim, SimHarness.OwnerA, new int2(10, 6), new int2(13, 8), false, digger[0]);
        sim.TickSeconds(1f);
        Assert.AreEqual(1, sim.Context.DigAreas.Count);
        int before = RockLeft(sim);
        sim.Move(SimHarness.OwnerA, new int2(3, 3), digger[0]);
        sim.TickSeconds(sim.Config.Siege.DigTime * 3f);
        Assert.AreEqual(0, sim.Context.DigAreas.Count, "leaving drops the area");
        Assert.GreaterOrEqual(RockLeft(sim), before - 1, $"the Digger stopped digging the area (before {before}, work {sim.UnitById(digger[0]).WorkKind}, at {sim.UnitById(digger[0]).Position})");
    }

    [Test]
    public void AQueuedAreaStartsAfterTheCurrentMove()
    {
        using var sim = new SimHarness(RockBlock());
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(3.5f, 3.5f), UnitType.Digger);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(8, 3), digger[0]);
        sim.Tick();
        DigArea(sim, SimHarness.OwnerA, new int2(10, 6), new int2(11, 6), true, digger[0]);
        sim.Tick();
        Assert.AreEqual(0, sim.Context.DigAreas.AreaOf(digger[0]), "busy: the area waits in the queue");
        Assert.AreEqual(1, sim.Context.Waypoints.CountOf(digger[0]));
        sim.TickSeconds(sim.Config.Siege.DigTime * 2f + 10f);
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(10, 6)));
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(11, 6)));
        Assert.AreEqual(TileType.Wall, sim.Map.Grid.TileAt(new int2(12, 6)), "only the box is dug");
    }

    [Test]
    public void TheBoxIsClampedToTheInsideAndTheMaximumSide()
    {
        Assert.IsTrue(CommandValidator.TryDigArea(new int2(5, 5), new int2(100, -20), new int2(0, 0), new int2(63, 63), 24, out int2 lo, out int2 hi));
        Assert.AreEqual(new int2(5, 1), lo, "never the border ring");
        Assert.AreEqual(new int2(28, 5), hi, "24 tiles a side from where the drag began");
        Assert.IsFalse(CommandValidator.TryDigArea(new int2(-1, 5), new int2(4, 5), new int2(0, 0), new int2(63, 63), 24, out _, out _), "a drag begun off the map is refused");
    }
}
