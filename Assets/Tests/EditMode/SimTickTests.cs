using System;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>The async tick: commands apply at the boundary, and the stages leave their jobs running.</summary>
public class SimTickTests
{
    [Test]
    public void QueuedSpawnAppearsAtNextTickAndNotBefore()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        Assert.AreEqual(0, sim.UnitCount);
        sim.Tick();
        Assert.AreEqual(1, sim.UnitCount);
    }

    [Test]
    public void TickLeavesItsJobsRunning()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(128));
        for (int i = 0; i < 1000; i++) sim.Spawn(SimHarness.OwnerA, new float2(2.5f + i % 100, 2.5f + i / 100));
        sim.Tick();
        SimData data = sim.Context.Data;
        Assert.Throws<InvalidOperationException>(() => { float2 _ = data.Positions[0]; },
            "the gather's job should still own the SoA after the tick returns");

        SimContext.RunningOverride = false; // the next boundary settles the jobs and schedules nothing
        sim.Tick();
        Assert.DoesNotThrow(() => { float2 _ = sim.Context.Data.Positions[0]; });
    }

    [Test]
    public void NotRunningOutsidePlaying()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        SimContext.RunningOverride = false;
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        for (int i = 0; i < 5; i++) sim.Tick();
        Assert.AreEqual(0, sim.UnitCount);
        Assert.AreEqual(1, sim.Context.Commands.Count, "the spawn stays queued");
        Assert.AreEqual(0, sim.Context.Clock.Tick);

        SimContext.RunningOverride = true;
        sim.Tick();
        Assert.AreEqual(1, sim.UnitCount);
    }
}
