using System;
using System.Collections.Generic;
using Config;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Net.Replication;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>Shared helpers for the v0.20 Onslaught rule tests (spec §13).</summary>
internal static class Onslaught
{
    public static bool Alive(SimHarness sim, int id) => Array.Exists(sim.Units(), u => u.Id == id && u.Health > 0f);
    public static float Health(SimHarness sim, int id) => sim.UnitById(id).Health;

    /// <summary>An open map with a rock column at <paramref name="x"/> (rows 1 to size - 2).</summary>
    public static MapStore RockColumn(int size, int x)
    {
        var rows = new string[size];
        for (int r = 0; r < size; r++)
            rows[r] = r == 0 || r == size - 1 ? new string('B', size) : "B" + new string('.', x - 1) + "#" + new string('.', size - x - 2) + "B";
        return MapStore.FromAscii(rows);
    }

    /// <summary>Every blast (position, kind) the next ticks report.</summary>
    public static List<(float2 at, byte kind)> Blasts(SimHarness sim)
    {
        var at = new List<(float2, byte)>();
        sim.Context.Settled += (data, tick, count) =>
        {
            using NativeArray<BombBlast> blasts = data.BombBlasts.ToArray(Allocator.Temp);
            foreach (BombBlast b in blasts) at.Add((b.Position, b.Bomber));
        };
        return at;
    }

    /// <summary>Every leap the next ticks start.</summary>
    public static List<LeapEvent> Leaps(SimHarness sim)
    {
        var leaps = new List<LeapEvent>();
        sim.Context.Settled += (data, tick, count) => { for (int i = 0; i < data.Leaps.Length; i++) leaps.Add(data.Leaps[i]); };
        return leaps;
    }

    /// <summary>Every shell the next ticks launch.</summary>
    public static List<Shell> Launches(SimHarness sim)
    {
        var shells = new List<Shell>();
        sim.Context.Settled += (data, tick, count) => { for (int i = 0; i < data.ShellsLaunched.Length; i++) shells.Add(data.ShellsLaunched[i]); };
        return shells;
    }
}

/// <summary>v0.20 Bombers (spec §2): the leap to the army's centre, its reach and path, the air, the landing, blast resistance.</summary>
public class SimBomberLeapTests
{
    /// <summary>Tanks that don't fire, so only the Bomber is being tested.</summary>
    private static SimHarness Sim(int size = 96) => new SimHarness(SimHarness.OpenMap(size), c => c.GetUnit(UnitType.Tank).Damage = 0);

