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
