using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Builders: build power stacks, they must be next to the target, and they stop when it is done or gone. Repairs cost resources.</summary>
public class SimConstructionTests
{
    private static float Progress(SimHarness sim, int id) =>
        sim.Em.HasComponent<Construction>(sim.BuildingEntity(id)) ? sim.Em.GetComponentData<Construction>(sim.BuildingEntity(id)).Progress : 1f;

    private static float Health(SimHarness sim, int id) => sim.Em.GetComponentData<HealthComponent>(sim.BuildingEntity(id)).currentHealth;

    [Test]
    public void TwoBuildersBuildTwiceAsFastAsOne()
    {
        float one, two;
        using (var sim = new SimHarness(SimHarness.OpenMap(48)))
        {
            int b = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 20)); // 300 health
            int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(18.5f, 20.5f), UnitType.Builder);
            sim.Tick();
            sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(20, 20), builder[0]);
            sim.TickSeconds(4f);
            one = Progress(sim, b);
        }
        using (var sim = new SimHarness(SimHarness.OpenMap(48)))
        {
            int b = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 20));
            int[] a = sim.Spawn(SimHarness.OwnerA, new float2(18.5f, 20.5f), UnitType.Builder);
            int[] c = sim.Spawn(SimHarness.OwnerA, new float2(21.5f, 20.5f), UnitType.Builder);
            sim.Tick();
            sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(20, 20), a[0], c[0]);
            sim.TickSeconds(4f);
            two = Progress(sim, b);
        }
        Assert.Greater(one, 0.2f, "about 25 hp/s for ~4 s on 300 hp");
        Assert.AreEqual(2f * one, two, 0.08f);
    }

    [Test]
    public void ABuilderOrderedFromAfarWalksThereFirst()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int b = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(40, 40));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(5.5f, 5.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(40, 40), builder[0]);
        sim.TickSeconds(0.5f);
        Assert.AreEqual(0f, Progress(sim, b), "too far to build yet");
        sim.TickSeconds(25f);
        Assert.AreEqual(1f, Progress(sim, b), "walked over and finished");
        Assert.AreEqual(WorkKinds.None, sim.UnitById(builder[0]).WorkKind, "its work is cleared once done");
    }

    [Test]
    public void OnlyBuildersTakeBuildOrdersAndTanksIgnoreThem()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        int b = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(20, 20));
        int[] tank = sim.Spawn(SimHarness.OwnerA, new float2(19.5f, 20.5f));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(20, 20), tank[0]);
        sim.TickSeconds(3f);
        Assert.AreEqual(0f, Progress(sim, b));
    }

    [Test]
    public void BuildersCantBuildAnEnemyBlueprint()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        int b = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Miner, new int2(20, 20));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(19.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(20, 20), builder[0]);
        sim.TickSeconds(3f);
        Assert.AreEqual(0f, Progress(sim, b));
    }

    [Test]
    public void BuildersNeverAttack()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, new float2(11.0f, 10.5f));
        sim.TickSeconds(3f);
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(enemy[0]).Health);
    }

    [Test]
    public void RepairCapsAtMaxAndCostsPerHealth()
    {
        Assert.AreEqual(10f, ConstructionRules.AffordableRepair(work: 10f, missing: 50f, costPerHealth: 0.2f, budget: 100f));
        Assert.AreEqual(5f, ConstructionRules.AffordableRepair(work: 10f, missing: 5f, costPerHealth: 0.2f, budget: 100f), "only what is missing");
        Assert.AreEqual(2.5f, ConstructionRules.AffordableRepair(work: 10f, missing: 50f, costPerHealth: 0.2f, budget: 0.5f), "only what the owner can pay");
        Assert.AreEqual(10f, ConstructionRules.AffordableRepair(work: 10f, missing: 50f, costPerHealth: 0f, budget: 0f), "free when the cost is 0");
    }

    private static int DamagedMiner(SimHarness sim, int owner)
    {
        int b = sim.PlaceBuilding(owner, BuildingType.Miner, new int2(20, 20), complete: true); // 50 health
        sim.Tick();
        Entity e = sim.BuildingEntity(b);
        var h = sim.Em.GetComponentData<HealthComponent>(e);
        h.currentHealth = 10f;
        sim.Em.SetComponentData(e, h);
        return b;
    }

    [Test]
    public void ABuilderRepairsAnOwnBuildingAndTheOwnerPays()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.GetUnit(UnitType.Builder).RunningCost = 0f); // no unit upkeep muddying the bill
        float spent = 0f;
        sim.Context.BudgetOf = _ => 1000f;
        sim.Context.Spend = (_, a) => spent += a;
        int b = DamagedMiner(sim, SimHarness.OwnerA);
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(19.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Repair, new int2(20, 20), builder[0]);
        sim.TickSeconds(5f);
        Assert.AreEqual(50f, Health(sim, b), 0.01f);
        Assert.AreEqual(40f * sim.Config.Siege.RepairCostPerHealth, spent, 0.01f);
        Assert.AreEqual(WorkKinds.None, sim.UnitById(builder[0]).WorkKind);
    }

    [Test]
    public void ARepairStopsWhenTheOwnerCantPay()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Context.BudgetOf = _ => 0f;
        sim.Context.Spend = (_, _) => { };
        int b = DamagedMiner(sim, SimHarness.OwnerA);
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(19.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Repair, new int2(20, 20), builder[0]);
        sim.TickSeconds(3f);
        Assert.AreEqual(10f, Health(sim, b), 0.01f);
    }

    [Test]
    public void BuildersRepairAlliesButNotEnemies()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Context.BudgetOf = _ => 1000f;
        sim.Context.Spend = (_, _) => { };
        sim.Context.TeamResolver = owner => owner == SimHarness.OwnerA || owner == 303 ? 0 : 1;
        int ally = DamagedMiner(sim, 303);
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(19.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Repair, new int2(20, 20), builder[0]);
        sim.TickSeconds(5f);
        Assert.Greater(Health(sim, ally), 10f);
    }
}
