using NUnit.Framework;
using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>v0.18 Bombers: the last gasp at 10 % health, and aiming for the middle.</summary>
public class SimBomberTests
{
    private static SimHarness Sim(int size = 64) => new SimHarness(SimHarness.OpenMap(size));
    private static bool Alive(SimHarness sim, int id) => System.Array.Exists(sim.Units(), u => u.Id == id && u.Health > 0f);
    private static float Health(SimHarness sim, int id) => sim.UnitById(id).Health;

    [Test]
    public void ABomberShotDownExplodesWhereItFell()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.Bomber);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(14, 10));
        for (int i = 0; i < 6; i++) sim.Spawn(SimHarness.OwnerB, new float2(19.5f, 8.5f + i * 0.5f));
        int[] nearby = sim.Spawn(SimHarness.OwnerB, new float2(16, 11));
        sim.Tick(); // the spawns
        for (int i = 0; i < 40 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]));
        float blast = sim.Config.GetUnit(UnitType.Bomber).BlastDamage;
        Assert.That(Alive(sim, friend[0]) ? Health(sim, friend[0]) : 0f, Is.EqualTo(100f - blast * sim.Config.Bomber.FriendlyDamage).Within(1e-3f), "its own side's Tank takes its share");
        Assert.IsFalse(Alive(sim, nearby[0]), "the enemy beside it is caught");
    }

    /// <summary>A bomber left at 2 of its 30 health (a Digger's hit does that) is under the 3 that is 10 %.</summary>
    [Test]
    public void AHitThatLeavesItUnderTheThresholdSetsItOff()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.Bomber);
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
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.Bomber);
        sim.Tick(); // the spawns
        sim.SetUnitHealth(bomber[0], 4f);
        sim.Tick();
        Assert.AreEqual(4f, Health(sim, bomber[0]), 0.001f);
    }

    [Test]
    public void DecaySetsItOff()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.Bomber);
        sim.Tick(); // the spawns
        sim.SetUnitHealth(bomber[0], 3.05f);
        sim.Context.BudgetOf = _ => 0f; // nothing to pay upkeep with: it decays
        sim.TickSeconds(3f);
        Assert.IsFalse(Alive(sim, bomber[0]));
    }

    [Test]
    public void AChainGoesOffInOneTickUpToThePassLimit()
    {
        // A blast only sets off friendly bombers when friends take enough of it (shipped, they take a tenth).
        using var sim = new SimHarness(SimHarness.OpenMap(96), c => c.Bomber.FriendlyDamage = 1f);
        var chain = new List<int[]>(); // each id is filled in when the spawn runs
        for (int i = 0; i < 6; i++) chain.Add(sim.Spawn(SimHarness.OwnerA, new float2(10 + i * 1.5f, 10), UnitType.Bomber));
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
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.Bomber);
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
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.Bomber);
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

/// <summary>v0.18: a bomber aims for the middle of a group or a side of a building, by what its side sees.</summary>
public class SimBomberAimTests
{
    /// <summary>Tanks that stay where they are and don't fire, so only the aim is being tested.</summary>
    private static SimHarness Sim(int size = 96) => new SimHarness(SimHarness.OpenMap(size), c => c.GetUnit(UnitType.Tank).Damage = 0);

    private static List<int[]> Block(SimHarness sim, int owner, float2 from, int side, float spacing = 1f)
    {
        var ids = new List<int[]>();
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
            ids.Add(sim.Spawn(owner, from + new float2(x, y) * spacing));
        return ids;
    }

    private static int Hurt(SimHarness sim, List<int[]> ids)
    {
        int hurt = 0;
        foreach (int[] id in ids)
        {
            Unit? found = null;
            foreach (Unit u in sim.Units()) if (u.Id == id[0]) found = u;
            if (found == null || found.Value.Health < found.Value.MaxHealth) hurt++;
        }
        return hurt;
    }

    private static List<float2> RunUntilBlast(SimHarness sim, int ticks = 120)
    {
        var at = new List<float2>();
        sim.Context.Settled += (data, tick, count) =>
        {
            using var blasts = data.BombBlasts.ToArray(Unity.Collections.Allocator.Temp);
            foreach (var blast in blasts) at.Add(blast.Position);
        };
        for (int i = 0; i < ticks && at.Count == 0; i++) sim.Tick();
        sim.Tick();
        return at;
    }

    /// <summary>Before v0.18 it went off 1.45 tiles from the nearest Tank and hit 2 of these 16 (worked out from the old contact rule).</summary>
    [Test]
    public void ABomberGoesOffInTheMiddleOfABlockAndHitsMoreOfIt()
    {
        using var sim = Sim();
        List<int[]> tanks = Block(sim, SimHarness.OwnerB, new float2(30, 30), 4);
        sim.Spawn(SimHarness.OwnerA, new float2(25, 31.5f), UnitType.Bomber);
        List<float2> blasts = RunUntilBlast(sim);
        Assert.AreEqual(1, blasts.Count);
        Assert.That(blasts[0].x, Is.GreaterThan(30f), "past the block's edge");
        Assert.That(Hurt(sim, tanks), Is.GreaterThanOrEqualTo(9));
    }

    [Test]
    public void ItGoesOffEarlyWhenTheBlastIsAlreadyGoodEnough()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerB, new float2(30, 30));
        sim.Spawn(SimHarness.OwnerB, new float2(30.8f, 30));
        sim.Spawn(SimHarness.OwnerB, new float2(31.6f, 30));
        sim.Spawn(SimHarness.OwnerA, new float2(25, 30), UnitType.Bomber);
        List<float2> blasts = RunUntilBlast(sim);
        Assert.AreEqual(1, blasts.Count);
        Assert.That(blasts[0].x, Is.LessThan(30.5f), "on contact at the near edge: all three are in the blast already");
    }

    [Test]
    public void ItDoesNotAimWhereItsOwnSideStands()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerB, new float2(30, 26));
        int[] enemy = sim.Spawn(SimHarness.OwnerB, new float2(30, 31));
        var friends = new List<int[]> { sim.Spawn(SimHarness.OwnerA, new float2(31, 26)), sim.Spawn(SimHarness.OwnerA, new float2(31, 27)) };
        sim.Spawn(SimHarness.OwnerA, new float2(25, 29), UnitType.Bomber);
        List<float2> blasts = RunUntilBlast(sim);
        Assert.AreEqual(1, blasts.Count);
        Assert.That(Hurt(sim, friends), Is.EqualTo(0), "the blast keeps clear of its own side");
        Assert.That(System.Array.Exists(sim.Units(), u => u.Id == enemy[0]), Is.False, "and takes the lone enemy instead");
    }

    [Test]
    public void AgainstABuildingItGoesOffAtTheMiddleOfASide()
    {
        using var sim = Sim();
        int hq = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Base, new int2(40, 40), complete: true);
        sim.Tick();
        float2 lo = Footprint.Start(new int2(40, 40), new int2(5, 5)), hi = lo + 5f, mid = (lo + hi) * 0.5f;
        sim.Spawn(SimHarness.OwnerA, lo - 1f, UnitType.Bomber); // just off a corner
        List<float2> blasts = RunUntilBlast(sim);
        Assert.AreEqual(1, blasts.Count);
        Assert.That(sim.BuildingHealthShare(hq), Is.LessThan(1f));
        float2 off = math.abs(blasts[0] - mid);
        Assert.That(math.min(off.x, off.y), Is.LessThan(1f), "near the middle of a side, not a corner");
    }
}
