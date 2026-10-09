using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Stances: Move ignores enemies, Attack-move stops to fight, Hold never moves, Stop clears the order.</summary>
public class SimStanceTests
{
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);

    /// <summary>True when the unit attacked during the last tick.</summary>
    private static bool Attacked(SimHarness sim, int id)
    {
        sim.Em.CompleteAllTrackedJobs();
        foreach (int2 e in sim.Context.Data.AttackEvents) if (e.x == id) return true;
        return false;
    }

    [Test]
    public void MovePassesEnemiesWithoutStopping()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(5, 32));
        sim.Spawn(SimHarness.OwnerB, new float2(32, 33));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Move, new int2(58, 32), a[0]);
        int ticks = (int)math.ceil(15f * sim.Config.Simulation.TickRate);
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick();
            Unit u = sim.UnitById(a[0]);
            if (u.OrderSlot >= 0) Assert.IsFalse(Attacked(sim, a[0]), $"attacked while moving on tick {t}");
        }
        Assert.Less(math.distance(sim.UnitById(a[0]).Position, new float2(58.5f, 32.5f)), 3f);
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
}
