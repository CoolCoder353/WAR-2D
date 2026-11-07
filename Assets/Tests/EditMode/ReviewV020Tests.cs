using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Code review of v0.19 and v0.20 (2026-11-07): the sim and input fixes that a test can pin down.</summary>
public class ReviewV020Tests
{
    private const int Killer = 101, Late = 202;

    /// <summary>A hit queued after the killing one changes nothing: not the kill, not the death's cause, and it lights no fire.</summary>
    [Test]
    public void TheFirstKillingHitKeepsTheKill()
    {
        int id = (1 << 20) | 3; // generation 1, index 3
        using var hits = new NativeQueue<DamageHit>(Allocator.Temp);
        using var health = new NativeArray<float>(new[] { 10f }, Allocator.Temp);
        using var lastHitBy = new NativeArray<int>(1, Allocator.Temp);
        using var buildingHealth = new NativeArray<float>(new[] { 5f }, Allocator.Temp);
        using var buildingDamage = new NativeArray<float>(1, Allocator.Temp);
        using var wallDamage = new NativeQueue<TileWork>(Allocator.Temp);
        using var hqLastHitBy = new NativeArray<int>(SimData.MaxOwners, Allocator.Temp);
        using var buildingKind = new NativeArray<byte>(new[] { (byte)BuildingType.Base }, Allocator.Temp);
        using var buildingOwnerSlot = new NativeArray<byte>(new byte[] { 1 }, Allocator.Temp);
        using var ownerSlot = new NativeArray<byte>(new byte[] { 1 }, Allocator.Temp);
        using var positions = new NativeArray<float2>(1, Allocator.Temp);
        using var buildingPositions = new NativeArray<float2>(1, Allocator.Temp);
        using var alarms = new NativeArray<DefendAlarm>(SimData.MaxOwners * DefendAlarms.PerOwner, Allocator.Temp);
        using var alarmCount = new NativeArray<int>(SimData.MaxOwners, Allocator.Temp);
        using var idOf = new NativeArray<int>(new[] { id }, Allocator.Temp);
        using var buildingIds = new NativeArray<int>(new[] { (1 << 20) | 4 }, Allocator.Temp);
        using var airborne = new NativeArray<byte>(1, Allocator.Temp);
        using var combat = new NativeArray<UnitCombat>(8, Allocator.Temp);
        using var buildingFires = new NativeArray<BuildingFire>(8, Allocator.Temp);
        using var lastHitKind = new NativeArray<byte>(8, Allocator.Temp);
        using var burningUnits = new NativeList<int>(4, Allocator.Temp);
        using var burningBuildings = new NativeList<int>(4, Allocator.Temp);

        hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = 0, Amount = 50f, AttackerOwner = Killer, HitKind = HitKinds.Shot });
        hits.Enqueue(new DamageHit { Kind = TargetKinds.Unit, Slot = 0, Amount = 5f, AttackerOwner = Late, HitKind = HitKinds.Fire, Ignite = 1 });
        hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = 0, Amount = 50f, AttackerOwner = Killer });
        hits.Enqueue(new DamageHit { Kind = TargetKinds.Building, Slot = 0, Amount = 5f, AttackerOwner = Late, Ignite = 1 });
        new ApplyDamageJob
        {
            Hits = hits, Health = health, LastHitBy = lastHitBy, BuildingHealth = buildingHealth, BuildingDamage = buildingDamage,
            WallDamage = wallDamage, HqLastHitBy = hqLastHitBy, BuildingKind = buildingKind, BuildingOwnerSlot = buildingOwnerSlot,
            OwnerSlot = ownerSlot, Positions = positions, BuildingPositions = buildingPositions, Alarms = alarms, AlarmCount = alarmCount,
            AlarmCellTiles = 8f, IdOf = idOf, BuildingIds = buildingIds, Airborne = airborne, Combat = combat, BuildingFires = buildingFires,
            LastHitKind = lastHitKind, BurningUnits = burningUnits, BurningBuildings = burningBuildings, BurnSeconds = 4f,
        }.Execute();

        Assert.AreEqual(0f, health[0]);
        Assert.AreEqual(Killer, lastHitBy[0], "the unit's kill stays with the shot that killed it");
        Assert.AreEqual(HitKinds.Shot, lastHitKind[3], "and so does its death's cause");
        Assert.AreEqual(0, burningUnits.Length, "a corpse is not set alight");
        Assert.AreEqual(Killer, hqLastHitBy[1], "the HQ's fall goes to the hit that destroyed it");
        Assert.AreEqual(0, burningBuildings.Length);
    }

    /// <summary>A building still queued when its owner is eliminated is created in the same drain as the kill, and must not outlive it.</summary>
    [Test]
    public void ABuildingQueuedAtAnOwnersEliminationGoesWithIt()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int barracks = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(10, 10), true);
        int theirs = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(20, 20), true);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillOwner, OwnerId = SimHarness.OwnerA });
        sim.Tick();

        Entity mine = sim.BuildingEntity(barracks);
        Assert.IsTrue(mine == Entity.Null || sim.Em.GetComponentData<HealthComponent>(mine).currentHealth <= 0f, "the eliminated owner's new building is destroyed");
        Assert.Greater(sim.BuildingHealthShare(theirs), 0f, "nobody else's");
    }

    /// <summary>A rebind records the left or right key, the defaults the plain one: they still conflict.</summary>
    [Test]
    public void ALeftModifierConflictsWithItsPlainBinding()
    {
        Assert.IsTrue(Rebinding.SameControl("<Keyboard>/leftCtrl", "<Keyboard>/ctrl"));
        Assert.IsTrue(Rebinding.SameControl("<Keyboard>/shift", "<Keyboard>/rightShift"));
        Assert.IsTrue(Rebinding.SameControl("<Keyboard>/A", "<Keyboard>/a"));
        Assert.IsFalse(Rebinding.SameControl("<Keyboard>/leftCtrl", "<Keyboard>/rightCtrl"), "left and right are different keys");
        Assert.IsFalse(Rebinding.SameControl("<Keyboard>/leftCtrl", "<Keyboard>/alt"));
        Assert.IsFalse(Rebinding.SameControl("<Keyboard>/s", "<Keyboard>/shift"));
    }
}
