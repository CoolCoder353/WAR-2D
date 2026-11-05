using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Formations in the simulation (v0.16).</summary>
public class SimFormationTests
{
    private static void Formed(SimHarness sim, int owner, int2 goal, params int[] ids) =>
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.Move, OwnerId = owner, Tile = goal, Ids = ids, Formation = true });

    [Test]
    public void AFormedGroupMovesAtItsSlowestMembersSpeed()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        int[] scout = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Scout);
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new float2(12.5f, 10.5f), UnitType.Heavy);
        sim.Tick();
        Formed(sim, SimHarness.OwnerA, new int2(11, 80), scout[0], heavy[0]);
        sim.TickSeconds(3f);
        float gap = math.abs(sim.UnitById(scout[0]).Position.y - sim.UnitById(heavy[0]).Position.y);
        Assert.Less(gap, 3f, "the Scout keeps with the Heavy");
        Assert.Greater(sim.UnitById(scout[0]).FormationSpeed, 0f, "still in formation on the way");
    }

    [Test]
    public void AGroupTooBigForTheSlotRadiusStillArrives()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(128));
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int i = 0; i < 300; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(10.5f + i % 20 * 1.1f, 10.5f + i / 20 * 1.1f), UnitType.Tank));
        sim.Tick();
        Formed(sim, SimHarness.OwnerA, new int2(64, 70), boxes.ConvertAll(b => b[0]).ToArray());
        sim.TickSeconds(70f);
        Assert.AreEqual(0, sim.Units().Count(u => u.OrderSlot >= 0), "the outer slots lie past Formation/SlotTiles");
    }

    [Test]
    public void AHeavyWalksAroundALoneRockOnItsWayToASlot()
    {
        var rows = new string[96];
        for (int r = 0; r < 96; r++)
        {
            int y = 95 - r;
            var row = new char[96];
            for (int x = 0; x < 96; x++) row[x] = x == 0 || y == 0 || x == 95 || y == 95 ? 'B' : x == 50 && y == 55 ? '#' : '.';
            rows[r] = new string(row);
        }
        using var sim = new SimHarness(WAR2D.World.MapStore.FromAscii(rows));
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new float2(50.5f, 50.5f), UnitType.Heavy);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(45, 60), heavy);
        sim.Tick();
        sim.Tick();
        // Its slot is straight across the rock, which is narrower than the body and between its corners.
        sim.SetFormationSlot(heavy[0], new float2(5f, 0f), 2.5f);
        sim.TickSeconds(30f);
        Assert.Less(math.distance(sim.UnitById(heavy[0]).Position, new float2(50.5f, 60.5f)), 1f, sim.UnitById(heavy[0]).Position.ToString());
    }

    [Test]
    public void AFormedGroupEndsInABlockAtTheGoal()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int i = 0; i < 9; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(10.5f + i, 10.5f), UnitType.Tank));
        sim.Tick();
        var ids = boxes.ConvertAll(b => b[0]); // filled once the spawns apply
        Formed(sim, SimHarness.OwnerA, new int2(40, 60), ids.ToArray());
        sim.TickSeconds(25f);
        float minGap = float.MaxValue;
        foreach (int a in ids) foreach (int b in ids) if (a < b) minGap = math.min(minGap, math.distance(sim.UnitById(a).Position, sim.UnitById(b).Position));
        string where = string.Join(" ", ids.ConvertAll(id => sim.UnitById(id).Position.ToString("F2", null)));
        Assert.Greater(minGap, 0.9f, "each unit stands in its own slot: " + where);
        foreach (int id in ids) Assert.Less(math.distance(sim.UnitById(id).Position, new float2(40.5f, 60.5f)), 4f, "the block is at the goal");
        foreach (int id in ids) Assert.AreEqual(0f, sim.UnitById(id).FormationSpeed, "arrived: the formation ends");
    }

    [Test]
    public void ANewOrderWithoutFormationClearsIt()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Scout);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(11.5f, 10.5f), UnitType.Heavy);
        sim.Tick();
        Formed(sim, SimHarness.OwnerA, new int2(10, 50), a[0], b[0]);
        sim.Tick(); sim.Tick();
        Assert.Greater(sim.UnitById(a[0]).FormationSpeed, 0f);
        sim.Move(SimHarness.OwnerA, new int2(10, 50), a[0]);
        sim.Tick(); sim.Tick();
        Assert.AreEqual(0f, sim.UnitById(a[0]).FormationSpeed);
    }

    [Test]
    public void ASingleUnitNeverFormsUp()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Scout);
        sim.Tick();
        Formed(sim, SimHarness.OwnerA, new int2(10, 50), a[0]);
        sim.Tick(); sim.Tick();
        Assert.AreEqual(0f, sim.UnitById(a[0]).FormationSpeed);
    }

    [Test]
    public void ALostUnitCatchesUpAtFullSpeed()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(128));
        int[] front = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 60.5f), UnitType.Heavy);
        int[] back = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Scout);
        sim.Tick();
        Formed(sim, SimHarness.OwnerA, new int2(10, 120), front[0], back[0]);
        sim.Tick(); sim.Tick();
        float before = sim.UnitById(back[0]).Position.y;
        sim.TickSeconds(1f);
        float moved = sim.UnitById(back[0]).Position.y - before;
        Assert.Greater(moved, sim.Config.Units[UnitType.Heavy].MoveSpeed * 1.2f, "50 tiles behind: it runs at its own speed");
    }

    [Test]
    public void UnitsGoRoundRockBetweenThemAndTheirSlots()
    {
        // A rock cup between the group and the goal, open towards the group, with the units inside it and already within
        // the slot radius: they must follow the flow out and round it, not walk straight at their slots into the rock.
        var rows = new string[96];
        for (int r = 0; r < 96; r++)
        {
            int y = 95 - r;
            char[] row = (r == 0 || r == 95 ? new string('B', 96) : "B" + new string('.', 94) + "B").ToCharArray();
            if (y == 54) for (int x = 28; x <= 48; x++) row[x] = '#';
            if (y >= 46 && y < 54) { row[28] = '#'; row[48] = '#'; } // a cup open towards the group: sliding can't get out
            rows[r] = new string(row);
        }
        using var sim = new SimHarness(MapStoreFrom(rows));
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int i = 0; i < 8; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(34.5f + i, 49.5f), UnitType.Tank)); // inside the cup, within the slot radius
        sim.Tick();
        var ids = boxes.ConvertAll(b => b[0]);
        Formed(sim, SimHarness.OwnerA, new int2(38, 60), ids.ToArray());
        sim.TickSeconds(40f);
        foreach (int id in ids) Assert.Less(math.distance(sim.UnitById(id).Position, new float2(38.5f, 60.5f)), 4f, $"unit {id} at {sim.UnitById(id).Position}");
    }

    [Test]
    public void EveryUnitArrivesWhenManySlotsAreOnRock()
    {
        // A dead-end corridor 3 tiles wide around the goal: the block's outer columns are rock. Their slots used to fall
        // back to the goal itself, so several units shared it and never arrived (code review 2026-11-03).
        var rows = new string[96];
        for (int r = 0; r < 96; r++)
        {
            int y = 95 - r;
            char[] row = (r == 0 || r == 95 ? new string('B', 96) : "B" + new string('.', 94) + "B").ToCharArray();
            if (y >= 50 && y <= 66) { for (int x = 30; x <= 38; x++) row[x] = '#'; for (int x = 42; x <= 50; x++) row[x] = '#'; }
            if (y >= 67 && y <= 70) for (int x = 30; x <= 50; x++) row[x] = '#';
            rows[r] = new string(row);
        }
        using var sim = new SimHarness(MapStoreFrom(rows));
        var boxes = new System.Collections.Generic.List<int[]>();
        for (int i = 0; i < 12; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(34.5f + i, 20.5f), UnitType.Tank));
        sim.Tick();
        var ids = boxes.ConvertAll(b => b[0]);
        Formed(sim, SimHarness.OwnerA, new int2(40, 60), ids.ToArray());
        sim.TickSeconds(60f);
        foreach (int id in ids) Assert.AreEqual(0f, sim.UnitById(id).FormationSpeed, $"unit {id} arrived (at {sim.UnitById(id).Position})");
    }

    [Test]
    public void ABlockedSlotPullsInOnlyToAFreePlace()
    {
        var offsets = new System.Collections.Generic.List<float2> { new float2(0, 0), new float2(1.2f, 0), FormationRules.NoSeat };
        Assert.IsTrue(FormationRules.Taken(offsets, 2, new float2(1.0f, 0), 1.2f), "too close to another member's slot");
        Assert.IsFalse(FormationRules.Taken(offsets, 2, new float2(2.4f, 0), 1.2f));
        Assert.IsFalse(FormationRules.Taken(offsets, 1, new float2(1.2f, 0), 1.2f), "its own slot doesn't count");
        Assert.IsFalse(FormationRules.HasSeat(FormationRules.NoSeat));
    }

    private static WAR2D.World.MapStore MapStoreFrom(string[] rows) => WAR2D.World.MapStore.FromAscii(rows);
}
