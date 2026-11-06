using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>v0.18 Attack order: go after one target, ignore the rest, and never follow it into the fog.</summary>
public class SimAttackOrderTests
{
    private static SimHarness Sim(int size = 96) => new SimHarness(SimHarness.OpenMap(size));
    private static bool Alive(SimHarness sim, int id) => sim.Units().Any(u => u.Id == id && u.Health > 0f);

    private static void Attack(SimHarness sim, int owner, int target, bool queue, params int[] ids) =>
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.Attack, Queue = queue, OwnerId = owner, TargetId = target, Tile = new int2(1, 1), Ids = ids });

    [Test]
    public void ACrowdAttackingABuildingKeepsClosingUntilInRange()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96), c => c.GetBuilding(BuildingType.Barracks).Health = 1000000);
        var crowd = new List<int[]>();
        for (int i = 0; i < 100; i++) crowd.Add(sim.Spawn(SimHarness.OwnerA, new float2(20.5f + i % 10 * 1.1f, 20.5f + i / 10 * 1.1f), UnitType.Flamethrower));
        sim.Spawn(SimHarness.OwnerB, new float2(90.5f, 90.5f));
        sim.Tick();
        int target = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(33, 25), complete: true);
        sim.TickSeconds(0.5f);
        Attack(sim, SimHarness.OwnerA, target, false, crowd.ConvertAll(c => c[0]).ToArray());
        sim.TickSeconds(20f);
        Assert.AreEqual(100, sim.Units().Count(u => u.OwnerId == SimHarness.OwnerA && u.Stance == Stances.Attack), "it saw the target");
        Assert.AreEqual(0, sim.Units().Count(u => u.OwnerId == SimHarness.OwnerA && u.OrderSlot < 0), "none stops for good, out of its range of 2.5");
    }

    [Test]
    public void ItChasesAndKillsAMovingTarget()
    {
        using var sim = Sim();
        int[] attacker = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(24, 20));
        sim.Tick();
        Attack(sim, SimHarness.OwnerA, target[0], false, attacker);
        sim.Move(SimHarness.OwnerB, new int2(26, 40), target);
        sim.Tick();
        for (int i = 0; i < 800 && Alive(sim, target[0]); i++) sim.Tick();
        Assert.IsFalse(Alive(sim, target[0]));
    }

    [Test]
    public void ItIgnoresANearerEnemy()
    {
        using var sim = Sim();
        int[] attacker = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        int[] nearer = sim.Spawn(SimHarness.OwnerB, new float2(22, 20));
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(24, 22));
        sim.Tick();
        Attack(sim, SimHarness.OwnerA, target[0], false, attacker);
        for (int i = 0; i < 50; i++) sim.Tick(); // both enemies shoot back, so not for long
        Unit a = sim.UnitById(attacker[0]);
        Assert.AreEqual(target[0], a.TargetId);
        Assert.AreEqual(sim.UnitById(nearer[0]).MaxHealth, sim.UnitById(nearer[0]).Health, "the nearer one isn't touched");
    }

    [Test]
    public void ItStandsWhenTheTargetDies()
    {
        using var sim = Sim();
        int[] attacker = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(23, 20));
        sim.Tick();
        sim.SetUnitHealth(target[0], 5f);
        Attack(sim, SimHarness.OwnerA, target[0], false, attacker);
        for (int i = 0; i < 60 && Alive(sim, target[0]); i++) sim.Tick();
        Assert.IsFalse(Alive(sim, target[0]));
        for (int i = 0; i < 5; i++) sim.Tick();
        Unit a = sim.UnitById(attacker[0]);
        Assert.AreEqual(Stances.Idle, a.Stance);
        Assert.AreEqual(0, a.OrderTarget);
    }

    [Test]
    public void ItAttacksABuilding()
    {
        using var sim = Sim();
        int[] attacker = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        int building = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(24, 20), complete: true);
        sim.Tick();
        Attack(sim, SimHarness.OwnerA, building, false, attacker);
        for (int i = 0; i < 80; i++) sim.Tick();
        Assert.That(sim.BuildingHealthShare(building), Is.LessThan(1f));
    }

    [Test]
    public void AQueuedAttackRunsAfterAMove()
    {
        using var sim = Sim();
        int[] attacker = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(26, 26));
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(22, 22), attacker);
        sim.Tick();
        Attack(sim, SimHarness.OwnerA, target[0], true, attacker);
        sim.Tick();
        Assert.AreEqual(Stances.Move, sim.UnitById(attacker[0]).Stance, "the Move still runs");
        for (int i = 0; i < 300 && sim.UnitById(attacker[0]).Stance != Stances.Attack; i++) sim.Tick();
        Assert.AreEqual(target[0], sim.UnitById(attacker[0]).OrderTarget);
    }

    [Test]
    public void AGroupSharesOneFlowFieldUntilTheTargetMovesFarEnough()
    {
        using var sim = Sim();
        var group = new[] { sim.Spawn(SimHarness.OwnerA, new float2(20, 20)), sim.Spawn(SimHarness.OwnerA, new float2(21, 20)), sim.Spawn(SimHarness.OwnerA, new float2(20, 21)) };
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(26, 20));
        sim.Tick();
        Attack(sim, SimHarness.OwnerA, target[0], false, group.Select(g => g[0]).ToArray());
        sim.Tick();
        sim.Tick();
        int[] slots = group.Select(g => sim.UnitById(g[0]).OrderSlot).Distinct().ToArray();
        Assert.AreEqual(1, slots.Length, "one order for the group");
        Assert.AreEqual(1, sim.Context.Orders.LiveCount);
    }

    [Test]
    public void AnAllyOrInvisibleTargetBecomesAnAttackMove()
    {
        using var sim = Sim();
        int[] attacker = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        int[] hidden = sim.Spawn(SimHarness.OwnerB, new float2(70, 70));
        int[] own = sim.Spawn(SimHarness.OwnerA, new float2(22, 22));
        sim.Tick();
        sim.Tick();
        Attack(sim, SimHarness.OwnerA, hidden[0], false, attacker);
        sim.Tick();
        Unit a = sim.UnitById(attacker[0]);
        Assert.AreEqual(Stances.AttackMove, a.Stance);
        Assert.AreEqual(0, a.OrderTarget);
        Attack(sim, SimHarness.OwnerA, own[0], false, attacker);
        sim.Tick();
        Assert.AreEqual(0, sim.UnitById(attacker[0]).OrderTarget, "not on the attack mask");
    }
}