    [Test]
    public void ItLeapsIntoTheCentreOfTheNearestArmyAndExplodesThere()
    {
        using var sim = Sim();
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) sim.Spawn(SimHarness.OwnerB, new float2(30 + x, 30 + y));
        sim.Spawn(SimHarness.OwnerA, new float2(25, 31), UnitType.Bomber);
        var leaps = Onslaught.Leaps(sim);
        var blasts = Onslaught.Blasts(sim);
        for (int i = 0; i < 80 && blasts.Count == 0; i++) sim.Tick();
        Assert.AreEqual(1, leaps.Count, "one leap");
        Assert.That(math.distance(leaps[0].To, new float2(31, 31)), Is.LessThan(0.6f), "to the block's centre");
        Assert.AreEqual(1, blasts.Count);
        Assert.That(math.distance(blasts[0].at, leaps[0].To), Is.LessThan(1e-3f), "it explodes where it lands");
        Assert.AreEqual(sim.Config.Bomber.LeapSeconds / sim.Config.Simulation.TickSeconds, leaps[0].End - leaps[0].Start, 0.5f, "the leap takes LeapSeconds");
    }

    [Test]
    public void ALeapGoesNoFurtherThanItsReach()
    {
        using var sim = Sim();
        for (int x = 0; x < 6; x++) sim.Spawn(SimHarness.OwnerB, new float2(30 + x, 31));
        sim.Spawn(SimHarness.OwnerA, new float2(24, 31), UnitType.Bomber);
        var leaps = Onslaught.Leaps(sim);
        for (int i = 0; i < 80 && leaps.Count == 0; i++) sim.Tick();
        Assert.AreEqual(1, leaps.Count);
        Assert.That(math.distance(leaps[0].From, leaps[0].To), Is.LessThanOrEqualTo(sim.Config.Bomber.LeapReach + 1e-3f));
        Assert.That(leaps[0].To.x, Is.LessThan(32.4f), "short of the army's centre (32.5)");
    }

    [Test]
    public void ABuildingInTheWayStopsTheLeap()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerB, new float2(31, 31));
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(28, 31), complete: true);
        sim.Spawn(SimHarness.OwnerA, new float2(26, 31), UnitType.Bomber);
        var leaps = Onslaught.Leaps(sim);
        for (int i = 0; i < 40; i++) sim.Tick();
        Assert.That(leaps.TrueForAll(l => l.From.x > 29.5f || l.To.x < 27f), "no leap across the building's footprint");
    }

    [Test]
    public void InTheAirItTakesNoDamageAndIgnoresDetonate()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96), c => c.GetUnit(UnitType.Bomber).Health = 1000);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) sim.Spawn(SimHarness.OwnerB, new float2(30 + x, 30 + y));
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(25, 31), UnitType.Bomber);
        var leaps = Onslaught.Leaps(sim);
        var blasts = Onslaught.Blasts(sim);
        for (int i = 0; i < 80 && leaps.Count == 0; i++) sim.Tick();
        Assert.AreEqual(1, leaps.Count, "it leapt");
        float atStart = Onslaught.Health(sim, bomber[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Detonate, int2.zero, bomber);
        for (int i = 0; i < leaps[0].End - leaps[0].Start - 2; i++) sim.Tick();
        Assert.AreEqual(atStart, Onslaught.Health(sim, bomber[0]), 1e-3f, "nine Tanks firing and not a scratch in the air");
        Assert.AreEqual(0, blasts.Count, "Detonate doesn't reach it in the air");
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.AreEqual(1, blasts.Count, "it lands and explodes");
        Assert.That(math.distance(blasts[0].at, leaps[0].To), Is.LessThan(1e-3f));
    }

    [Test]
    public void ItShrugsOffNinetyNinePercentOfAMinesBlast()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.GetUnit(UnitType.Bomber).Sight = 1);
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Mine, new int2(20, 20), complete: true);
        int[] bomber = sim.Spawn(SimHarness.OwnerB, new float2(20.5f, 20.5f), UnitType.Bomber);
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.IsTrue(Onslaught.Alive(sim, bomber[0]), "it survives");
        float expected = sim.Config.GetUnit(UnitType.Bomber).Health - sim.Config.Siege.BlastDamage * (1f - sim.Config.GetUnit(UnitType.Bomber).BlastResist);
        Assert.AreEqual(expected, Onslaught.Health(sim, bomber[0]), 0.01f);
    }

    [Test]
    public void AHeavyShrugsOffHalfOfABlastWithNoArmourTakenOff()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.GetUnit(UnitType.Heavy).Damage = 0);
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Mine, new int2(20, 20), complete: true);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(20.5f, 20.5f), UnitType.Heavy);
        for (int i = 0; i < 6; i++) sim.Tick();
        UnitConfig h = sim.Config.GetUnit(UnitType.Heavy);
        Assert.AreEqual(h.Health - sim.Config.Siege.BlastDamage * (1f - h.BlastResist), Onslaught.Health(sim, heavy[0]), 0.01f);
    }
}

/// <summary>v0.20 flat armour (spec §7.2): off every shot, never under Combat/MinHit.</summary>
public class ArmourTests
{
    private static float FirstHit(UnitType attacker)
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.GetUnit(UnitType.Heavy).Damage = 0);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(24, 24), UnitType.Heavy);
        sim.Spawn(SimHarness.OwnerA, new float2(21, 24), attacker);
        float max = sim.Config.GetUnit(UnitType.Heavy).Health;
        for (int i = 0; i < 60; i++)
        {
            sim.Tick();
            if (heavy[0] > 0 && Onslaught.Health(sim, heavy[0]) < max) return max - Onslaught.Health(sim, heavy[0]);
        }
        return 0f;
    }

    [Test]
    public void ATanksShotLosesTheArmour()
    {
        GameConfigData c = ConfigLoader.LoadConfig();
        float raw = c.GetUnit(UnitType.Tank).Damage * c.Damage.Multiplier(UnitType.Tank, TargetClass.HeavyUnit);
        Assert.AreEqual(raw - c.GetUnit(UnitType.Heavy).FlatArmour, FirstHit(UnitType.Tank), 1e-3f);
    }

    [Test]
    public void SmallArmsDoTheLeastHit()
    {
        Assert.AreEqual(ConfigLoader.LoadConfig().Combat.MinHit, FirstHit(UnitType.Scout), 1e-3f);
    }
}

