using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>
/// v0.13 Bastion, world-level: the Gun turret's weapon state and targeting (it shoots like a unit that can't move),
/// and the Jammer's jam mask (enemies see in it only up close, their Radars not at all).
/// </summary>
public class SimBastionTests
{
    private const int OwnerC = 303;
    /// <summary>The turret's anchor in every turret test: its centre, where it fires from, is (20.5, 20.5).</summary>
    private static readonly int2 TurretAt = new int2(20, 20);

    private static SimHarness Sim(System.Action<Config.GameConfigData> configure = null) =>
        new SimHarness(SimHarness.OpenMap(64), c => { c.Simulation.TargetSearchSliceTicks = 1; configure?.Invoke(c); });

    /// <summary>A 64x64 map with rock at x = 23..24, top to bottom.</summary>
    private static SimHarness RockSim()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
            rows[r] = r == 0 || r == 63 ? new string('B', 64) : "B" + new string('.', 22) + "##" + new string('.', 38) + "B";
        return new SimHarness(MapStore.FromAscii(rows), c => c.Simulation.TargetSearchSliceTicks = 1);
    }

    private static BuildingWeapon Weapon(SimHarness sim, int id) => sim.Em.GetComponentData<BuildingWeapon>(sim.BuildingEntity(id));

    private static float Health(SimHarness sim, int id) => sim.UnitById(id).Health;

    /// <summary>Counts the building attack events by the given building, read where the server reads them.</summary>
    private static int[] CountShots(SimHarness sim, int building)
    {
        var shots = new int[1];
        sim.Context.Settled += (data, tick, n) =>
        {
            foreach (int2 e in data.BuildingAttackEvents) if (e.x == building) shots[0]++;
        };
        return shots;
    }

    private static void SetUnpaid(SimHarness sim, int building, bool unpaid)
    {
        Entity e = sim.BuildingEntity(building);
        UpkeepComponent upkeep = sim.Em.GetComponentData<UpkeepComponent>(e);
        upkeep.unpaid = unpaid;
        sim.Em.SetComponentData(e, upkeep);
    }

    private static void SetBuildingHealth(SimHarness sim, int building, float health)
    {
        Entity e = sim.BuildingEntity(building);
        HealthComponent h = sim.Em.GetComponentData<HealthComponent>(e);
        h.currentHealth = health;
        sim.Em.SetComponentData(e, h);
    }

    private static int SlotOf(SimHarness sim, int building)
    {
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        int slot = data.BuildingSlotOfIndex[NetIdAllocator.IndexOf(building)];
        return slot < data.BuildingCount[0] && data.BuildingIds[slot] == building ? slot : -1;
    }

    private static List<int> Attackers(SimHarness sim)
    {
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        var list = new List<int>();
        for (int i = 0; i < data.AttackerBuildings.Length; i++) list.Add(data.AttackerBuildings[i]);
        return list;
    }

    // ---- Task 3: weapon state ----

    [Test]
    public void OnlyAWeaponedBuildingGetsAWeaponAndTheJammerItsTag()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt);
        int jammer = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Jammer, new int2(30, 30));
        int miner = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(40, 40));
        sim.Tick();
        Assert.IsTrue(sim.Em.HasComponent<BuildingWeapon>(sim.BuildingEntity(turret)));
        Assert.AreEqual(-1, Weapon(sim, turret).TargetId);
        Assert.IsTrue(sim.Em.HasComponent<Construction>(sim.BuildingEntity(turret)), "a turret starts as a blueprint");
        Assert.IsTrue(sim.Em.HasComponent<Construction>(sim.BuildingEntity(jammer)), "so does a Jammer");
        Assert.IsTrue(sim.Em.HasComponent<Jammer>(sim.BuildingEntity(jammer)));
        Assert.IsFalse(sim.Em.HasComponent<BuildingWeapon>(sim.BuildingEntity(jammer)));
        Assert.IsFalse(sim.Em.HasComponent<BuildingWeapon>(sim.BuildingEntity(miner)));
        Assert.AreEqual("Gun turret", WAR2D.UI.AlertFeedModel.BuildingName(BuildingType.GunTurret));
        Assert.AreEqual("Jammer", WAR2D.UI.AlertFeedModel.BuildingName(BuildingType.Jammer));
    }

    [Test]
    public void TheAttackerListFollowsCompletionAndDeath()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt);
        sim.Tick();
        sim.Tick();
        CollectionAssert.IsEmpty(Attackers(sim), "a blueprint is not an attacker");
        sim.AddWork(turret, 10000f);
        sim.Tick();
        CollectionAssert.AreEqual(new[] { SlotOf(sim, turret) }, Attackers(sim), "complete: listed");
        SetBuildingHealth(sim, turret, 0f);
        sim.Tick();
        CollectionAssert.IsEmpty(Attackers(sim), "dead: gone");
    }

    [Test]
    public void SlotReuseClearsWeaponState()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(25.5f, 20.5f), UnitType.Heavy);
        for (int i = 0; i < 12; i++) sim.Tick();
        int slot = SlotOf(sim, turret);
        Assert.Greater(Weapon(sim, turret).TargetId, 0, "the turret has a target");
        Assert.AreEqual(slot, Attackers(sim).Single());

        sim.Em.DestroyEntity(sim.BuildingEntity(turret)); // destroyed between ticks, as the destruction system does
        int miner = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(40, 40));
        sim.Tick();
        Assert.AreEqual(slot, SlotOf(sim, miner), "the Miner took the turret's slot");
        SimData data = sim.Context.Data;
        Assert.AreEqual((-1, (byte)0, 0f), (data.BuildingTarget[slot], data.BuildingTargetKind[slot], data.BuildingCooldown[slot]));
        CollectionAssert.IsEmpty(Attackers(sim));
    }

    [Test]
    public void UnpaidUpkeepFlagsTheSlot()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Tick();
        SetUnpaid(sim, turret, true);
        sim.Tick();
        Assert.AreNotEqual(0, sim.Context.Data.BuildingFlag[SlotOf(sim, turret)] & BuildingFlags.Unpaid);
        SetUnpaid(sim, turret, false);
        sim.Tick();
        Assert.AreEqual(0, sim.Context.Data.BuildingFlag[SlotOf(sim, turret)] & BuildingFlags.Unpaid);
    }

    // ---- Task 4: targeting and firing ----

    [Test]
    public void ATurretByTheBorderSwitchesFromABuildingToAUnitEnteringRange()
    {
        // One row above the bottom border: the fog row (tiles y = 0..1) is half border rock. The turret first holds an
        // enemy building in range, then an enemy unit stops 4 tiles east on the same row: units come first.
        using var sim = Sim();
        int2 at = new int2(10, 1);
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, at, complete: true);
        int miner = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Miner, new int2(10, 5), complete: true);
        for (int i = 0; i < 12; i++) sim.Tick();
        Assert.AreEqual((miner, TargetKinds.Building), (Weapon(sim, turret).TargetId, Weapon(sim, turret).TargetKind), "the building first");

        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(14.5f, 1.5f), UnitType.Heavy);
        for (int i = 0; i < 12; i++) sim.Tick();
        Assert.AreEqual((heavy[0], TargetKinds.Unit), (Weapon(sim, turret).TargetId, Weapon(sim, turret).TargetKind), "then the unit in range");
        Assert.Less(Health(sim, heavy[0]), sim.Config.GetUnit(UnitType.Heavy).Health, "and shoots it");
    }

    [Test]
    public void ATurretFiresAtAnEnemyUnitInRangeAndSight()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] scout = sim.Spawn(SimHarness.OwnerB, new float2(25.5f, 20.5f), UnitType.Builder);
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 20; i++) sim.Tick();
        Assert.Less(Health(sim, scout[0]), sim.Config.GetUnit(UnitType.Builder).Health);
        Assert.Greater(shots[0], 0, "each shot is a building attack event");
        Assert.AreEqual(scout[0], Weapon(sim, turret).TargetId);
        Assert.AreEqual(TargetKinds.Unit, Weapon(sim, turret).TargetKind);
    }

    [Test]
    public void TheCooldownIsTheAttackIntervalAndTheTableScalesTheDamage()
    {
        using var sim = Sim();
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(27f, 20.5f), UnitType.Heavy); // 6.5 away: outside its own range 5
        var drops = new List<(int tick, float amount)>();
        float last = sim.Config.GetUnit(UnitType.Heavy).Health;
        for (int t = 0; t < 60; t++)
        {
            sim.Tick();
            float now = Health(sim, heavy[0]);
            if (now < last) drops.Add((t, last - now));
            last = now;
        }
        Assert.GreaterOrEqual(drops.Count, 4);
        int interval = (int)math.round(sim.Config.GetBuilding(BuildingType.GunTurret).AttackInterval * sim.Config.Simulation.TickRate);
        for (int i = 1; i < drops.Count; i++) Assert.AreEqual(interval, drops[i].tick - drops[i - 1].tick, "one shot per AttackInterval");
        Assert.AreEqual(8f * 0.4f, drops[0].amount, 1e-3f, "8 damage x0.4 against Heavy armour");
    }

    [Test]
    public void ATurretDoesFullTableDamageToAScout()
    {
        using var sim = Sim();
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] scout = sim.Spawn(SimHarness.OwnerB, new float2(26f, 20.5f), UnitType.Scout); // 5.5 away: outside its range 4
        float start = sim.Config.GetUnit(UnitType.Scout).Health;
        sim.Tick(); // the spawn applies
        for (int i = 0; i < 40 && Health(sim, scout[0]) == start; i++) sim.Tick();
        Assert.AreEqual(start - 8f * 1.5f, Health(sim, scout[0]), 1e-3f, "8 damage x1.5 against Light armour");
    }

    [Test]
    public void ATurretPrefersAUnitThenABuildingThenAWall()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] builder = sim.Spawn(SimHarness.OwnerB, new float2(26.5f, 20.5f), UnitType.Builder); // 6 away
        sim.Tick();
        int miner = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Miner, new int2(22, 22), complete: true); // ~2 away
        int2 wall = new int2(18, 20);                                                                       // 2 away
        sim.PlaceWall(SimHarness.OwnerB, wall);
        sim.Tick();
        sim.ForceBuiltWall(wall);
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.AreEqual((builder[0], TargetKinds.Unit), (Weapon(sim, turret).TargetId, Weapon(sim, turret).TargetKind), "a unit first, though it is the farthest");

        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, Ids = new[] { builder[0] } });
        for (int i = 0; i < 4; i++) sim.Tick();
        Assert.AreEqual((miner, TargetKinds.Building), (Weapon(sim, turret).TargetId, Weapon(sim, turret).TargetKind), "then the building");

        SetBuildingHealth(sim, miner, 0f);
        for (int i = 0; i < 4; i++) sim.Tick();
        Assert.AreEqual((sim.Map.Grid.Index(wall), TargetKinds.Wall), (Weapon(sim, turret).TargetId, Weapon(sim, turret).TargetKind), "then the wall");
        float before = sim.Context.Data.WallHealth[sim.Map.Grid.Index(wall)];
        for (int i = 0; i < 20; i++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        Assert.Less(sim.Context.Data.WallHealth[sim.Map.Grid.Index(wall)], before, "and breaks it");
    }

    [Test]
    public void ABlueprintTurretDoesNotFire()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt);
        int[] builder = sim.Spawn(SimHarness.OwnerB, new float2(25.5f, 20.5f), UnitType.Builder);
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(0, shots[0]);
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Builder).Health, Health(sim, builder[0]));
    }

    [Test]
    public void AnUnpaidTurretDoesNotFireUntilPaidAgain()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(26f, 20.5f), UnitType.Heavy);
        sim.Tick();
        SetUnpaid(sim, turret, true);
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(0, shots[0], "unpaid: not firing");
        Assert.AreEqual(-1, Weapon(sim, turret).TargetId, "and holding no target");
        SetUnpaid(sim, turret, false);
        for (int i = 0; i < 20; i++) sim.Tick();
        Assert.Greater(shots[0], 0, "paid again: firing");
        Assert.Less(Health(sim, heavy[0]), sim.Config.GetUnit(UnitType.Heavy).Health);
    }

    [Test]
    public void ATurretSparesAlliesAndTheirWalls()
    {
        using var sim = Sim();
        sim.Context.TeamResolver = _ => 0; // everyone on one team
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(25.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        int2 wall = new int2(18, 20);
        sim.PlaceWall(SimHarness.OwnerB, wall);
        sim.Tick();
        sim.ForceBuiltWall(wall);
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        Assert.AreEqual(0, shots[0]);
        Assert.AreEqual(-1, Weapon(sim, turret).TargetId);
        Assert.AreEqual(sim.Context.Data.WallMaxHealth, sim.Context.Data.WallHealth[sim.Map.Grid.Index(wall)], "the ally's wall is untouched");
    }

    [Test]
    public void ATurretSparesOwnersOffItsAttackMask()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] builder = sim.Spawn(SimHarness.OwnerB, new float2(25.5f, 20.5f), UnitType.Builder);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetAttack, OwnerId = SimHarness.OwnerA, TargetOwnerId = SimHarness.OwnerB, Flag = false });
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(0, shots[0], "A stopped attacking B");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Builder).Health, Health(sim, builder[0]));
    }

    [Test]
    public void ATurretDoesNotFireIntoTheFogUntilSomeoneLightsTheTarget()
    {
        using var sim = Sim(c => c.Buildings[BuildingType.GunTurret].Sight = 2f); // sees 2 tiles, shoots 7
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(26.5f, 20.5f), UnitType.Builder);
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(0, shots[0], "the target is in the fog");

        sim.Spawn(SimHarness.OwnerA, new float2(26.5f, 24f), UnitType.Builder); // sight 6 lights it
        for (int i = 0; i < 20; i++) sim.Tick();
        Assert.Greater(shots[0], 0, "lit: fired at");
    }

    [Test]
    public void ATurretDoesNotFireThroughRock()
    {
        using var sim = RockSim(); // rock at x = 23..24
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] target = sim.Spawn(SimHarness.OwnerB, new float2(26.5f, 20.5f), UnitType.Builder);
        sim.Spawn(SimHarness.OwnerA, new float2(26.5f, 23f), UnitType.Builder); // lights the target from its side of the rock
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        int grid = sim.Context.VisionOf(SimHarness.OwnerA);
        Assert.IsTrue(sim.Context.Data.Sees(grid, sim.UnitById(target[0]).Position), "the target is seen");
        Assert.AreEqual(0, shots[0], "but not through the rock");
    }

    [Test]
    public void ATurretDoesNotFireBeyondItsRange()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(28f, 20.5f), UnitType.Builder);   // 7.5 away, range 7
        sim.Spawn(SimHarness.OwnerA, new float2(28f, 23f), UnitType.Builder);     // lit
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(0, shots[0]);
    }

    [Test]
    public void ATurretVictimAndAShotTurretRaiseUnderAttackForTheirOwners()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(24.5f, 20.5f), UnitType.Tank); // in each other's range
        var alerts = new List<(int owner, Alert alert)>();
        var service = new AlertService(0f, 4, (owner, list) => { foreach (Alert a in list) alerts.Add((owner, a)); });
        sim.Context.Settled += (data, tick, n) =>
        {
            service.CollectDamage(data, n, id =>
            {
                int index = NetIdAllocator.IndexOf(id), b = data.BuildingSlotOfIndex[index];
                bool found = b < data.BuildingCount[0] && data.BuildingIds[b] == id;
                return (found, found ? data.BuildingOwnerId[b] : 0, found ? data.BuildingAnchor[b] : default);
            }, tick);
            service.Flush();
        };
        for (int i = 0; i < 40; i++) sim.Tick();
        Assert.IsTrue(alerts.Any(a => a.owner == SimHarness.OwnerB && a.alert.Kind == AlertKind.UnderAttack), "the turret's victim's owner");
        Assert.IsTrue(alerts.Any(a => a.owner == SimHarness.OwnerA && a.alert.Kind == AlertKind.UnderAttack && math.all(a.alert.Tile == TurretAt)), "the turret's owner, at the turret");
    }

    [Test]
    public void AWallShotIsABuildingAttackEventWithNoTarget()
    {
        using var sim = Sim();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(55f, 55f), UnitType.Builder); // B exists, far out of range
        int2 wall = new int2(18, 20);
        sim.PlaceWall(SimHarness.OwnerB, wall);
        sim.Tick();
        sim.ForceBuiltWall(wall);
        var targets = new List<int>();
        sim.Context.Settled += (data, tick, n) =>
        {
            foreach (int2 e in data.BuildingAttackEvents) if (e.x == turret) targets.Add(e.y);
        };
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.AreEqual(TargetKinds.Wall, Weapon(sim, turret).TargetKind, "the turret shoots the wall");
        Assert.Greater(targets.Count, 0, "each wall shot is an event, so clients see the turret fire");
        CollectionAssert.AreEqual(Enumerable.Repeat(0, targets.Count), targets, "naming no target (a wall tile is no entity)");
    }

    [Test]
    public void WithTheDefaultSliceATurretHoldsItsTargetWhileSlotsAheadOfItMove()
    {
        // The default TargetSearchSliceTicks: the turret re-searches on some ticks and keeps its target between them.
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        Assert.Greater(sim.Context.Data.Slice, 1, "the default config slices the search");
        int miner = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(40, 40), complete: true); // a slot ahead of the turret
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, TurretAt, complete: true);
        int[] heavy = sim.Spawn(SimHarness.OwnerB, new float2(26f, 20.5f), UnitType.Heavy);
        sim.Spawn(SimHarness.OwnerB, new float2(28.5f, 22.5f), UnitType.Heavy); // a second enemy, farther, in range
        var shots = new List<int>();
        sim.Context.Settled += (data, tick, n) =>
        {
            foreach (int2 e in data.BuildingAttackEvents) if (e.x == turret) shots.Add(e.y);
        };
        for (int i = 0; i < 20; i++) sim.Tick();
        Assert.AreEqual(heavy[0], Weapon(sim, turret).TargetId, "the nearest Heavy");
        int slotBefore = SlotOf(sim, turret);

        sim.Em.DestroyEntity(sim.BuildingEntity(miner)); // destroyed between ticks: the turret's slot moves
        sim.Tick();
        Assert.AreNotEqual(slotBefore, SlotOf(sim, turret), "the turret changed slots");
        int created = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Miner, new int2(44, 44), complete: true);
        int firedBefore = shots.Count;
        for (int i = 0; i < 60; i++)
        {
            sim.Tick();
            Assert.AreEqual(heavy[0], Weapon(sim, turret).TargetId, $"tick {i}: the same target across search and non-search ticks");
        }
        Assert.IsTrue(sim.Em.Exists(sim.BuildingEntity(created)));
        int interval = (int)math.round(sim.Config.GetBuilding(BuildingType.GunTurret).AttackInterval * sim.Config.Simulation.TickRate);
        Assert.GreaterOrEqual(shots.Count - firedBefore, 60 / interval - 1, "and it kept firing");
        CollectionAssert.AreEqual(Enumerable.Repeat(heavy[0], shots.Count), shots, "every shot at that one id");
    }

    [Test]
    public void ATurretInsideAnEnemyJamSeesAndShootsOnlyWithinJamSight()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96), c => c.Simulation.TargetSearchSliceTicks = 1);
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, new int2(40, 40), complete: true);
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(36, 44), complete: true); // centre (36.5, 44.5)
        int[] far = sim.Spawn(SimHarness.OwnerB, new float2(42.5f, 44.5f), UnitType.Heavy); // 6 away: in range 7, beyond JamSight 3
        int[] shots = CountShots(sim, turret);
        for (int i = 0; i < 30; i++) sim.Tick();
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, new float2(42.5f, 44.5f)), "the target stands in B's jam");
        Assert.AreEqual(0, shots[0], "jammed: the turret doesn't see it, so doesn't fire");
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Heavy).Health, Health(sim, far[0]));

        sim.Spawn(SimHarness.OwnerB, new float2(38.5f, 44.5f), UnitType.Heavy); // 2 away: within JamSight
        for (int i = 0; i < 20; i++) sim.Tick();
        Assert.Greater(shots[0], 0, "within JamSight it sees and fires");
    }

    // ---- Task 6: the jam ----

    /// <summary>B's Jammer, complete, centred on (40, 40) (anchor (40, 40), 2x2), radius 12.</summary>
    private static readonly int2 JammerAt = new int2(40, 40);
    private static readonly float2 JammerCentre = new float2(40f, 40f);

    private static bool Jammed(SimHarness sim, int owner, float2 p)
    {
        sim.Em.CompleteAllTrackedJobs();
        return sim.Context.Data.Jammed(sim.Context.VisionOf(owner), p);
    }

    private static bool Sees(SimHarness sim, int owner, float2 p)
    {
        sim.Em.CompleteAllTrackedJobs();
        return sim.Context.Data.Sees(sim.Context.VisionOf(owner), p);
    }

    private static void Pass(SimHarness sim, int ticks = 8)
    {
        for (int i = 0; i < ticks; i++) sim.Tick();
    }

    [Test]
    public void TheJamIsBuiltWhenAJammerCompletesAndClearedWhenItDies()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int jammer = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt);
        Pass(sim);
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, JammerCentre), "a blueprint doesn't jam");
        sim.AddWork(jammer, 10000f);
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, JammerCentre), "complete: jammed for A");
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, JammerCentre + new float2(11f, 0f)), "inside the radius");
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, JammerCentre + new float2(14f, 0f)), "outside it");
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerB, JammerCentre), "never for its owner");

        SetBuildingHealth(sim, jammer, 0f);
        Pass(sim);
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, JammerCentre), "dead: cleared");
    }

    [Test]
    public void AnUnpaidJammerStopsJammingUntilPaid()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int jammer = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt, complete: true);
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, JammerCentre));
        SetUnpaid(sim, jammer, true);
        Pass(sim);
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, JammerCentre), "unpaid: cleared");
        SetUnpaid(sim, jammer, false);
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, JammerCentre), "paid again: back");
    }

    [Test]
    public void TheJamSparesWhomItsOwnerSharesVisionWithAndFollowsChanges()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Spawn(OwnerC, new float2(10, 80));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt, complete: true);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = SimHarness.OwnerB, TargetOwnerId = SimHarness.OwnerA, Flag = true });
        Pass(sim);
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, JammerCentre), "B shares vision with A: A is unaffected");
        Assert.IsTrue(Jammed(sim, OwnerC, JammerCentre), "C isn't");

        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = SimHarness.OwnerB, TargetOwnerId = SimHarness.OwnerA, Flag = false });
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, JammerCentre), "sharing stopped: rebuilt, A is jammed too");

        // A sharing its vision with B doesn't matter: only the Jammer owner's sharing does.
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = SimHarness.OwnerA, TargetOwnerId = SimHarness.OwnerB, Flag = true });
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, JammerCentre));
    }

    [Test]
    public void ADefeatedSpectatorsGridIsNeverJammed()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt, complete: true);
        Pass(sim);
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        Assert.IsFalse(data.Jammed(SimData.SeesAll, JammerCentre), "the spectator grid has no jam");
        Assert.IsTrue(data.Sees(SimData.SeesAll, JammerCentre), "and sees everything");
    }

    [Test]
    public void InsideTheJamAUnitSeesOnlyWithinJamSight()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        int[] tank = sim.Spawn(SimHarness.OwnerA, new float2(31f, 41f)); // 9 from the Jammer, sight 8
        Pass(sim);
        float2 far = new float2(37f, 41f), near = new float2(33f, 41f); // 6 and 2 tiles from the Tank
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, far), "no Jammer yet: seen at 6");
        int jammer = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt, complete: true);
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, far));
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, far), "jammed: not seen beyond JamSight");
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, near), "seen within JamSight");
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, sim.UnitById(tank[0]).Position), "its own cell");
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, JammerCentre), "the Jammer itself is in the jam: not seen from 9");
        Assert.IsTrue(Sees(sim, SimHarness.OwnerB, far), "the Jammer's owner is unaffected");

        sim.Teleport(tank[0], new float2(38f, 40f));
        Pass(sim);
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, JammerCentre), "up close it is seen");
        Assert.IsTrue(sim.Em.Exists(sim.BuildingEntity(jammer)));
    }

    [Test]
    public void JamSightIsMeasuredBetweenTheCellsClosestTilesNotTheirCentres()
    {
        // Fog cells are 2 tiles. The Tank stands in cell x = 15 (tiles 30..31), the targets in cells two away.
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(31.9f, 41f));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt, complete: true);
        Pass(sim);
        float2 straight = new float2(34.1f, 41f); // 2.2 tiles away; the cells' centres are 4 apart, closest tiles 3
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, straight));
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, straight), "a target 2.2 tiles away is within JamSight 3");
        float2 diagonal = new float2(34.1f, 45.1f); // cells (2, 2) away: closest tiles (3, 3) apart, 4.2 > 3
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, diagonal));
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, diagonal), "a diagonal cell whose closest tile is beyond JamSight is not seen");
        float2 knight = new float2(34.1f, 43.1f); // cells (2, 1) away: closest tiles (3, 1) apart, 3.2 > 3
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, knight), "nor one just beyond it");
    }

    [Test]
    public void ARadarIsBlindInAJamAndNeverPingsForAJammedContact()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        int radar = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Radar, new int2(20, 40), complete: true);
        sim.Spawn(SimHarness.OwnerB, new float2(36f, 40f)); // 15 from the Radar (sight 20), 4 from the Jammer
        Pass(sim);
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, new float2(36f, 40f)), "no Jammer yet: the Radar sees it");

        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, JammerAt, complete: true);
        Pass(sim);
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, new float2(36f, 40f)), "jammed: the Radar sees nothing there");
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, new float2(26f, 40f)), "outside the jam it still sees");

        sim.Em.CompleteAllTrackedJobs();
        var pings = new List<Alert>();
        var radars = new RadarService(0f, 0f);
        var view = new RadarView { Id = radar, OwnerId = SimHarness.OwnerA, Tile = new int2(20, 40), Centre = Footprint.VisualCenter(new int2(20, 40), new int2(2, 2)), Radius = 20f };
        radars.Update(sim.Context.Data, sim.Context.Clock.UnitCount, new[] { view }, 0, (owner, alert) => pings.Add(alert));
        CollectionAssert.IsEmpty(pings, "the only contact is jammed");

        sim.Spawn(SimHarness.OwnerB, new float2(30f, 30f)); // 14 from the Jammer: outside the jam
        Pass(sim);
        sim.Em.CompleteAllTrackedJobs();
        radars.Update(sim.Context.Data, sim.Context.Clock.UnitCount, new[] { view }, 10, (owner, alert) => pings.Add(alert));
        Assert.AreEqual(1, pings.Count, "a contact outside the jam pings");
    }
}
