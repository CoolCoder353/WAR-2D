using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Bombs: who sets them off, what the blast hits, and that enemies can't shoot them.</summary>
public class SimBombTests
{
    private static int ArmedBomb(SimHarness sim, int owner, int2 tile)
    {
        int id = sim.PlaceBuilding(owner, BuildingType.Bomb, tile);
        sim.Tick();
        sim.Tick();
        sim.AddWork(id, 1000f); // complete it
        sim.Tick();
        return id;
    }

    [Test]
    public void AnEnemyUnitSetsItOffAndTheBlastHitsFriendsToo()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        ArmedBomb(sim, SimHarness.OwnerA, new int2(15, 15));
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(16.5f, 15.5f)); // inside the 2.5-tile blast
        int[] enemy = sim.Spawn(SimHarness.OwnerB, new float2(12.5f, 15.5f));
        int blasts = 0;
        sim.Tick();
        sim.Move(SimHarness.OwnerB, new int2(18, 15), enemy[0]);
        for (int i = 0; i < 40 && blasts == 0; i++)
        {
            sim.Tick();
            sim.Em.CompleteAllTrackedJobs();
            blasts += sim.Context.Data.BombBlasts.Count;
        }
        Assert.AreEqual(1, blasts);
        float max = sim.Config.GetUnit(UnitType.Tank).Health;
        Unit hurt = sim.Units().FirstOrDefault(u => u.Id == friend[0]);
        Assert.IsTrue(hurt.Id == 0 || hurt.Health < max, "friendly fire");
    }

    [Test]
    public void OwnAndAlliedUnitsDontSetItOff()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.Context.TeamResolver = owner => owner == SimHarness.OwnerB || owner == SimHarness.OwnerA ? 0 : 1; // A and B allied
        ArmedBomb(sim, SimHarness.OwnerA, new int2(15, 15));
        int[] own = sim.Spawn(SimHarness.OwnerA, new float2(15.5f, 15.5f));
        sim.Spawn(SimHarness.OwnerB, new float2(15.5f, 15.4f));
        sim.TickSeconds(2f);
        sim.Em.CompleteAllTrackedJobs();
        Assert.AreEqual(0, sim.Context.Data.BombBlasts.Count);
        Assert.Greater(sim.UnitById(own[0]).Health, 0f);
    }

    [Test]
    public void ABlueprintBombDoesNothing()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Bomb, new int2(15, 15));
        sim.Spawn(SimHarness.OwnerB, new float2(15.5f, 15.5f));
        sim.TickSeconds(2f);
        sim.Em.CompleteAllTrackedJobs();
        Assert.AreEqual(0, sim.Context.Data.BombBlasts.Count);
    }

    [Test]
    public void EnemiesNeverTargetABomb()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int bomb = ArmedBomb(sim, SimHarness.OwnerA, new int2(15, 15));
        sim.Spawn(SimHarness.OwnerB, new float2(18.5f, 15.5f)); // in range 5, not on the tile
        for (int i = 0; i < 60; i++)
        {
            sim.Tick();
            sim.Em.CompleteAllTrackedJobs();
            foreach (int2 attack in sim.Context.Data.AttackEvents) Assert.AreNotEqual(bomb, attack.y, "no shot at a hidden bomb");
        }
    }
}