/// <summary>v0.20 burning (spec §7.3): lit by a flame, one fire refreshed not stacked, class rates, buildings, credit.</summary>
public class SimBurnTests
{
    /// <summary>A Flamethrower that fires once, then never again in the test.</summary>
    private static SimHarness OneShot(Action<GameConfigData> more = null) => new SimHarness(SimHarness.OpenMap(48), c =>
    {
        c.GetUnit(UnitType.Flamethrower).AttackInterval = 1000f;
        c.GetUnit(UnitType.Tank).Damage = 0;
        c.GetUnit(UnitType.Heavy).Damage = 0;
        more?.Invoke(c);
    });

    private static float Burnt(UnitType victim)
    {
        using var sim = OneShot();
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(24, 24), victim);
        sim.Spawn(SimHarness.OwnerA, new float2(20, 24), UnitType.Flamethrower);
        sim.TickSeconds(sim.Config.Burn.Seconds + 1.5f);
        return sim.Config.GetUnit(victim).Health - Onslaught.Health(sim, target[0]);
    }

    [Test]
    public void OneFlameHitBurnsALightUnitForItsSeconds()
    {
        GameConfigData c = ConfigLoader.LoadConfig();
        float hit = c.GetUnit(UnitType.Flamethrower).Damage * c.Damage.Multiplier(UnitType.Flamethrower, TargetClass.LightUnit);
        Assert.AreEqual(hit + c.Burn.Dps * c.Burn.LightUnit * c.Burn.Seconds, Burnt(UnitType.Tank), 1f);
    }

    [Test]
    public void AHeavyBurnsAtHalfRateAndArmourDoesntStopFire()
    {
        GameConfigData c = ConfigLoader.LoadConfig();
        float raw = c.GetUnit(UnitType.Flamethrower).Damage * c.Damage.Multiplier(UnitType.Flamethrower, TargetClass.HeavyUnit);
        float hit = math.max(c.Combat.MinHit, raw - c.GetUnit(UnitType.Heavy).FlatArmour);
        Assert.AreEqual(hit + c.Burn.Dps * c.Burn.HeavyUnit * c.Burn.Seconds, Burnt(UnitType.Heavy), 1f);
    }

    [Test]
    public void AFireIsRefreshedNotStacked()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.GetUnit(UnitType.Tank).Damage = 0);
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(24, 24));
        sim.Spawn(SimHarness.OwnerA, new float2(20, 24), UnitType.Flamethrower);
        float maxLeft = 0f;
        sim.Context.Settled += (data, tick, count) =>
        {
            if (tank[0] <= 0) return;
            UnitCombat u = data.Combat[NetIdAllocator.IndexOf(tank[0])];
            if (u.Id == tank[0]) maxLeft = math.max(maxLeft, u.BurnLeft);
        };
        sim.TickSeconds(3f);
        Assert.That(maxLeft, Is.GreaterThan(0f), "it burns");
        Assert.That(maxLeft, Is.LessThanOrEqualTo(sim.Config.Burn.Seconds + 1e-3f), "one fire, its timer reset by each hit");
    }

    [Test]
    public void ABuildingBurnsAndTheFireGoesToWhoeverLitIt()
    {
        using var sim = OneShot();
        int barracks = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(24, 24), complete: true);
        sim.Spawn(SimHarness.OwnerA, new float2(19, 24), UnitType.Flamethrower);
        bool burning = false;
        sim.Context.Settled += (data, tick, count) =>
        {
            BuildingFire f = data.BuildingFires[NetIdAllocator.IndexOf(barracks)];
            burning |= f.Id == barracks && f.Left > 0f && f.By == SimHarness.OwnerA;
        };
        sim.TickSeconds(2f);
        Assert.IsTrue(burning, "lit, by the Flamethrower's owner");
        float share = sim.BuildingHealthShare(barracks);
        sim.TickSeconds(1f);
        Assert.That(sim.BuildingHealthShare(barracks), Is.LessThan(share), "and it burns on after the one shot");
    }

    [Test]
    public void FireKillsAreCreditedToWhoeverLitIt()
    {
        using var sim = OneShot();
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(24, 24));
        sim.Spawn(SimHarness.OwnerA, new float2(20, 24), UnitType.Flamethrower);
        var killers = new List<int>();
        sim.Context.UnitKilled += (owner, killer) => { if (owner == SimHarness.OwnerB) killers.Add(killer); };
        sim.TickSeconds(0.5f);
        sim.SetUnitHealth(tank[0], 4f);
        sim.TickSeconds(3f);
        Assert.IsFalse(Onslaught.Alive(sim, tank[0]));
        CollectionAssert.Contains(killers, SimHarness.OwnerA);
    }

    [Test]
    public void ABurningBomberExplodes()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.GetUnit(UnitType.Bomber).Sight = 1);
        sim.Spawn(SimHarness.OwnerB, new float2(24, 24), UnitType.Bomber);
        sim.Spawn(SimHarness.OwnerA, new float2(19, 24), UnitType.Flamethrower);
        var blasts = Onslaught.Blasts(sim);
        sim.TickSeconds(6f);
        Assert.That(blasts.Exists(b => b.kind == 1), "it went off when the fire took it to its last gasp");
    }
}

