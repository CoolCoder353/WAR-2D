using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Real artillery shells: the flight, the aim ahead of a moving target, the blast by class, and what outlives the gun.</summary>
public class SimShellTests
{
    private const float Damage = 70f;
    private static readonly float2 Gun = new float2(10f, 30f), Spot = new float2(26f, 30f); // 16 tiles apart: in range and in sight

    private static SimHarness Sim(System.Action<GameConfigData> more = null) =>
        new SimHarness(SimHarness.OpenMap(96), c => { c.Simulation.TargetSearchSliceTicks = 1; more?.Invoke(c); });

    private static SimData Settled(SimHarness sim)
    {
        sim.Em.CompleteAllTrackedJobs();
        return sim.Context.Data;
    }

    /// <summary>Ticks until the gun has fired, and returns its shell.</summary>
    private static Shell Fired(SimHarness sim)
    {
        for (int i = 0; i < 300; i++)
        {
            sim.Tick();
            SimData data = Settled(sim);
            if (data.Shells.Length > 0) return data.Shells[0];
        }
        Assert.Fail("the gun never fired: " + string.Join(" ", sim.Units().Select(u => u.Position.ToString())));
        return default;
    }

    /// <summary>Ticks through the tick the shell lands on.</summary>
    private static void Land(SimHarness sim, in Shell shell)
    {
        while (sim.Context.Clock.Tick < shell.LandTick) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
    }

    private static float Lost(SimHarness sim, int[] id, UnitType type) => sim.Config.GetUnit(type).Health - sim.UnitById(id[0]).Health;

    private static float Falloff(float distance, float radius) => Damage * (1f - 0.5f * distance / radius);

    [Test]
    public void TheShippedArtilleryFiresShells()
    {
        GameConfigData c = ConfigLoader.LoadConfig();
        UnitConfig gun = c.GetUnit(UnitType.Artillery);
        Assert.AreEqual((70, 18f, 5f, 14f), (gun.Damage, gun.Range, gun.AttackInterval, gun.ShellSpeed));
        Assert.AreEqual(2f, gun.ShellBlast(UnitType.Heavy, TargetClass.HeavyUnit));
        Assert.AreEqual(gun.SplashMedium, gun.ShellBlast(UnitType.Tank, TargetClass.LightUnit));
        Assert.AreEqual(gun.SplashFast, gun.ShellBlast(UnitType.Scout, TargetClass.LightUnit));
        Assert.AreEqual(gun.SplashFast, gun.ShellBlast(UnitType.SuicideBomber, TargetClass.LightUnit));
        foreach (UnitConfig other in c.Units.Values.Where(u => u != gun)) Assert.AreEqual(0f, other.ShellSpeed, "only the Artillery");
    }

    [Test]
    public void TheAimMeetsATargetDrivingStraight()
    {
        float2 from = float2.zero, target = new float2(14f, 0f);
        Assert.AreEqual(1f, ShellAim.Seconds(from, target, float2.zero, 14f), 1e-4f, "standing still: distance over speed");
        Assert.AreEqual(14f / 19f, ShellAim.Seconds(from, target, new float2(-5f, 0f), 14f), 1e-4f, "driving at the gun");
        Assert.AreEqual(14f / 9f, ShellAim.Seconds(from, target, new float2(5f, 0f), 14f), 1e-4f, "driving away");
        float across = ShellAim.Seconds(from, target, new float2(0f, 5f), 14f);
        Assert.AreEqual(14f * across, math.length(target + new float2(0f, 5f) * across), 1e-3f, "across: the shell and the target arrive together");
        Assert.AreEqual(2f, ShellAim.Seconds(from, target, new float2(13.99f, 0f), 14f), 1e-3f, "nearly as fast as the shell: capped");
        Assert.AreEqual(2f, ShellAim.Seconds(from, target, new float2(20f, 0f), 14f), 1e-3f, "faster than the shell: capped");
        Assert.AreEqual(20, ShellAim.Ticks(1f, 0.05f), "a whole number of ticks stays one");
        Assert.AreEqual(21, ShellAim.Ticks(1.01f, 0.05f), "never early");
        Assert.AreEqual(1, ShellAim.Ticks(0f, 0.05f), "never instant");
    }

