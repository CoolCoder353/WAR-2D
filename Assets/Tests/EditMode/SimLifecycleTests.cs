using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Deaths, upkeep, decay, kills and the unit cap inside the tick.</summary>
public class SimLifecycleTests
{
    [Test]
    public void DeadUnitIsRemovedAndRecordedForExplosion()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        var deaths = new List<int>();
        sim.Context.UnitDied += (id, _) => deaths.Add(id);
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        var units = sim.Units();
        sim.Em.CompleteAllTrackedJobs();
        using (var q = sim.Em.CreateEntityQuery(typeof(Unit)))
        using (var entities = q.ToEntityArray(Unity.Collections.Allocator.Temp))
        {
            Unit u = sim.Em.GetComponentData<Unit>(entities[0]);
            u.Health = 0f;
            sim.Em.SetComponentData(entities[0], u);
        }
        sim.Tick(); // the lifecycle records it
        sim.Tick(); // the boundary removes it
        Assert.AreEqual(0, sim.UnitCount);
        CollectionAssert.AreEqual(new[] { a[0] }, deaths);
        Assert.IsFalse(sim.Context.Ids.IsLive(a[0]));
    }

    [Test]
    public void UpkeepChargesPerUnitUntilBudgetRunsOut()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        float wallet = 5f;
        sim.Context.BudgetOf = _ => wallet;
        sim.Context.Spend = (_, amount) => wallet -= amount;
        for (int i = 0; i < 4; i++) sim.Spawn(SimHarness.OwnerA, new float2(10 + i * 2, 10));
        for (int i = 0; i < 21; i++) sim.Tick(); // the charge runs on tick 20 and is settled at the next boundary
        int unpaid = 0;
        foreach (Unit u in sim.Units()) unpaid += u.Unpaid;
        Assert.AreEqual(2, unpaid);
        Assert.AreEqual(1f, wallet, 1e-4f, "two units at 2/s were paid");
    }

    [Test]
    public void UnpaidUnitsDecay()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Context.BudgetOf = _ => 0f;
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        for (int i = 0; i < 39; i++) sim.Tick(); // unpaid from tick 20, decaying for 20 ticks = 1 s
        Assert.AreEqual(95f, sim.UnitById(a[0]).Health, 0.5f);
    }

    [Test]
    public void KillOwnerKillsOnlyThatOwner()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int[] b = sim.Spawn(SimHarness.OwnerB, new float2(40, 40));
        sim.Tick();
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillOwner, OwnerId = SimHarness.OwnerA });
        sim.Tick();
        var units = sim.Units();
        Assert.AreEqual(1, units.Length);
        Assert.AreEqual(b[0], units[0].Id);
    }

    [Test]
    public void DestroyAllWipesUnitsWithExplosions()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int explosions = 0;
        sim.Context.UnitDied += (_, _) => explosions++;
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Spawn(SimHarness.OwnerB, new float2(40, 40));
        sim.Tick();
        SimContext.RunningOverride = false; // match over: kills still apply
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DestroyAll });
        sim.Tick();
        Assert.AreEqual(0, sim.UnitCount);
        Assert.AreEqual(2, explosions);
    }

    [Test]
    public void SpawnOverCapIsRefused()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.MaxUnitsPerPlayer = 3);
        var boxes = new List<int[]>();
        for (int i = 0; i < 4; i++) boxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(10 + i * 2, 10)));
        sim.Tick();
        Assert.AreEqual(3, sim.UnitCount);
        Assert.AreEqual(-1, boxes[3][0], "the fourth spawn reports a refusal so the spawner keeps its queue");
    }
}
