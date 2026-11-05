using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Stances: Move ignores enemies, Attack-move stops to fight, Hold never moves, Stop clears the order.</summary>
public class SimStanceTests
{
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);

    /// <summary>A map on which neither side can kill the other, to watch a whole pass.</summary>
    private static SimHarness Tough() => new SimHarness(SimHarness.OpenMap(64), c =>
    {
        c.Simulation.TargetSearchSliceTicks = 1;
        c.GetUnit(UnitType.Tank).Health = 1000000;
    });

    /// <summary>True when the unit attacked during the last tick.</summary>
    private static bool Attacked(SimHarness sim, int id)
    {
        sim.Em.CompleteAllTrackedJobs();
        foreach (int2 e in sim.Context.Data.AttackEvents) if (e.x == id) return true;
        return false;
    }

    [Test]
    public void MoveFiresAtEnemiesInRangeAndNeverStopsForThem()
    {
        using var sim = Tough();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(5, 32));
        float2 enemyAt = new float2(32, 33);
        sim.Spawn(SimHarness.OwnerB, enemyAt);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, new int2(58, 32), a[0]);
        float range = sim.Config.GetUnit(UnitType.Tank).Range;
        float step = sim.Config.GetUnit(UnitType.Tank).MoveSpeed / sim.Config.Simulation.TickRate;
        int ticks = (int)math.ceil(15f * sim.Config.Simulation.TickRate), shots = 0, shotsWhileMoving = 0;
        float2 last = sim.UnitById(a[0]).Position;
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick();
            Unit u = sim.UnitById(a[0]);
            bool fired = Attacked(sim, a[0]);
            if (fired)
            {
                shots++;
                if (math.distance(u.Position, last) > step / 2f) shotsWhileMoving++;
                Assert.LessOrEqual(math.distance(u.Position, enemyAt), range + step, $"fired out of range on tick {t}");
            }
            last = u.Position;
        }
        Assert.Greater(shots, 0, "it fires as it passes");
        Assert.AreEqual(shots, shotsWhileMoving, "and it never stops to do so");
        Assert.Less(math.distance(sim.UnitById(a[0]).Position, new float2(58.5f, 32.5f)), 3f);
    }

    [Test]
    public void MoveDropsItsTargetOnceThatLeavesRange()
    {
        using var sim = Tough();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(30, 32));
        sim.Spawn(SimHarness.OwnerB, new float2(32, 33));
        sim.TickSeconds(0.5f);
        Assert.Greater(sim.UnitById(a[0]).TargetId, 0, "idle in range, it fires");
        sim.Order(SimHarness.OwnerA, OrderKind.Move, new int2(5, 32), a[0]);
        sim.TickSeconds(5f);
        Assert.Greater(math.distance(sim.UnitById(a[0]).Position, new float2(32, 33)), sim.Config.GetUnit(UnitType.Tank).Range);
        Assert.LessOrEqual(sim.UnitById(a[0]).TargetId, 0, "it holds no target out of range");
        Assert.IsFalse(Attacked(sim, a[0]));
    }

    [Test]
    public void ASuicideBomberOnAMoveHoldsOffUntilItArrives()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(5, 32), UnitType.SuicideBomber);
        sim.Spawn(SimHarness.OwnerB, new float2(32, 33), UnitType.Builder); // unarmed, so nothing shoots the bomber down
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, new int2(58, 32), bomber[0]);
        sim.Tick();
        int ticks = (int)math.ceil(5f * sim.Config.Simulation.TickRate);
        for (int t = 0; t < ticks && sim.UnitById(bomber[0]).OrderSlot >= 0; t++)
        {
            sim.Tick();
            Assert.LessOrEqual(sim.UnitById(bomber[0]).TargetId, 0, $"a target on tick {t}");
        }
        Assert.Greater(sim.UnitById(bomber[0]).Position.x, 40f, "it ran past the enemy");
    }

    [Test]
    public void AttackMoveStopsToFight()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(5, 32));
        sim.Spawn(SimHarness.OwnerB, new float2(32, 33));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(58, 32), a[0]);
        bool attacked = false;
        int ticks = (int)math.ceil(6f * sim.Config.Simulation.TickRate);
        for (int t = 0; t < ticks && !attacked; t++)
        {
            sim.Tick();
            attacked = Attacked(sim, a[0]);
        }
        Assert.IsTrue(attacked);
        Assert.Less(sim.UnitById(a[0]).Position.x, 34f);
    }

    [Test]
    public void HoldNeverMoves()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Hold, default, a[0]);
        sim.Tick();
        Assert.AreEqual(Stances.Hold, sim.UnitById(a[0]).Stance);
        float2 start = sim.UnitById(a[0]).Position;
        for (int i = 0; i < 20; i++) sim.Spawn(SimHarness.OwnerA, start);
        sim.TickSeconds(5f);
        float2 end = sim.UnitById(a[0]).Position;
        Assert.AreEqual(start.x, end.x, 1e-4f);
        Assert.AreEqual(start.y, end.y, 1e-4f);
    }

    [Test]
    public void StopClearsOrder()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, new int2(50, 50), a[0]);
        for (int i = 0; i < 5; i++) sim.Tick();
        Assert.GreaterOrEqual(sim.UnitById(a[0]).OrderSlot, 0);
        sim.Order(SimHarness.OwnerA, OrderKind.Stop, default, a[0]);
        sim.Tick();
        sim.Tick();
        Unit u = sim.UnitById(a[0]);
        Assert.AreEqual(-1, u.OrderSlot);
        Assert.AreEqual(Stances.Idle, u.Stance);
        Assert.Less(math.length(u.Velocity), 1e-3f);
    }

    [Test]
    public void ArrivalResetsStanceToIdle()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, new int2(20, 10), a[0]);
        sim.Tick();
        Assert.AreEqual(Stances.Move, sim.UnitById(a[0]).Stance);
        sim.TickSeconds(5f);
        Unit u = sim.UnitById(a[0]);
        Assert.AreEqual(-1, u.OrderSlot);
        Assert.AreEqual(Stances.Idle, u.Stance);
    }

    [Test]
    public void QueuedWaypointsRunInOrder()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        var goals = new[] { new int2(20, 20), new int2(40, 20), new int2(40, 40) };
        sim.Order(SimHarness.OwnerA, OrderKind.Move, false, goals[0], a[0]);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, goals[1], a[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, goals[2], a[0]);
        var closest = new float[3] { float.MaxValue, float.MaxValue, float.MaxValue };
        var closestTick = new int[3];
        int ticks = (int)math.ceil(30f * sim.Config.Simulation.TickRate);
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick();
            float2 p = sim.UnitById(a[0]).Position;
            for (int g = 0; g < 3; g++)
            {
                float d = math.distance(p, (float2)goals[g] + 0.5f);
                if (d < closest[g]) { closest[g] = d; closestTick[g] = t; }
            }
        }
        for (int g = 0; g < 3; g++) Assert.Less(closest[g], 1.5f, $"goal {g} visited");
        Assert.Less(closestTick[0], closestTick[1]);
        Assert.Less(closestTick[1], closestTick[2]);
    }

    [Test]
    public void QueueBeyondCapIsDropped()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => { c.Simulation.TargetSearchSliceTicks = 1; c.Orders.MaxQueued = 2; });
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, false, new int2(20, 10), a[0]);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, new int2(30, 10), a[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, new int2(30, 30), a[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, new int2(10, 30), a[0]);
        sim.TickSeconds(30f);
        Assert.Less(math.distance(sim.UnitById(a[0]).Position, new float2(30.5f, 30.5f)), 1.5f);
    }

    [Test]
    public void GroupSharesOneFieldPerLeg()
    {
        using var sim = Sim();
        var ids = new int[30];
        var boxes = new int[30][];
        for (int i = 0; i < 30; i++) boxes[i] = sim.Spawn(SimHarness.OwnerA, new float2(8 + i % 6, 8 + i / 6));
        sim.Tick();
        for (int i = 0; i < 30; i++) ids[i] = boxes[i][0];
        sim.Order(SimHarness.OwnerA, OrderKind.Move, false, new int2(30, 12), ids);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, new int2(30, 40), ids);
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, new int2(50, 40), ids);
        int maxLive = 0;
        int ticks = (int)math.ceil(40f * sim.Config.Simulation.TickRate);
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick();
            sim.Em.CompleteAllTrackedJobs();
            maxLive = math.max(maxLive, sim.Context.Orders.LiveCount);
        }
        // One small size class: at most the leg being finished and the next one are live at once.
        Assert.LessOrEqual(maxLive, 2);
        int near = 0;
        foreach (int id in ids) if (math.distance(sim.UnitById(id).Position, new float2(50.5f, 40.5f)) < 6f) near++;
        Assert.GreaterOrEqual(near, 27, "the group reached the last leg");
    }
}
