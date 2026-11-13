using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Rules;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>The v0.12 units: armour classes, fog-gated targeting, splash, minimum range and the Bomber.</summary>
public class SimArsenalTests
{
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);

    private static bool Alive(SimHarness sim, int id) => sim.Units().Any(u => u.Id == id && u.Health > 0f);

    [Test]
    public void AMissingArmourIsAnError()
    {
        string xml = ConfigParserTests.ValidXml.Replace("<Armour>Light</Armour>", "");
        var errors = new List<string>();
        ConfigParser.Parse(xml, errors);
        Assert.That(errors, Has.Some.Contains("Armour"));
    }

    [Test]
    public void TheUnitShorthandSetsBothUnitClassesAndAnExplicitEntryWins()
    {
        string xml = ConfigParserTests.ValidXml.Replace("<DamageTable>", "<DamageTable><Entry attacker=\"Tank\" target=\"HeavyUnit\">0.25</Entry>");
        var errors = new List<string>();
        GameConfigData c = ConfigParser.Parse(xml, errors);
        Assert.That(errors, Is.Empty);
        Assert.AreEqual(1f, c.Damage.Multiplier(UnitType.Tank, TargetClass.LightUnit));
        Assert.AreEqual(0.25f, c.Damage.Multiplier(UnitType.Tank, TargetClass.HeavyUnit), "an explicit class beats the shorthand");
    }

    [Test]
    public void TheDamageTableHasAColumnPerTargetClass() =>
        Assert.AreEqual(DamageTable.Classes, System.Enum.GetValues(typeof(TargetClass)).Length);

    [Test]
    public void AScoutDoesQuarterDamageToAHeavy()
    {
        using var sim = Sim();
        int[] scout = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Scout);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(13, 10), UnitType.Heavy);
        for (int i = 0; i < 3; i++) sim.Tick(); // one Scout shot (the interval is 10 ticks)
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Heavy).Health - 4f * 0.25f, sim.UnitById(heavy[0]).Health, 1e-3f);
    }

    [Test]
    public void AnEnemyInRangeButInTheFogIsNotTargetedUntilSomeoneLightsIt()
    {
        // Shipped Artillery sees as far as it shoots (playtest 2026-11-04); the fog rule still holds for any shorter sight.
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => { c.Simulation.TargetSearchSliceTicks = 1; c.GetUnit(UnitType.Artillery).Sight = 7f; });
        int[] gun = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Artillery);
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(22, 10)); // 12 away: in range 14, outside sight 7
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(-1, sim.UnitById(gun[0]).TargetId, "the target is in the fog");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(tank[0]).Health);

        sim.Spawn(SimHarness.OwnerA, new float2(16, 10), UnitType.Scout); // sight 12 reaches it
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.IsTrue(!Alive(sim, tank[0]) || sim.UnitById(tank[0]).Health < sim.Config.GetUnit(UnitType.Tank).Health, "the Artillery fires once the Scout lights it (v0.20: its shell kills a Tank)");
    }

    [Test]
    public void MinimumRangeIgnoresTargetsThatAreTooClose()
    {
        using var sim = Sim();
        int[] sniper = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Sniper);
        int[] close = sim.Spawn(SimHarness.OwnerB, new float2(12, 10), UnitType.Builder); // 2 away, minimum range is 5
        for (int i = 0; i < 5; i++) sim.Tick();
        Assert.AreEqual(-1, sim.UnitById(sniper[0]).TargetId);
        int[] far = sim.Spawn(SimHarness.OwnerB, new float2(16, 10), UnitType.Builder);
        for (int i = 0; i < 70; i++) sim.Tick(); // v0.20: it aims for 2 s first
        Assert.IsFalse(Alive(sim, far[0]), "the far one is fair game: one shot kills a Builder");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Builder).Health, sim.UnitById(close[0]).Health, "the close one was never shot");
    }

    [Test]
    public void SplashFallsOffAndSparesTheAttackersSide()
    {
        // The falloff rule, with the v0.19 flame (6 damage, splash 1.5) and no fire, so only the splash is measured.
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => { c.Simulation.TargetSearchSliceTicks = 1; c.GetUnit(UnitType.Flamethrower).Damage = 6; c.GetUnit(UnitType.Flamethrower).Splash = 1.5f; c.Burn.Dps = 0f; });
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Flamethrower);
        int[] primary = sim.Spawn(SimHarness.OwnerB, new float2(12, 10), UnitType.Builder);
        int[] near = sim.Spawn(SimHarness.OwnerB, new float2(12, 10.75f), UnitType.Builder);
        int[] edge = sim.Spawn(SimHarness.OwnerB, new float2(12, 11.2f), UnitType.Builder);
        int[] outside = sim.Spawn(SimHarness.OwnerB, new float2(12, 12f), UnitType.Builder);
        int[] ally = sim.Spawn(SimHarness.OwnerA, new float2(12.2f, 10.3f), UnitType.Builder);
        for (int i = 0; i < 3; i++) sim.Tick();
        float health = sim.Config.GetUnit(UnitType.Builder).Health, hit = 6f * sim.Config.Damage.Multiplier(UnitType.Flamethrower, TargetClass.LightUnit);
        Assert.AreEqual(health - hit, sim.UnitById(primary[0]).Health, 0.05f, "the direct hit");
        float nearHit = health - sim.UnitById(near[0]).Health, edgeHit = health - sim.UnitById(edge[0]).Health;
        Assert.AreEqual(hit * 0.75f, nearHit, 0.8f, "about 75 % of the direct hit (separation moves bodies a little)");
        Assert.AreEqual(hit * 0.58f, edgeHit, 1.2f, "a little over half near the edge");
        Assert.Greater(edgeHit, 0f);
        Assert.Less(edgeHit, nearHit);
        Assert.Less(nearHit, hit);
        Assert.AreEqual(health, sim.UnitById(outside[0]).Health, "outside the radius");
        Assert.AreEqual(health, sim.UnitById(ally[0]).Health, "allies are not hit");
    }

    [Test]
    public void SplashDamagesEnemyWallsInTheRadius()
    {
        using var sim = Sim();
        int2 wall = new int2(12, 11);
        sim.Spawn(SimHarness.OwnerB, new float2(12.5f, 10.5f), UnitType.Builder);
        sim.Tick(); // owner B exists from here, so it can own the wall
        sim.PlaceWall(SimHarness.OwnerB, wall);
        sim.Tick();
        sim.ForceBuiltWall(wall);
        float before = sim.Context.Data.WallHealth[sim.Map.Grid.Index(wall)];
        sim.Spawn(SimHarness.OwnerA, new float2(11, 10), UnitType.Flamethrower);
        for (int i = 0; i < 6; i++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        Assert.Less(sim.Context.Data.WallHealth[sim.Map.Grid.Index(wall)], before);
    }

    [Test]
    public void ABomberRunsAtAnEnemyAndBarelyScratchesFriends()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Bomber);
        int[] victim = sim.Spawn(SimHarness.OwnerB, new float2(14, 10), UnitType.Builder);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(13.5f, 10.8f), UnitType.Builder);
        int[] far = sim.Spawn(SimHarness.OwnerA, new float2(14, 20), UnitType.Builder);
        sim.Tick();
        for (int i = 0; i < 80 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]), "the bomber is spent");
        float blast = sim.Config.GetUnit(UnitType.Bomber).BlastDamage * sim.Config.Damage.Multiplier(UnitType.Bomber, TargetClass.LightUnit);
        float builder = sim.Config.GetUnit(UnitType.Builder).Health;
        Assert.Greater(blast, builder, "this scenario needs a blast that kills a Builder");
        Assert.IsFalse(Alive(sim, victim[0]), "the blast kills a Builder");
        Assert.AreEqual(builder - blast * sim.Config.Bomber.FriendlyDamage, sim.UnitById(friend[0]).Health, 1e-3f, "friends take only their share of the blast");
        Assert.IsTrue(Alive(sim, far[0]), "outside the radius");
    }

    [Test]
    public void ABomberRunsAtAnEnemyBuildingAndBlowsUpOnContact()
    {
        using var sim = Sim();
        int factory = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.HeavyFactory, new int2(16, 10), complete: true);
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(11.5f, 10.5f), UnitType.Bomber);
        sim.Tick();
        float before = sim.Em.GetComponentData<HealthComponent>(sim.BuildingEntity(factory)).currentHealth;
        for (int i = 0; i < 80 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]), "the bomber is spent");
        float after = sim.Em.GetComponentData<HealthComponent>(sim.BuildingEntity(factory)).currentHealth;
        float blast = sim.Config.GetUnit(UnitType.Bomber).BlastDamage * sim.Config.Damage.Multiplier(UnitType.Bomber, TargetClass.Building);
        Assert.AreEqual(before - blast, after, 0.01f, "the blast damage, times the table's number against buildings");
    }

    [Test]
    public void TheDetonateOrderBlowsUpEverySelectedBomber()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => { c.Simulation.TargetSearchSliceTicks = 1; c.Bomber.FriendlyDamage = 0.1f; }); // pinned: the balance may set it to 0
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Bomber);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(30, 30), UnitType.Bomber);
        int[] near = sim.Spawn(SimHarness.OwnerA, new float2(11, 10), UnitType.Builder);
        int[] other = sim.Spawn(SimHarness.OwnerA, new float2(30, 25), UnitType.Builder);
        sim.Tick(); // the units exist from the next tick on
        sim.Order(SimHarness.OwnerA, OrderKind.Detonate, default, a[0], b[0]);
        for (int i = 0; i < 3; i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, a[0]) || Alive(sim, b[0]));
        float builder = sim.Config.GetUnit(UnitType.Builder).Health;
        Assert.Less(sim.UnitById(near[0]).Health, builder, "a friend in the blast takes its share");
        Assert.AreEqual(builder, sim.UnitById(other[0]).Health, "5 tiles from the blast");
    }

    [Test]
    public void AForeignPlayerCannotDetonateABomber()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Bomber);
        sim.Order(SimHarness.OwnerB, OrderKind.Detonate, default, a[0]);
        for (int i = 0; i < 3; i++) sim.Tick();
        Assert.IsTrue(Alive(sim, a[0]));
    }
}

