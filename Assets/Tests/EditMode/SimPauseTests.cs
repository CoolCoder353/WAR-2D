using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

public class SimPauseTests
{
    [Test]
    public void PausedTickDoesNotAdvanceOrMoveUnits()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Tick();
        int tick = sim.Context.Clock.Tick;
        sim.Context.Pause.Paused = true;
        sim.Tick(); sim.Tick();
        Assert.AreEqual(tick, sim.Context.Clock.Tick);
        Assert.IsFalse(sim.Context.Clock.Running);
    }

    [Test]
    public void AnArrivalJustBeforeAPauseStillTakesTheNextWaypoint()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] unit = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(14, 10), unit);
        sim.Order(SimHarness.OwnerA, OrderKind.Move, true, new int2(30, 10), unit);
        sim.Em.CompleteAllTrackedJobs();
        for (int i = 0; i < 400 && sim.Context.Data.Arrivals.IsEmpty(); i++)
        {
            sim.Tick();
            sim.Em.CompleteAllTrackedJobs();
        }
        Assert.IsFalse(sim.Context.Data.Arrivals.IsEmpty(), "it arrived on the tick before the pause");
        sim.Context.Pause.Paused = true;
        sim.Tick();
        sim.Context.Pause.Paused = false;
        sim.TickSeconds(10f);
        Assert.Greater(sim.UnitById(unit[0]).Position.x, 25f, "on to the second leg");
    }

    [Test]
    public void StepRunsExactlyThatManyTicks()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Tick();
        sim.Context.Pause.Paused = true;
        sim.Tick();
        int tick = sim.Context.Clock.Tick;
        sim.Context.Pause.Step(3);
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.AreEqual(tick + 3, sim.Context.Clock.Tick);
        Assert.IsTrue(sim.Context.Pause.Frozen);
    }

    [Test]
    public void ResumeRunsAgain()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Context.Pause.Paused = true;
        sim.Tick();
        sim.Context.Pause.Paused = false;
        sim.Tick();
        Assert.IsTrue(sim.Context.Clock.Running);
    }
}