/// <summary>v0.20 Artillery shells (spec §4): flight, landing, a shell outliving its gun, the friendly share, the minimum range.</summary>
public class SimShellTests
{
    private static SimHarness Sim(Action<GameConfigData> more = null) => new SimHarness(SimHarness.OpenMap(96), c =>
    {
        c.GetUnit(UnitType.Tank).Damage = 0;
        c.GetUnit(UnitType.Scout).Damage = 0;
        more?.Invoke(c);
    });

    [Test]
    public void AShellFliesAndLandsWhereItAimedOnAStillTarget()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(10, 48), UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerA, new float2(34, 48), UnitType.Scout); // the spotter
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(40, 48));
        var launches = Onslaught.Launches(sim);
        var blasts = Onslaught.Blasts(sim);
        for (int i = 0; i < 160 && blasts.Count == 0; i++) sim.Tick();
        Assert.That(launches.Count, Is.GreaterThan(0), "it fired beyond its own sight, on its spotter's");
        Shell s = launches[0];
        float seconds = math.distance(s.From, s.To) / sim.Config.Artillery.ShellSpeed;
        Assert.AreEqual(seconds / sim.Config.Simulation.TickSeconds, s.LandTick - s.LaunchTick, 1.01f, "flight at ShellSpeed");
        Assert.That(math.distance(s.To, new float2(40, 48)), Is.LessThan(0.5f), "aimed at the still target");
        Assert.That(blasts.Exists(b => b.kind == 2), "it landed in a blast");
        sim.Tick();
        Assert.IsFalse(Onslaught.Alive(sim, tank[0]), "the blast's centre kills a Tank");
    }

    [Test]
    public void AShellLandsEvenIfItsGunDiedAndItsOwnerGetsTheKill()
    {
        using var sim = Sim();
        int[] gun = sim.Spawn(SimHarness.OwnerA, new float2(10, 48), UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerA, new float2(34, 48), UnitType.Scout);
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(40, 48));
        var launches = Onslaught.Launches(sim);
        var killers = new List<int>();
        sim.Context.UnitKilled += (owner, killer) => { if (owner == SimHarness.OwnerB) killers.Add(killer); };
        for (int i = 0; i < 120 && launches.Count == 0; i++) sim.Tick();
        Assert.That(launches.Count, Is.GreaterThan(0));
        sim.SetUnitHealth(gun[0], 0f);
        for (int i = 0; i < 80; i++) sim.Tick();
        Assert.IsFalse(Onslaught.Alive(sim, gun[0]), "the gun died");
        Assert.IsFalse(Onslaught.Alive(sim, tank[0]), "its shell still landed");
        CollectionAssert.Contains(killers, SimHarness.OwnerA);
    }

    [Test]
    public void ItsOwnSideTakesOnlyItsShare()
    {
        using var sim = Sim(c => c.GetUnit(UnitType.Heavy).Damage = 0);
        sim.Spawn(SimHarness.OwnerA, new float2(10, 48), UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerA, new float2(34, 48), UnitType.Scout);
        sim.Spawn(SimHarness.OwnerB, new float2(40, 48), UnitType.Heavy);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(40, 49.5f), UnitType.Heavy);
        var blasts = Onslaught.Blasts(sim);
        for (int i = 0; i < 160 && blasts.Count == 0; i++) sim.Tick();
        sim.Tick();
        UnitConfig h = sim.Config.GetUnit(UnitType.Heavy);
        float most = h.Health - sim.Config.GetUnit(UnitType.Artillery).Damage * sim.Config.Artillery.FriendlyDamage * (1f - h.BlastResist);
        Assert.That(Onslaught.Health(sim, friend[0]), Is.GreaterThanOrEqualTo(most - 1e-3f), "at most the friendly share");
        Assert.That(Onslaught.Health(sim, friend[0]), Is.LessThan(h.Health), "but some");
    }

    [Test]
    public void ItNeverFiresInsideItsMinimumRange()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(30, 48), UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, new float2(34, 48));
        var launches = Onslaught.Launches(sim);
        for (int i = 0; i < 100; i++) sim.Tick();
        Assert.AreEqual(0, launches.Count);
    }
}

