using NUnit.Framework;
using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>v0.18 Suicide bombers: the last gasp at 10 % health, and aiming for the middle.</summary>
public class SimBomberTests
{
    private static SimHarness Sim(int size = 64) => new SimHarness(SimHarness.OpenMap(size));
    private static bool Alive(SimHarness sim, int id) => System.Array.Exists(sim.Units(), u => u.Id == id && u.Health > 0f);
    private static float Health(SimHarness sim, int id) => sim.UnitById(id).Health;

    [Test]
    public void ABomberShotDownExplodesWhereItFell()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(14, 10));
        for (int i = 0; i < 6; i++) sim.Spawn(SimHarness.OwnerB, new float2(19.5f, 8.5f + i * 0.5f));
        int[] nearby = sim.Spawn(SimHarness.OwnerB, new float2(16, 11));
        sim.Tick(); // the spawns
        for (int i = 0; i < 40 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]));
        float blast = sim.Config.GetUnit(UnitType.SuicideBomber).BlastDamage;
        Assert.That(Alive(sim, friend[0]) ? Health(sim, friend[0]) : 0f, Is.LessThanOrEqualTo(100f - System.Math.Min(100f, blast)), "its own side's Tank is caught");
        Assert.IsFalse(Alive(sim, nearby[0]), "the enemy beside it is caught");
    }

    /// <summary>A bomber left at 2 of its 30 health (a Digger's hit does that) is under the 3 that is 10 %.</summary>
    [Test]
    public void AHitThatLeavesItUnderTheThresholdSetsItOff()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        int[] nearby = sim.Spawn(SimHarness.OwnerB, new float2(16, 10));
        sim.Tick(); // the spawns
        sim.SetUnitHealth(bomber[0], 2f);
        sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]));
        Assert.IsFalse(Alive(sim, nearby[0]), "150 kills a Tank");
    }

    [Test]
    public void ABomberAboveTheThresholdDoesNotGoOff()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        sim.Tick(); // the spawns
        sim.SetUnitHealth(bomber[0], 4f);
        sim.Tick();
        Assert.AreEqual(4f, Health(sim, bomber[0]), 0.001f);
    }

    [Test]
    public void DecaySetsItOff()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        sim.Tick(); // the spawns
        sim.SetUnitHealth(bomber[0], 3.05f);
        sim.Context.BudgetOf = _ => 0f; // nothing to pay upkeep with: it decays
        sim.TickSeconds(3f);
        Assert.IsFalse(Alive(sim, bomber[0]));
    }

    [Test]
    public void AChainGoesOffInOneTickUpToThePassLimit()
    {
        using var sim = Sim(96);
        var chain = new List<int[]>(); // each id is filled in when the spawn runs
        for (int i = 0; i < 6; i++) chain.Add(sim.Spawn(SimHarness.OwnerA, new float2(10 + i * 1.5f, 10), UnitType.SuicideBomber));
        int blasts = 0;
        sim.Context.Settled += (data, tick, count) => blasts += data.BombBlasts.Count;
        sim.Tick(); // the spawns
        sim.SetUnitHealth(chain[0][0], 1f);
        sim.Tick();
        sim.Tick();
        sim.Tick();
        Assert.AreEqual(sim.Config.Bomber.ChainPasses, blasts, "one blast per pass; the fifth bomber dies in the fourth's blast");
    }

    [Test]
    public void AKillShotDownStillCreditsWhoeverShotIt()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        var killers = new List<int>();
        sim.Context.UnitKilled += (owner, killer) => { if (owner == SimHarness.OwnerA) killers.Add(killer); };
        for (int i = 0; i < 6; i++) sim.Spawn(SimHarness.OwnerB, new float2(19.5f, 8.5f + i * 0.5f));
        sim.Tick(); // the spawns
        for (int i = 0; i < 40 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.That(killers, Does.Contain(SimHarness.OwnerB));
    }

    [Test]
    public void DetonatingOnceGoesOffOnce()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        int blasts = 0;
        sim.Context.Settled += (data, tick, count) => blasts += data.BombBlasts.Count;
        sim.Tick(); // the spawns
        sim.Order(SimHarness.OwnerA, OrderKind.Detonate, int2.zero, bomber);
        sim.Tick();
        sim.Tick();
        sim.Tick();
        Assert.AreEqual(1, blasts);
    }
}
