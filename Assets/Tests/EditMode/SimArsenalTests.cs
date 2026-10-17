using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Rules;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>The v0.12 units: armour classes, fog-gated targeting, splash, minimum range and the Suicide bomber.</summary>
public class SimArsenalTests
{
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);

    private static bool Alive(SimHarness sim, int id) => sim.Units().Any(u => u.Id == id && u.Health > 0f);

    [Test]
    public void ShippedConfigHasTheSpecNumbers()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        UnitConfig heavy = c.GetUnit(UnitType.Heavy);
        Assert.AreEqual((400, 30, 5f, 1.5f), (heavy.Health, heavy.Damage, heavy.Range, heavy.AttackInterval));
        Assert.AreEqual((TargetClass.HeavyUnit, 1, 5f), (heavy.Armour, heavy.SizeClass, heavy.BuildSeconds));
        UnitConfig artillery = c.GetUnit(UnitType.Artillery);
        Assert.AreEqual((14f, 2f, 4f, 1), (artillery.Range, artillery.Splash, artillery.MinRange, artillery.SizeClass));
        Assert.AreEqual((16f, 3f), (c.GetUnit(UnitType.Sniper).Range, c.GetUnit(UnitType.Sniper).MinRange));
        Assert.AreEqual(1.5f, c.GetUnit(UnitType.Flamethrower).Splash);
        Assert.AreEqual((2f, 150f), (c.GetUnit(UnitType.SuicideBomber).BlastRadius, c.GetUnit(UnitType.SuicideBomber).BlastDamage));
        Assert.AreEqual(12f, c.GetUnit(UnitType.Scout).Sight);
        Assert.AreEqual(TargetClass.LightUnit, c.GetUnit(UnitType.Tank).Armour);
        Assert.AreEqual(2f, c.GetUnit(UnitType.Builder).BuildSeconds);
        Assert.AreEqual(1f, c.GetUnit(UnitType.Tank).BuildSeconds);
        Assert.AreEqual((3, 3, 400f), (c.GetBuilding(BuildingType.HeavyFactory).Width, c.GetBuilding(BuildingType.HeavyFactory).Height, c.GetBuilding(BuildingType.HeavyFactory).UpfrontCost));
        Assert.AreEqual((2, 250f), (c.GetBuilding(BuildingType.SiegeWorkshop).Width, c.GetBuilding(BuildingType.SiegeWorkshop).UpfrontCost));
    }

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
    public void ArmourMultipliersFollowTheSpec()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        Assert.AreEqual(0.25f, c.Damage.Multiplier(UnitType.Scout, TargetClass.HeavyUnit));
        Assert.AreEqual(2f, c.Damage.Multiplier(UnitType.Artillery, TargetClass.Building));
        Assert.AreEqual(3f, c.Damage.Multiplier(UnitType.SuicideBomber, TargetClass.Wall));
        Assert.AreEqual(DamageTable.Classes, System.Enum.GetValues(typeof(TargetClass)).Length);
    }

    [Test]
    public void AScoutDoesQuarterDamageToAHeavy()
    {
        using var sim = Sim();
        int[] scout = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Scout);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(13, 10), UnitType.Heavy);
        for (int i = 0; i < 3; i++) sim.Tick(); // one Scout shot (the interval is 10 ticks)
        Assert.AreEqual(400f - 4f * 0.25f, sim.UnitById(heavy[0]).Health, 1e-3f);
    }

    [Test]
    public void AnEnemyInRangeButInTheFogIsNotTargetedUntilSomeoneLightsIt()
    {
        using var sim = Sim();
        int[] gun = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Artillery);
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(22, 10)); // 12 away: in range 14, outside sight 7
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(-1, sim.UnitById(gun[0]).TargetId, "the target is in the fog");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(tank[0]).Health);

        sim.Spawn(SimHarness.OwnerA, new float2(16, 10), UnitType.Scout); // sight 12 reaches it
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.Less(sim.UnitById(tank[0]).Health, sim.Config.GetUnit(UnitType.Tank).Health, "the Artillery fires once the Scout lights it");
    }

    [Test]
    public void MinimumRangeIgnoresTargetsThatAreTooClose()
    {
        using var sim = Sim();
        int[] sniper = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Sniper);
        int[] close = sim.Spawn(SimHarness.OwnerB, new float2(12, 10), UnitType.Builder); // 2 away, minimum range is 3
        for (int i = 0; i < 5; i++) sim.Tick();
        Assert.AreEqual(-1, sim.UnitById(sniper[0]).TargetId);
        int[] far = sim.Spawn(SimHarness.OwnerB, new float2(16, 10), UnitType.Builder);
        for (int i = 0; i < 12; i++) sim.Tick();
        Assert.IsFalse(Alive(sim, far[0]), "the far one is fair game: one 60-damage shot kills a Builder");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Builder).Health, sim.UnitById(close[0]).Health, "the close one was never shot");
    }

    [Test]
    public void SplashFallsOffAndSparesTheAttackersSide()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.Flamethrower);
        int[] primary = sim.Spawn(SimHarness.OwnerB, new float2(12, 10), UnitType.Builder);
        int[] near = sim.Spawn(SimHarness.OwnerB, new float2(12, 10.75f), UnitType.Builder);
        int[] edge = sim.Spawn(SimHarness.OwnerB, new float2(12, 11.2f), UnitType.Builder);
        int[] outside = sim.Spawn(SimHarness.OwnerB, new float2(12, 12f), UnitType.Builder);
        int[] ally = sim.Spawn(SimHarness.OwnerA, new float2(12.2f, 10.3f), UnitType.Builder);
        for (int i = 0; i < 3; i++) sim.Tick();
        float health = sim.Config.GetUnit(UnitType.Builder).Health, hit = 6f * 1.5f;
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
    public void ABomberRunsAtAnEnemyAndTakesFriendsWithIt()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.SuicideBomber);
        int[] victim = sim.Spawn(SimHarness.OwnerB, new float2(14, 10), UnitType.Builder);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(13.5f, 10.8f), UnitType.Builder);
        int[] far = sim.Spawn(SimHarness.OwnerA, new float2(14, 20), UnitType.Builder);
        sim.Tick();
        for (int i = 0; i < 80 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]), "the bomber is spent");
        Assert.IsFalse(Alive(sim, victim[0]), "150 damage kills a 60-health Builder");
        Assert.IsFalse(Alive(sim, friend[0]), "friends in the blast die too");
        Assert.IsTrue(Alive(sim, far[0]), "outside the radius");
    }

    [Test]
    public void ABomberRunsAtAnEnemyBuildingAndBlowsUpOnContact()
    {
        using var sim = Sim();
        int factory = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.HeavyFactory, new int2(16, 10), complete: true);
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(11.5f, 10.5f), UnitType.SuicideBomber);
        sim.Tick();
        float before = sim.Em.GetComponentData<HealthComponent>(sim.BuildingEntity(factory)).currentHealth;
        for (int i = 0; i < 80 && Alive(sim, bomber[0]); i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]), "the bomber is spent");
        float after = sim.Em.GetComponentData<HealthComponent>(sim.BuildingEntity(factory)).currentHealth;
        Assert.AreEqual(before - 150f * 2f, after, 0.01f, "150 blast damage, x2 against buildings");
    }

    [Test]
    public void TheDetonateOrderBlowsUpEverySelectedBomber()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.SuicideBomber);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(30, 30), UnitType.SuicideBomber);
        int[] near = sim.Spawn(SimHarness.OwnerA, new float2(11, 10), UnitType.Builder);
        int[] other = sim.Spawn(SimHarness.OwnerA, new float2(30, 25), UnitType.Builder);
        sim.Tick(); // the units exist from the next tick on
        sim.Order(SimHarness.OwnerA, OrderKind.Detonate, default, a[0], b[0]);
        for (int i = 0; i < 3; i++) sim.Tick();
        sim.Tick();
        Assert.IsFalse(Alive(sim, a[0]) || Alive(sim, b[0]));
        Assert.IsFalse(Alive(sim, near[0]));
        Assert.IsTrue(Alive(sim, other[0]), "5 tiles from the blast");
    }

    [Test]
    public void AForeignPlayerCannotDetonateABomber()
    {
        using var sim = Sim();
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10, 10), UnitType.SuicideBomber);
        sim.Order(SimHarness.OwnerB, OrderKind.Detonate, default, a[0]);
        for (int i = 0; i < 3; i++) sim.Tick();
        Assert.IsTrue(Alive(sim, a[0]));
    }

    [Test]
    public void ABomberShotDownBeforeContactDoesNotExplode()
    {
        using var sim = Sim();
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(15, 10), UnitType.SuicideBomber);
        var tanks = new List<int[]>();
        for (int i = 0; i < 6; i++) tanks.Add(sim.Spawn(SimHarness.OwnerB, new float2(20, 9 + i * 0.4f))); // 6 volleys of 10 kill its 30 health at once
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.IsFalse(Alive(sim, bomber[0]));
        foreach (int[] id in tanks) Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(id[0]).Health, "no blast");
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