/// <summary>v0.20 (spec §4.1, D17): Artillery fires over walls, not rock, and walls still block its own sight.</summary>
public class ArtilleryOverWallsTests
{
    private static List<Shell> Fire(MapStore map, Action<SimHarness> setup)
    {
        var sim = new SimHarness(map, c => { c.GetUnit(UnitType.Tank).Damage = 0; c.GetUnit(UnitType.Scout).Damage = 0; });
        try
        {
            setup(sim);
            var launches = Onslaught.Launches(sim);
            for (int i = 0; i < 120 && launches.Count == 0; i++) sim.Tick();
            return launches;
        }
        finally { sim.Dispose(); }
    }

    [Test]
    public void ItShellsASeenTargetBehindAWall()
    {
        List<Shell> shells = Fire(SimHarness.OpenMap(96), sim =>
        {
            sim.Spawn(SimHarness.OwnerA, new float2(10, 48), UnitType.Artillery);
            sim.Spawn(SimHarness.OwnerA, new float2(30, 48), UnitType.Scout); // sees past the wall
            sim.Spawn(SimHarness.OwnerB, new float2(34, 48));
            sim.Tick();
            for (int y = 40; y <= 56; y++) { sim.PlaceWall(SimHarness.OwnerB, new int2(20, y)); }
            sim.Tick();
            for (int y = 40; y <= 56; y++) sim.ForceBuiltWall(new int2(20, y));
        });
        Assert.That(shells.Count, Is.GreaterThan(0), "over the wall");
    }

    [Test]
    public void ItDoesntShellATargetBehindRock()
    {
        List<Shell> shells = Fire(Onslaught.RockColumn(96, 20), sim =>
        {
            sim.Spawn(SimHarness.OwnerA, new float2(10, 48), UnitType.Artillery);
            sim.Spawn(SimHarness.OwnerA, new float2(30, 48), UnitType.Scout);
            sim.Spawn(SimHarness.OwnerB, new float2(34, 48));
        });
        Assert.AreEqual(0, shells.Count, "rock still blocks the shot");
    }

    [Test]
    public void AWallStillBlocksItsOwnSight()
    {
        List<Shell> shells = Fire(SimHarness.OpenMap(96), sim =>
        {
            sim.Spawn(SimHarness.OwnerA, new float2(10, 48), UnitType.Artillery);
            sim.Spawn(SimHarness.OwnerB, new float2(20, 48)); // within its sight, but behind the wall and nobody spots
            sim.Tick();
            for (int y = 38; y <= 58; y++) sim.PlaceWall(SimHarness.OwnerB, new int2(15, y));
            sim.Tick();
            for (int y = 38; y <= 58; y++) sim.ForceBuiltWall(new int2(15, y));
        });
        Assert.AreEqual(0, shells.Count, "it fires only at what its side sees");
    }
}