/// <summary>Heavy and Artillery use the large pathing class.</summary>
public class SimLargeClassTests
{
    /// <summary>A 64-tile map with a wall down column 30, open only on the listed rows.</summary>
    private static SimHarness WallWithGap(params int[] openRows)
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            if (r == 0 || r == 63) { rows[r] = new string('B', 64); continue; }
            var line = new System.Text.StringBuilder("B" + new string('.', 62) + "B");
            if (System.Array.IndexOf(openRows, r) < 0) line[30] = '#';
            rows[r] = line.ToString();
        }
        return new SimHarness(MapStore.FromAscii(rows), c => c.Simulation.TargetSearchSliceTicks = 1);
    }

    [Test]
    public void BothHeavyUnitsUseTheLargeClass()
    {
        Config.GameConfigData c = Config.ConfigLoader.LoadConfig();
        Assert.AreEqual(WAR2D.Pathing.FlowSizeClass.Large, c.GetUnit(UnitType.Heavy).SizeClass);
        Assert.AreEqual(WAR2D.Pathing.FlowSizeClass.Large, c.GetUnit(UnitType.Artillery).SizeClass);
        Assert.AreEqual(WAR2D.Pathing.FlowSizeClass.Small, c.GetUnit(UnitType.Scout).SizeClass);
    }

    [Test]
    public void ALargeUnitPassesAThreeWideChoke()
    {
        using var sim = WallWithGap(20, 21, 22);
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new Unity.Mathematics.float2(10.5f, 21.5f), UnitType.Heavy);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new Unity.Mathematics.int2(50, 21), heavy[0]);
        sim.TickSeconds(30f);
        Assert.Greater(sim.UnitById(heavy[0]).Position.x, 40f);
    }

    [Test]
    public void AWallRaisedUnderALargeUnitPushesItToAClearSpot()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new Unity.Mathematics.float2(20.5f, 20.5f), UnitType.Heavy);
        sim.Tick();
        // Every tile next to the new wall lacks a large unit's clearance, so the search must walk past them.
        var wall = new Unity.Mathematics.int2(20, 20);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = wall, TileType = TileType.PlayerWall });
        for (int i = 0; i < 4; i++) sim.Tick();
        Unity.Mathematics.int2 tile = (Unity.Mathematics.int2)Unity.Mathematics.math.floor(sim.UnitById(heavy[0]).Position);
        Assert.AreNotEqual(wall, tile, "pushed off the wall");
        Assert.AreEqual(0, sim.Context.Data.LargeGrid[tile.y * sim.Context.Data.Width + tile.x], "onto a tile with a large unit's clearance");
    }

    [Test]
    public void ALargeUnitIsRefusedAtAOneWideGap()
    {
        using var sim = WallWithGap(21);
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new Unity.Mathematics.float2(10.5f, 21.5f), UnitType.Heavy);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new Unity.Mathematics.int2(50, 21), heavy[0]);
        sim.TickSeconds(30f);
        Assert.Less(sim.UnitById(heavy[0]).Position.x, 30f, "the Heavy cannot fit through");
    }
}
