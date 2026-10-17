using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Blueprints: refunds, progress, health while building, and what a blueprint may not do.</summary>
public class ConstructionTests
{
    [Test]
    public void ABlueprintRefundsInFullAndAFinishedBuildingItsShare()
    {
        Assert.AreEqual(200f, ConstructionRules.Refund(blueprint: true, cost: 200f, demolishRefund: 0.5f), "any blueprint, whatever its progress");
        Assert.AreEqual(100f, ConstructionRules.Refund(blueprint: false, cost: 200f, demolishRefund: 0.5f), "half back for a finished building");
        Assert.AreEqual(0f, ConstructionRules.Refund(blueprint: false, cost: 200f, demolishRefund: 0f));
        Assert.AreEqual(200f, ConstructionRules.Refund(blueprint: false, cost: 200f, demolishRefund: 7f), "the share is clamped to 0..1");
    }

    [Test]
    public void WorkRaisesHealthAndProgressTogetherAndCompletesAtMax()
    {
        float progress = 0f, health = ConstructionRules.StartHealth;
        Assert.IsFalse(ConstructionRules.AddWork(ref progress, ref health, 300f, 150f));
        Assert.AreEqual(0.5f, progress, 1e-4f);
        Assert.AreEqual(151f, health, 1e-3f, "health rises by the work done, keeping damage taken");
        Assert.IsTrue(ConstructionRules.AddWork(ref progress, ref health, 300f, 150f));
        Assert.AreEqual(1f, progress);
        Assert.AreEqual(300f, health, "health is capped at max");
    }

    [Test]
    public void DamagedBlueprintStillCompletesOnProgressNotHealth()
    {
        float progress = 0.9f, health = 50f; // took damage while building
        Assert.IsTrue(ConstructionRules.AddWork(ref progress, ref health, 300f, 30f));
        Assert.AreEqual(80f, health, 1e-3f, "it completes damaged; Builders can repair it later");
    }

    [Test]
    public void ANewSpawnerIsABlueprintWithOneHealth()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int id = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(10, 10));
        sim.Tick();
        Entity e = sim.BuildingEntity(id);
        Assert.IsTrue(sim.Em.HasComponent<Construction>(e));
        Assert.AreEqual(ConstructionRules.StartHealth, sim.Em.GetComponentData<HealthComponent>(e).currentHealth);
        Assert.IsFalse(sim.Em.HasComponent<UpkeepComponent>(e), "no upkeep until complete (P2)");
    }

    [Test]
    public void TheHqIsCompleteAtOnce()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int id = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Base, new int2(10, 10));
        sim.Tick();
        Assert.IsFalse(sim.Em.HasComponent<Construction>(sim.BuildingEntity(id)));
    }

    [Test]
    public void WorkCompletesABlueprintAndRaisesTheEvent()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int completed = 0;
        sim.Context.BuildingCompleted += (_, _) => completed++;
        int id = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(10, 10)); // 50 health
        sim.Tick();
        sim.Tick();
        sim.AddWork(id, 60f);
        sim.Tick();
        Entity e = sim.BuildingEntity(id);
        Assert.IsFalse(sim.Em.HasComponent<Construction>(e));
        Assert.IsTrue(sim.Em.HasComponent<UpkeepComponent>(e));
        Assert.AreEqual(1, completed);
    }

    [Test]
    public void ABlueprintSeesNothing()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(30, 30)); // Sight 6 when built
        for (int i = 0; i < 6; i++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        int grid = sim.Context.VisionOf(SimHarness.OwnerA);
        Assert.IsFalse(sim.Context.Data.Sees(grid, new float2(35.5f, 30.5f)));
        Assert.IsFalse(sim.Context.Data.Sees(grid, new float2(30.5f, 30.5f)), "not even its own tile (owner, 2026-10-18)");
    }
}