/// <summary>v0.20 Snipers (spec §5.2): stop and aim before each shot; moving starts the aim again; big damage to Heavies.</summary>
public class SimSniperAimTests
{
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c => c.GetUnit(UnitType.Heavy).Damage = 0);

    [Test]
    public void ItAimsBeforeItFiresThenHitsAHeavyHard()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(10, 32), UnitType.Sniper);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(20, 32), UnitType.Heavy);
        float max = sim.Config.GetUnit(UnitType.Heavy).Health;
        sim.TickSeconds(sim.Config.Sniper.AimSeconds * 0.75f);
        Assert.AreEqual(max, Onslaught.Health(sim, heavy[0]), 1e-3f, "no shot while it aims");
        sim.TickSeconds(sim.Config.Sniper.AimSeconds);
        GameConfigData c = sim.Config;
        float hit = c.GetUnit(UnitType.Sniper).Damage * c.Damage.Multiplier(UnitType.Sniper, TargetClass.HeavyUnit) - c.GetUnit(UnitType.Heavy).FlatArmour;
        Assert.AreEqual(max - hit, Onslaught.Health(sim, heavy[0]), 1e-3f, "one shot after the aim");
    }

    [Test]
    public void MovingStartsTheAimAgain()
    {
        using var sim = Sim();
        int[] sniper = sim.Spawn(SimHarness.OwnerA, new float2(10, 32), UnitType.Sniper);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(20, 32), UnitType.Heavy);
        float max = sim.Config.GetUnit(UnitType.Heavy).Health;
        sim.TickSeconds(sim.Config.Sniper.AimSeconds * 0.75f);
        sim.Move(SimHarness.OwnerA, new int2(10, 30), sniper); // walks away a couple of tiles: no aim on a Move
        sim.TickSeconds(1.5f);
        sim.TickSeconds(sim.Config.Sniper.AimSeconds * 0.75f);
        Assert.AreEqual(max, Onslaught.Health(sim, heavy[0]), 1e-3f, "its aim began again when it stopped");
        sim.TickSeconds(sim.Config.Sniper.AimSeconds);
        Assert.That(Onslaught.Health(sim, heavy[0]), Is.LessThan(max), "and then it fired");
    }

    [Test]
    public void ItIsLargeAndAimsWithAnAimEvent()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(10, 32), UnitType.Sniper);
        sim.Spawn(SimHarness.OwnerB, new float2(20, 32), UnitType.Heavy);
        int aims = 0;
        sim.Context.Settled += (data, tick, count) => aims += data.AimEvents.Length;
        sim.TickSeconds(1f);
        Assert.AreEqual(1, aims, "one aim begun");
        Assert.AreEqual(1, sim.Config.GetUnit(UnitType.Sniper).SizeClass, "the large pathing class");
    }
}

/// <summary>v0.20 Gun turret (spec §6.3): a 2×2 that builds on its own clock and shoots Bombers first.</summary>
public class GunTurretTests
{
    [Test]
    public void ItIsATwoByTwoBuiltOnItsOwnClock()
    {
        GameConfigData c = ConfigLoader.LoadConfig();
        BuildingConfig t = c.GetBuilding(BuildingType.GunTurret), b = c.GetBuilding(BuildingType.Barracks);
        Assert.AreEqual((2, 2), (t.Width, t.Height));
        Assert.AreEqual(t.BuildSeconds, t.Health / t.BuildRate(c.Siege.BuildPower), 1e-3f, "BuildSeconds with one Builder");
        Assert.AreEqual(c.Siege.BuildPower, b.BuildRate(c.Siege.BuildPower), 1e-3f, "every other building by its health");
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        Assert.AreEqual(t.Health / t.BuildSeconds, sim.Context.Data.BuildRateByKind[(int)BuildingType.GunTurret], 1e-3f);
        Assert.AreEqual(c.Siege.BuildPower, sim.Context.Data.BuildRateByKind[(int)BuildingType.Barracks], 1e-3f);
    }

