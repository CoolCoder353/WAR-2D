using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>The third playtest of 2026-11-04: targeting, large units, decay, the blast rules and the defences.</summary>
public class PlaytestFix3Tests
{
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c => c.Simulation.TargetSearchSliceTicks = 1);

    [TestCase(UnitType.Digger)]
    [TestCase(UnitType.Flamethrower)]
    public void AShortRangedUnitBesideAnHqAttacksIt(UnitType type)
    {
        using var sim = Sim();
        int hq = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Base, new int2(20, 20), complete: true);
        // Just off the HQ's right edge: its centre is further away than the unit's range, its edge isn't.
        int[] unit = sim.Spawn(SimHarness.OwnerA, new float2(23.6f, 20.5f), type);
        sim.TickSeconds(3f);
        Assert.Less(sim.BuildingHealthShare(hq), 1f, "the unit stood beside the HQ without firing");
        Assert.AreEqual(TargetKinds.Building, sim.UnitById(unit[0]).TargetKind);
    }

    [Test]
    public void AHeavySpawnedTouchingABuildingCanWalkAway()
    {
        using var sim = Sim();
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.HeavyFactory, new int2(20, 20), complete: true);
        // A tile centre right beside the footprint, as the spawn search picks: the body overlaps the footprint.
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new float2(22.5f, 20.5f), UnitType.Heavy);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(40, 20), heavy);
        sim.TickSeconds(10f);
        Assert.Greater(sim.UnitById(heavy[0]).Position.x, 30f, "the Heavy never left the factory's side");
    }

    [Test]
    public void AHeavyPushedOutByANewBuildingCanWalkAway()
    {
        using var sim = Sim();
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new float2(20.5f, 20.5f), UnitType.Heavy);
        sim.Tick();
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.HeavyFactory, new int2(20, 20), complete: true);
        sim.TickSeconds(0.5f);
        float2 pushed = sim.UnitById(heavy[0]).Position;
        sim.Move(SimHarness.OwnerA, new int2(40, 40), heavy);
        sim.TickSeconds(15f);
        Assert.Greater(math.distance(sim.UnitById(heavy[0]).Position, pushed), 10f, "the Heavy froze where the push left it");
    }

    [Test]
    public void AHeavyWalledInAtATileCentreCanWalkAway()
    {
        using var sim = Sim();
        int[] heavy = sim.Spawn(SimHarness.OwnerA, new float2(20.5f, 20.5f), UnitType.Heavy);
        sim.Tick();
        sim.ForceBuiltWall(new int2(21, 20)); // beside it: the tile under it loses its clearance, the unit isn't pushed
        sim.TickSeconds(0.5f);
        sim.Move(SimHarness.OwnerA, new int2(5, 20), heavy);
        sim.TickSeconds(8f);
        Assert.Less(sim.UnitById(heavy[0]).Position.x, 15f, "the Heavy froze beside the new wall");
    }

    [Test]
    public void DecayTakesOneWholeUnitAtATime()
    {
        using var sim = Sim();
        sim.Context.BudgetOf = _ => 0f;
        int[] tanks = { sim.Spawn(SimHarness.OwnerA, new float2(10, 10))[0], sim.Spawn(SimHarness.OwnerA, new float2(12, 10))[0], sim.Spawn(SimHarness.OwnerA, new float2(14, 10))[0], sim.Spawn(SimHarness.OwnerA, new float2(16, 10))[0] };
        sim.TickSeconds(2.5f);
        Unit[] alive = sim.Units().Where(u => tanks.Contains(u.Id)).ToArray();
        Assert.LessOrEqual(alive.Count(u => u.Health < 100f), 1, "only one unit decays at a time");

        sim.TickSeconds(6f); // 4 unpaid units at 5 % each: a unit every 5 s, then a little slower
        Assert.Less(sim.Units().Count(u => tanks.Contains(u.Id) && u.Health > 0f), 4, "a whole unit is gone");
        Assert.LessOrEqual(sim.Units().Count(u => tanks.Contains(u.Id) && u.Health > 0f && u.Health < 100f), 1);
    }

    [Test]
    public void ABuildingLeavesTheUnitsTheirUpkeep()
    {
        var player = new ServerPlayer(null, 10f) { UnitUpkeepReserve = 8f };
        Assert.IsTrue(player.TryPayBuildingUpkeep(2f), "the units' 8 stays in the bank");
        Assert.AreEqual(8f, player.Resources);
        Assert.IsFalse(player.TryPayBuildingUpkeep(1f), "paying it would leave the units short");
        Assert.AreEqual(8f, player.Resources);
        Assert.IsTrue(player.TryPayBuildingUpkeep(0f), "free buildings always run");
        Assert.IsFalse(UpkeepRules.BuildingDecays(3), "units go first");
        Assert.IsTrue(UpkeepRules.BuildingDecays(0));
    }

    [Test]
    public void AnUnpaidBombIsADud()
    {
        using var sim = Sim();
        int bomb = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Bomb, new int2(20, 20), complete: true);
        sim.Tick();
        sim.Em.SetComponentData(sim.BuildingEntity(bomb), new UpkeepComponent { ownerId = SimHarness.OwnerA, runningCostPerSecond = 1f, unpaid = true });
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(20.5f, 20.5f));
        sim.TickSeconds(1f);
        Assert.AreEqual(sim.Config.GetUnit(UnitType.Tank).Health, sim.UnitById(tank[0]).Health);
    }

    [Test]
    public void ABombGoesOffForAnEnemyNearItAndSparesItsOwnSide()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.GetUnit(UnitType.Heavy).Damage = 0); // only the blast hurts here
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Bomb, new int2(20, 20), complete: true);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(21.5f, 22f), UnitType.Heavy);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, new float2(21.5f, 20.5f), UnitType.Heavy); // a tile off the bomb
        sim.TickSeconds(0.5f);
        float damage = sim.Config.Siege.BlastDamage;
        Assert.AreEqual(400f - damage, sim.UnitById(enemy[0]).Health, 1e-3f, "the bomb went off beside it");
        Assert.AreEqual(400f - damage * sim.Config.Siege.BombFriendlyDamage, sim.UnitById(friend[0]).Health, 1e-3f);
    }

    [Test]
    public void ABombersBlastDoesATenthToFriends()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => { c.Simulation.TargetSearchSliceTicks = 1; c.GetUnit(UnitType.Heavy).Damage = 0; c.Bomber.FriendlyUnitDamage = 0.1f; }); // pinned: the balance may set it to 0
        int[] bomber = sim.Spawn(SimHarness.OwnerA, new float2(20, 20), UnitType.SuicideBomber);
        int[] friend = sim.Spawn(SimHarness.OwnerA, new float2(21, 20), UnitType.Heavy);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, new float2(20, 21), UnitType.Heavy);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Detonate, new int2(20, 20), bomber);
        sim.TickSeconds(0.25f);
        float blast = sim.Config.GetUnit(UnitType.SuicideBomber).BlastDamage;
        Assert.AreEqual(400f - blast * 0.1f, sim.UnitById(friend[0]).Health, 1e-2f);
        Assert.AreEqual(400f - blast, sim.UnitById(enemy[0]).Health, 1e-2f);
    }

    [Test]
    public void IdleUnitsAnswerFireFromOutOfTheirRange()
    {
        using var sim = Sim();
        int[] sniper = sim.Spawn(SimHarness.OwnerB, new float2(30, 20), UnitType.Sniper); // sees and reaches 20
        int[] shot = sim.Spawn(SimHarness.OwnerA, new float2(14, 20), UnitType.Heavy);    // range 5
        int[] nearby = sim.Spawn(SimHarness.OwnerA, new float2(10, 20), UnitType.Heavy);  // not shot, 4 tiles off the victim
        int[] holding = sim.Spawn(SimHarness.OwnerA, new float2(14, 24), UnitType.Heavy);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Hold, new int2(14, 24), holding);
        sim.TickSeconds(8f);
        Assert.Greater(sim.UnitById(shot[0]).Position.x, 15f, "the victim went after the shooter");
        Assert.Greater(sim.UnitById(nearby[0]).Position.x, 11f, "so did the unit standing by it");
        Assert.AreEqual(14f, sim.UnitById(holding[0]).Position.x, 1e-3f, "Hold stays");
        Assert.IsTrue(sim.Units().Any(u => u.Id == sniper[0]));
    }

    [Test]
    public void AnAlarmFromTheFogSendsUnitsOnlyAStepTowardsTheShooter()
    {
        var alarm = new DefendAlarm { Victim = new float2(10, 10), From = new float2(40, 10) };
        Assert.AreEqual(new int2(18, 10), DefendAlarms.Goal(alarm, false, 8f));
        Assert.AreEqual(new int2(40, 10), DefendAlarms.Goal(alarm, true, 8f));
    }

    [Test]
    public void AnInfiniteBalanceNeverMoves()
    {
        var player = new ServerPlayer(null, 100f);
        player.ResetResources(100f, infinite: true);
        Assert.AreEqual(ServerPlayer.InfiniteBalance, player.Resources);
        Assert.IsTrue(player.TrySpend(5000f));
        Assert.IsTrue(player.TryPayUpkeep(50f));
        Assert.IsTrue(player.TryPayBuildingUpkeep(50f));
        player.Add(10f);
        player.Refund(10f);
        Assert.AreEqual(ServerPlayer.InfiniteBalance, player.Resources);
        Assert.Greater(player.SpentThisMatch, 0f, "what it spends still counts on the end screen");

        player.ResetResources(100f);
        Assert.AreEqual(100f, player.Resources, "a rematch without it is an ordinary balance");
    }

    [Test]
    public void APingReachesItsSenderAndWhoeverItSharesVisionWith()
    {
        int[] everyone = { 1, 2, 3 };
        var to = new System.Collections.Generic.List<int>();
        PingRules.Recipients(1, everyone, (from, other) => other == 3, to);
        CollectionAssert.AreEquivalent(new[] { 1, 3 }, to);
    }

    [Test]
    public void PingsAreLimitedPerPlayer()
    {
        var cooldown = new PingRules.Cooldown(1f);
        Assert.IsTrue(cooldown.Allow(1, 10.0));
        Assert.IsFalse(cooldown.Allow(1, 10.5));
        Assert.IsTrue(cooldown.Allow(2, 10.5), "another player's ping isn't held up");
        Assert.IsTrue(cooldown.Allow(1, 11.0));
    }

    [Test]
    public void AShortMiddleClickPingsAndADragPans()
    {
        Assert.IsTrue(PingRules.IsClick(new UnityEngine.Vector2(10, 10), new UnityEngine.Vector2(13, 12), 0.2f));
        Assert.IsFalse(PingRules.IsClick(new UnityEngine.Vector2(10, 10), new UnityEngine.Vector2(30, 10), 0.2f), "it moved: a pan");
        Assert.IsFalse(PingRules.IsClick(new UnityEngine.Vector2(10, 10), new UnityEngine.Vector2(10, 10), 1.5f), "held: a pan that didn't move");
    }

    [Test]
    public void RichGemsAreGoldAtFarZoomLikeRedGems()
    {
        Assert.AreEqual(TileType.Gem, MapView.FlatType((int)TileType.Gem));
        Assert.AreEqual(TileType.RichGem, MapView.FlatType((int)TileType.RichGem), "it used to fall back to rock");
        Assert.AreEqual(TileType.Wall, MapView.FlatType((int)TileType.RichGem + 1));
    }

    [Test]
    public void TheSpectatorBarWritesNumbersLikeTheReplayViewer()
    {
        Assert.AreEqual("3,420 res · 410 units", WAR2D.UI.SpectatorController.Numbers(3420f, 410));
        Assert.AreEqual("Infinite · 5 units", WAR2D.UI.SpectatorController.Numbers(ServerPlayer.InfiniteBalance, 5));
    }

    [Test]
    public void ArtilleryAndSnipersSeeAsFarAsTheyShoot()
    {
        Config.ConfigLoader.ResetForTests();
        var c = Config.ConfigLoader.LoadConfig();
        Assert.GreaterOrEqual(c.GetUnit(UnitType.Artillery).Sight, c.GetUnit(UnitType.Artillery).Range);
        Assert.GreaterOrEqual(c.GetUnit(UnitType.Sniper).Sight, c.GetUnit(UnitType.Sniper).Range);
        Assert.GreaterOrEqual(c.GetBuilding(BuildingType.GunTurret).Sight, c.GetBuilding(BuildingType.GunTurret).Range);
    }
}