    [Test]
    public void DamageLandsOnlyAfterTheFlight()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        int[] tank = sim.Spawn(SimHarness.OwnerB, Gun + new float2(14f, 0f));
        Shell shell = Fired(sim);
        Assert.AreEqual(20, shell.LandTick - shell.FiredTick, "14 tiles at 14 tiles a second, 20 ticks a second");
        Assert.AreEqual(Gun + new float2(14f, 0f), shell.Landing);
        while (sim.Context.Clock.Tick < shell.LandTick - 1)
        {
            sim.Tick();
            Assert.AreEqual(0f, Lost(sim, tank, UnitType.Tank), "still in the air");
        }
        Assert.AreEqual(1, Settled(sim).Shells.Length);
        sim.Tick();
        Assert.AreEqual(Damage, Lost(sim, tank, UnitType.Tank), 1e-3f, "one hit at the centre of the blast, never a direct hit as well");
        Assert.AreEqual(0, Settled(sim).Shells.Length);
    }

    [Test]
    public void TheReloadIsStillFiveSeconds()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, Spot, UnitType.Heavy);
        Shell first = Fired(sim);
        Land(sim, first);
        Shell second = Fired(sim);
        Assert.AreEqual(100, second.FiredTick - first.FiredTick, 1);
    }

    [Test]
    public void ATargetDrivingStraightIsHit()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(14f, 30f), UnitType.Artillery); // the Tank's road passes in range
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(20f, 4f));
        sim.Tick();
        sim.Move(SimHarness.OwnerB, new int2(20, 90), tank[0]);
        Shell shell = Fired(sim);
        Assert.Greater(math.lengthsq(Settled(sim).Velocity[Settled(sim).SlotOf(tank[0], sim.Context.Clock.UnitCount)]), 1f, "it was on the move when the gun fired");
        Land(sim, shell);
        Assert.Less(math.distance(sim.UnitById(tank[0]).Position, shell.Landing), 0.5f, "the shell came down where it had got to (the Tank has made one more step since)");
        Assert.Greater(Lost(sim, tank, UnitType.Tank), Falloff(0.25f, sim.Config.GetUnit(UnitType.Artillery).SplashMedium), "about a whole shell");
    }

    [Test]
    public void ACrowdsShoveDoesNotThrowOffTheAim()
    {
        using var sim = Sim();
        var bodies = new List<int[]>();
        for (int i = 0; i < 8; i++) bodies.Add(sim.Spawn(SimHarness.OwnerB, Spot + new float2(i % 3, i / 3) * 0.05f));
        sim.Tick();
        sim.Tick();
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery); // fires while they push apart, at up to twice a Tank's speed
        Shell shell = Fired(sim);
        Assert.Less(bodies.Min(b => math.distance(sim.UnitById(b[0]).Position, shell.Landing)), 1f, "not a tile of the 10 or so the shove would have sent it");
    }

    [Test]
    public void ATargetThatTurnsBackIsMissedAndTheShellStillLands()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, new float2(14f, 30f), UnitType.Artillery); // the Tank's road passes in range
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(20f, 4f));
        sim.Tick();
        sim.Move(SimHarness.OwnerB, new int2(20, 90), tank[0]);
        Shell shell = Fired(sim);
        sim.Move(SimHarness.OwnerB, new int2(20, 4), tank[0]);
        Land(sim, shell);
        Assert.Greater(math.distance(sim.UnitById(tank[0]).Position, shell.Landing), 4f);
        Assert.AreEqual(0f, Lost(sim, tank, UnitType.Tank), "it dodged");
        SimData data = Settled(sim);
        Assert.AreEqual(1, data.ShellLandings.Length, "a miss still comes down");
        Assert.AreEqual(shell.Landing, data.ShellLandings[0].Point);
        Assert.AreEqual(SimHarness.OwnerA, data.ShellLandings[0].OwnerId);
        sim.Tick();
        Assert.AreEqual(0, Settled(sim).ShellLandings.Length, "reported for one tick");
    }

    [Test]
    public void EachClassHasItsOwnBlastRadiusAndAlliesAreSpared()
    {
        using var sim = Sim();
        UnitConfig gun = sim.Config.GetUnit(UnitType.Artillery);
        float medium = gun.SplashMedium, fast = gun.SplashFast, heavy = gun.Splash;
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, Spot, UnitType.Builder); // the nearest, so the target
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetAttack, OwnerId = SimHarness.OwnerB, TargetOwnerId = SimHarness.OwnerA, Flag = false }); // B leaves the ally below alone
        int[] tankIn = sim.Spawn(SimHarness.OwnerB, Spot + new float2(0f, medium - 0.2f));
        int[] tankOut = sim.Spawn(SimHarness.OwnerB, Spot - new float2(0f, medium + 0.2f));
        int[] heavyIn = sim.Spawn(SimHarness.OwnerB, Spot + new float2(heavy - 0.2f, 0f), UnitType.Heavy);
        int[] heavyOut = sim.Spawn(SimHarness.OwnerB, Spot + math.normalize(new float2(1f, 1f)) * (heavy + 0.2f), UnitType.Heavy);
        int[] scoutIn = sim.Spawn(SimHarness.OwnerB, Spot + new float2(fast - 0.2f, 0f), UnitType.Scout);
        int[] scoutOut = sim.Spawn(SimHarness.OwnerB, Spot + new float2(0f, fast + 0.2f), UnitType.Scout);
        int[] ally = sim.Spawn(SimHarness.OwnerA, Spot - new float2(1f, 0f), UnitType.Builder);
        Shell shell = Fired(sim);
        Assert.AreEqual(Spot, shell.Landing);
        Land(sim, shell);
        Assert.AreEqual(Falloff(medium - 0.2f, medium), Lost(sim, tankIn, UnitType.Tank), 0.5f);
        Assert.AreEqual(0f, Lost(sim, tankOut, UnitType.Tank), "outside the Tank's radius");
        Assert.AreEqual(Falloff(heavy - 0.2f, heavy), Lost(sim, heavyIn, UnitType.Heavy), 0.5f);
        Assert.AreEqual(0f, Lost(sim, heavyOut, UnitType.Heavy), "outside the Heavy's radius, though a Tank there would be hit");
        Assert.AreEqual(Falloff(fast - 0.2f, fast), Lost(sim, scoutIn, UnitType.Scout), 0.5f);
        Assert.AreEqual(0f, Lost(sim, scoutOut, UnitType.Scout), "outside the Scout's radius");
        Assert.AreEqual(0f, Lost(sim, ally, UnitType.Builder), "allies are not hit");
    }

    [Test]
    public void ASuicideBomberIsHitAsAFastUnit()
    {
        using var sim = Sim();
        float fast = sim.Config.GetUnit(UnitType.Artillery).SplashFast;
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, Spot, UnitType.Builder);
        int[] near = sim.Spawn(SimHarness.OwnerB, Spot + new float2(fast - 0.2f, 0f), UnitType.SuicideBomber);
        int[] far = sim.Spawn(SimHarness.OwnerB, Spot + new float2(0f, fast + 0.2f), UnitType.SuicideBomber);
        Land(sim, Fired(sim));
        Assert.AreEqual(0f, sim.UnitById(near[0]).Health, "inside the fast radius even the edge's half of 70 kills a 30-health bomber");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.SuicideBomber).Health, sim.UnitById(far[0]).Health);
    }

    [Test]
    public void TheFastListComesFromTheConfig()
    {
        using var sim = Sim(c => c.GetUnit(UnitType.Artillery).FastUnits = new List<UnitType> { UnitType.Tank });
        UnitConfig gun = sim.Config.GetUnit(UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, Spot, UnitType.Builder);
        int[] tank = sim.Spawn(SimHarness.OwnerB, Spot + new float2(gun.SplashFast - 0.2f, 0f));
        int[] scout = sim.Spawn(SimHarness.OwnerB, Spot + new float2(0f, gun.SplashFast - 0.2f), UnitType.Scout);
        Land(sim, Fired(sim));
        Assert.AreEqual(Falloff(gun.SplashFast - 0.2f, gun.SplashFast), Lost(sim, tank, UnitType.Tank), 0.5f, "listed as fast");
        Assert.AreEqual(0f, Lost(sim, scout, UnitType.Scout), "no longer listed");
    }

    [Test]
    public void ABuildingTargetTakesTheWholeShellAndItsNeighbourTheFalloff()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        int target = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(26, 30), complete: true);
        int neighbour = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(28, 30), complete: true); // its edge 1 tile from the centre
        int far = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(26, 34), complete: true); // 3 tiles: past the 2.0
        Shell shell = Fired(sim);
        Assert.AreEqual(Spot, shell.Landing, "the target's own position");
        Land(sim, shell);
        sim.Tick(); // the boundary writes building damage back
        float health = sim.Config.GetBuilding(BuildingType.Barracks).Health;
        Assert.AreEqual(1f - 2f * Damage / health, sim.BuildingHealthShare(target), 1e-3f, "double damage to buildings");
        Assert.AreEqual(1f - 2f * Falloff(1f, 2f) / health, sim.BuildingHealthShare(neighbour), 1e-3f);
        Assert.AreEqual(1f, sim.BuildingHealthShare(far));
    }

    [Test]
    public void AWallInTheBlastIsHitByTheSameRadiusAsABuilding()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerB, new float2(26.5f, 30.5f)); // the target: units come before walls
        sim.Tick(); // owner B exists from here, so it can own the walls
        int2 near = new int2(27, 31), far = new int2(26, 33);
        sim.PlaceWall(SimHarness.OwnerB, near);
        sim.PlaceWall(SimHarness.OwnerB, far);
        sim.Tick();
        sim.ForceBuiltWall(near);
        sim.ForceBuiltWall(far);
        SimData data = Settled(sim);
        float full = data.WallHealth[sim.Map.Grid.Index(near)];
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 30.5f), UnitType.Artillery);
        Land(sim, Fired(sim));
        sim.Tick();
        data = Settled(sim);
        Assert.AreEqual(full - Falloff(math.SQRT2, 2f), data.WallHealth[sim.Map.Grid.Index(near)], 1e-2f, "1.41 tiles from the landing");
        Assert.AreEqual(full, data.WallHealth[sim.Map.Grid.Index(far)], "3 tiles: past the 2.0");
    }

    [Test]
    public void AShellOutlivesItsGunAndItsOwnerGetsTheKill()
    {
        using var sim = Sim();
        var kills = new List<(int owner, int killer)>();
        sim.Context.UnitKilled += (owner, killer) => kills.Add((owner, killer));
        int[] gun = sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        int[] tank = sim.Spawn(SimHarness.OwnerB, Spot);
        sim.Tick();
        sim.SetUnitHealth(tank[0], Damage / 2f);
        Shell shell = Fired(sim);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, OwnerId = SimHarness.OwnerA, Ids = gun });
        sim.Tick();
        sim.Tick();
        Assert.IsFalse(sim.Units().Any(u => u.Id == gun[0]), "the gun is gone");
        Assert.AreEqual(1, Settled(sim).Shells.Length, "its shell flies on");
        Land(sim, shell);
        Assert.AreEqual(0f, sim.UnitById(tank[0]).Health);
        Assert.AreEqual(SimHarness.OwnerA, sim.UnitById(tank[0]).LastHitBy);
        sim.Tick();
        CollectionAssert.Contains(kills, (SimHarness.OwnerB, SimHarness.OwnerA), "the kill goes to the gun's owner");
    }

    [Test]
    public void ALandingRaisesTheVictimsAlarmFromWhereTheGunFired()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, Spot);
        Shell shell = Fired(sim);
        int slot = Settled(sim).OwnerSlotOf(SimHarness.OwnerB);
        Assert.AreEqual(0, Settled(sim).AlarmCount[slot], "nothing has hit it yet");
        Land(sim, shell);
        SimData data = Settled(sim);
        Assert.AreEqual(1, data.AlarmCount[slot]);
        Assert.AreEqual(shell.From, data.Alarms[slot * DefendAlarms.PerOwner].From);
        Assert.AreEqual(Spot, data.Alarms[slot * DefendAlarms.PerOwner].Victim);
    }

    [Test]
    public void TheMatchEndWipeBringsNothingDown()
    {
        using var sim = Sim();
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        sim.Spawn(SimHarness.OwnerB, Spot);
        Shell shell = Fired(sim);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.DestroyAll });
        sim.Tick();
        Assert.AreEqual(0, Settled(sim).Shells.Length);
        while (sim.Context.Clock.Tick <= shell.LandTick + 2)
        {
            sim.Tick();
            Assert.AreEqual(0, Settled(sim).ShellLandings.Length);
        }
    }

    [Test]
    public void WithoutAShellSpeedTheGunStillHitsAtOnce()
    {
        using var sim = Sim(c => c.GetUnit(UnitType.Artillery).ShellSpeed = 0f);
        sim.Spawn(SimHarness.OwnerA, Gun, UnitType.Artillery);
        int[] tank = sim.Spawn(SimHarness.OwnerB, Spot);
        for (int i = 0; i < 5; i++) sim.Tick();
        Assert.AreEqual(Damage, Lost(sim, tank, UnitType.Tank), 1e-3f);
        Assert.AreEqual(0, Settled(sim).Shells.Length);
    }
}