    [Test]
    public void ItShootsABomberBeforeANearerTank()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => { c.GetUnit(UnitType.Bomber).Sight = 1; c.GetUnit(UnitType.Tank).Damage = 0; });
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.GunTurret, new int2(30, 30), complete: true);
        int[] tank = sim.Spawn(SimHarness.OwnerA, new float2(34, 30));
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(37, 30), UnitType.Bomber);
        float max = sim.Config.GetUnit(UnitType.Tank).Health;
        for (int i = 0; i < 60 && Onslaught.Alive(sim, bomber[0]); i++)
        {
            sim.Tick();
            if (tank[0] > 0 && Onslaught.Alive(sim, tank[0])) Assert.AreEqual(max, Onslaught.Health(sim, tank[0]), 1e-3f, "the Bomber first");
        }
        Assert.IsFalse(Onslaught.Alive(sim, bomber[0]));
    }
}

/// <summary>v0.20 renames (spec §2.6): the parser refuses the old names and names the new ones.</summary>
public class OnslaughtConfigTests
{
    private static List<string> Errors(Func<string, string> edit)
    {
        string xml = UnityEngine.Resources.Load<UnityEngine.TextAsset>("GameConfig").text;
        var errors = new List<string>();
        ConfigParser.Parse(edit(xml), errors);
        return errors;
    }

    [Test]
    public void TheShippedConfigParses() => CollectionAssert.IsEmpty(Errors(x => x));

    [Test]
    public void SuicideBomberIsRefusedWithTheNewName()
    {
        List<string> errors = Errors(x => x.Replace("<Unit type=\"Bomber\">", "<Unit type=\"SuicideBomber\">"));
        Assert.That(errors, Has.Some.Contains("use \"Bomber\""));
    }

    [Test]
    public void BombIsRefusedWithTheNewName()
    {
        List<string> errors = Errors(x => x.Replace("<Building type=\"Mine\">", "<Building type=\"Bomb\">"));
        Assert.That(errors, Has.Some.Contains("use \"Mine\""));
    }

    [Test]
    public void TheOldSiegeKeysAndTheRetiredAimKeysAreRefused()
    {
        List<string> errors = Errors(x => x.Replace("MineTriggerRadius", "BombTriggerRadius").Replace("<LeapReach>", "<AimTiles>6</AimTiles><LeapReach>"));
        Assert.That(errors, Has.Some.Contains("MineTriggerRadius"));
        Assert.That(errors, Has.Some.Contains("AimTiles"));
    }
}

/// <summary>v0.20 combat batch codec: round trip, and malformed input refused.</summary>
public class CombatCodecTests
{
    [Test]
    public void ItRoundTrips()
    {
        var news = new CombatNews { Tick = 1234 };
        news.Statuses.Add(new StatusEntry { Index = 7, Bits = 5 });
        news.Statuses.Add(new StatusEntry { Index = 3, Bits = 0 });
        news.Leaps.Add(new LeapEntry { Index = 9, From = new float2(1.5f, 2.25f), To = new float2(6f, 7.5f), Start = 1230, End = 1242 });
        news.Aims.Add(new AimEntry { Index = 11, TargetId = -42 });
        news.Shells.Add(new ShellEntry { Full = true, From = new float2(10, 10), To = new float2(40, 12), Launch = 1200, Land = 1243 });
        news.Shells.Add(new ShellEntry { Full = false, To = new float2(40, 12), Land = 1243 });
        var writer = new Mirror.NetworkWriter();
        CombatCodec.Write(writer, news);
        var back = new CombatNews();
        CombatCodec.Read(writer.ToArraySegment(), back);
        Assert.AreEqual(1234, back.Tick);
        Assert.AreEqual(new[] { 3, 7 }, back.Statuses.ConvertAll(s => s.Index).ToArray(), "sorted by index");
        Assert.AreEqual(new float2(6f, 7.5f), back.Leaps[0].To);
        Assert.AreEqual(1242, back.Leaps[0].End);
        Assert.AreEqual(-42, back.Aims[0].TargetId);
        Assert.IsTrue(back.Shells[0].Full);
        Assert.IsFalse(back.Shells[1].Full);
        Assert.AreEqual(1243, back.Shells[1].Land);
    }

    [Test]
    public void AHugeCountIsRefused()
    {
        var writer = new Mirror.NetworkWriter();
        VarInt.Write(writer, 5u);
        VarInt.Write(writer, 1000000u);
        var combat = new ClientCombat();
        Assert.IsFalse(combat.Apply(writer.ToArraySegment()));
    }
}
