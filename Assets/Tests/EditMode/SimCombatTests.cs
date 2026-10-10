using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using WAR2D.Sim;

/// <summary>Combat inside the tick: targeting, damage, buildings as targets and the search slice.</summary>
public class SimCombatTests
{
    private static SimHarness Sim(int slice = 1) =>
        new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = slice);

    [Test]
    public void UnitAttacksNearestEnemyInRange()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int[] b = sim.Spawn(SimHarness.OwnerB, new float2(13, 10));
        for (int i = 0; i < 10; i++) sim.Tick(); // the first attack lands on tick 1; the next one is 1 s later
        float expected = sim.Config.GetUnit(UnitType.Tank).Health - sim.Config.GetUnit(UnitType.Tank).Damage
                         * sim.Config.Damage.Multiplier(UnitType.Tank, TargetClass.Unit);
        Assert.AreEqual(expected, sim.UnitById(b[0]).Health, 1e-4f);
        Assert.AreEqual(expected, sim.UnitById(a[0]).Health, 1e-4f, "both sides fire");
        Assert.AreEqual(b[0], sim.UnitById(a[0]).TargetId);
    }

    [Test]
    public void UnitAttacksBuildingWhenNoUnitInRange()
    {
        using var sim = Sim();
        Entity building = sim.Em.CreateEntity();
        sim.Em.AddComponentData(building, new BuildingData { id = sim.Context.Ids.Allocate(), ownerId = SimHarness.OwnerB, buildingType = BuildingType.Miner, position = new float2(13, 10) });
        sim.Em.AddComponentData(building, new HealthComponent { currentHealth = 300, maxHealth = 300 });
        sim.Em.AddComponentData(building, LocalTransform.FromPosition(new float3(13, 10, 0)));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        for (int i = 0; i < 3; i++) sim.Tick(); // damage from tick 1 is written back at a later boundary
        sim.Em.CompleteAllTrackedJobs();
        Assert.Less(sim.Em.GetComponentData<HealthComponent>(building).currentHealth, 300f);
    }

    [Test]
    public void SameOwnerNeverTargeted()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(11, 10));
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual(-1, sim.UnitById(a[0]).TargetId);
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(b[0]).Health);
    }

    [Test]
    public void AlliesNeverFight()
    {
        using var sim = Sim();
        sim.Context.TeamResolver = owner => 0; // everyone on one team
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int[] b = sim.Spawn(SimHarness.OwnerB, new float2(11, 10));
        Entity building = sim.Em.CreateEntity();
        sim.Em.AddComponentData(building, new BuildingData { id = sim.Context.Ids.Allocate(), ownerId = SimHarness.OwnerB, buildingType = BuildingType.Miner, position = new float2(12, 10) });
        sim.Em.AddComponentData(building, new HealthComponent { currentHealth = 300, maxHealth = 300 });
        sim.Em.AddComponentData(building, LocalTransform.FromPosition(new float3(12, 10, 0)));
        for (int i = 0; i < 10; i++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        Assert.AreEqual(-1, sim.UnitById(a[0]).TargetId);
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(b[0]).Health);
        Assert.AreEqual(300f, sim.Em.GetComponentData<HealthComponent>(building).currentHealth);
    }

    [Test]
    public void SliceSpreadsSearches()
    {
        using var sim = Sim(slice: 8);
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        int[] b = sim.Spawn(SimHarness.OwnerB, new float2(12, 10));
        int found = -1;
        for (int t = 0; t < 9 && found < 0; t++)
        {
            sim.Tick();
            if (sim.UnitById(a[0]).TargetId == b[0]) found = t;
        }
        Assert.GreaterOrEqual(found, 0, "the target should be picked up within one slice");
    }

    [Test]
    public void KillGoesToTheLastHittersOwner()
    {
        using var sim = Sim();
        var kills = new System.Collections.Generic.List<(int owner, int killer)>();
        int spawned = 0;
        sim.Context.UnitKilled += (owner, killer) => kills.Add((owner, killer));
        sim.Context.UnitSpawned += _ => spawned++;
        for (int i = 0; i < 4; i++) sim.Spawn(SimHarness.OwnerA, new float2(10, 9 + i * 0.7f));
        sim.Spawn(SimHarness.OwnerB, new float2(13, 10));
        for (int t = 0; t < 2000 && kills.Count == 0; t++) sim.Tick();
        Assert.AreEqual(5, spawned, "every spawn is reported");
        Assert.IsNotEmpty(kills, "B's unit died");
        Assert.AreEqual((SimHarness.OwnerB, SimHarness.OwnerA), kills[0], "A gets the kill");
    }
}
