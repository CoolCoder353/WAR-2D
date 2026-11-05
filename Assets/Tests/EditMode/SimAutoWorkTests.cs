using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Idle Builders build and repair within <c>AutoWork/Radius</c> on their own (v0.16).</summary>
public class SimAutoWorkTests
{
    [Test]
    public void AnIdleBuilderFinishesANearbyBlueprint()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int barracks = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(16, 10));
        sim.TickSeconds(60f);
        Assert.IsFalse(sim.IsBlueprint(barracks), "built without an order");
    }

    [Test]
    public void ItLeavesBlueprintsBeyondTheRadius()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.AutoWork.Radius = 8f);
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int far = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(40, 40));
        sim.TickSeconds(10f);
        Assert.IsTrue(sim.IsBlueprint(far));
    }

    [Test]
    public void AnIdleBuilderGoesAcrossTheMapToABlueprintWithinTheRadius()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.AutoWork.Radius = 900f);
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int far = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(40, 40));
        sim.TickSeconds(90f);
        Assert.IsFalse(sim.IsBlueprint(far), "built without an order");
    }

    [Test]
    public void ItLeavesWallBlueprintsBeyondTheRadius()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.AutoWork.Radius = 8f);
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick(); // owner A exists from here, so it can own walls
        var far = new int2(50, 50);
        sim.PlaceWall(SimHarness.OwnerA, far);
        sim.TickSeconds(20f);
        Assert.AreEqual(WallStates.Blueprint, sim.Context.Data.WallState[sim.Map.Grid.Index(far)]);
    }

    [Test]
    public void AnIdleBuilderTakesTheNearestBlueprintFirst()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(10, 55));
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(35, 10));
        sim.TickSeconds(3f);
        Assert.Greater(sim.UnitById(builder[0]).Position.x, 15f, "heading east to the nearer one");
    }

    [Test]
    public void AnIdleBuilderTakesARepairCloserThanABlueprint()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int near = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 10), complete: true);
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(10, 55));
        sim.Tick();
        sim.SetBuildingHealthShare(near, 0.5f);
        sim.TickSeconds(3f);
        Assert.Less(sim.UnitById(builder[0]).Position.y, 14f, "heading east to the closer job, not north");
    }

    [Test]
    public void AnIdleBuilderTakesABlueprintCloserThanARepair()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int far = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(10, 55), complete: true);
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 10));
        sim.Tick();
        sim.SetBuildingHealthShare(far, 0.5f);
        sim.TickSeconds(3f);
        Assert.Less(sim.UnitById(builder[0]).Position.y, 14f, "heading east to the closer job, not north");
    }

    [Test]
    public void ItRepairsADamagedWall()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        var wall = new int2(16, 10);
        sim.PlaceWall(SimHarness.OwnerA, wall);
        sim.Tick();
        sim.ForceBuiltWall(wall);
        int index = sim.Map.Grid.Index(wall);
        sim.Context.Data.WallDamage.Enqueue(new TileWork { Tile = index, Amount = sim.Context.Data.WallMaxHealth * 0.5f });
        sim.TickSeconds(30f);
        Assert.Greater(sim.Context.Data.WallHealth[index], sim.Context.Data.WallMaxHealth * 0.85f);
    }

    /// <summary>64x64 where the nearest free tile to a building at (20, 20), (19, 19), is a one-tile hole in rock.</summary>
    private static WAR2D.World.MapStore HoleBesideTheJob()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                bool border = x == 0 || y == 0 || x == 63 || y == 63;
                bool ring = x >= 18 && x <= 20 && y >= 18 && y <= 20 && !(x == 19 && y == 19) && !(x == 20 && y == 20);
                row[x] = border ? 'B' : ring ? '#' : '.';
            }
            rows[r] = new string(row);
        }
        return WAR2D.World.MapStore.FromAscii(rows);
    }

    [Test]
    public void ABuilderStandsWhereItCanWalkToNotInAHoleBesideTheJob()
    {
        using var sim = new SimHarness(HoleBesideTheJob());
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(30.5f, 30.5f), UnitType.Builder);
        sim.Tick();
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(20, 20));
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(20, 20), builder[0]);
        sim.TickSeconds(60f);
        Assert.IsFalse(sim.IsBlueprint(turret), $"the Builder ended at {sim.UnitById(builder[0]).Position}");
    }

    [Test]
    public void AWalledOffBuilderDoesNotCostTheRestOfItsGroupTheOrder()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                bool border = x == 0 || y == 0 || x == 63 || y == 63;
                bool room = (x == 4 || x == 10) && y >= 4 && y <= 10 || (y == 4 || y == 10) && x >= 4 && x <= 10;
                row[x] = border ? 'B' : room ? '#' : '.';
            }
            rows[r] = new string(row);
        }
        using var sim = new SimHarness(WAR2D.World.MapStore.FromAscii(rows));
        int[] shut = sim.Spawn(SimHarness.OwnerA, new float2(7.5f, 7.5f), UnitType.Builder);
        int[] free = sim.Spawn(SimHarness.OwnerA, new float2(30.5f, 30.5f), UnitType.Builder);
        sim.Tick();
        int job = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(40, 40));
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(40, 40), shut[0], free[0]);
        sim.TickSeconds(40f);
        Assert.IsFalse(sim.IsBlueprint(job), $"the free Builder ended at {sim.UnitById(free[0]).Position}");
    }

    /// <summary>64x64 with a walled room, 16 to 24 each way, that nothing outside can reach.</summary>
    private static WAR2D.World.MapStore SealedRoom()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
        {
            int y = 63 - r;
            var row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                bool border = x == 0 || y == 0 || x == 63 || y == 63;
                bool room = (x == 16 || x == 24) && y >= 16 && y <= 24 || (y == 16 || y == 24) && x >= 16 && x <= 24;
                row[x] = border ? 'B' : room ? '#' : '.';
            }
            rows[r] = new string(row);
        }
        return WAR2D.World.MapStore.FromAscii(rows);
    }

    [Test]
    public void AnIdleBuilderPassesOverARepairItCantReach()
    {
        using var sim = new SimHarness(SealedRoom());
        sim.Spawn(SimHarness.OwnerA, new float2(30.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        int shut = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 20), complete: true);
        int open = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(50, 50));
        sim.Tick();
        sim.SetBuildingHealthShare(shut, 0.5f);
        sim.TickSeconds(90f);
        Assert.IsFalse(sim.IsBlueprint(open), "the one it could reach");
    }

    [Test]
    public void AnIdleBuilderPassesOverABlueprintItCantReach()
    {
        using var sim = new SimHarness(SealedRoom());
        sim.Spawn(SimHarness.OwnerA, new float2(30.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        int shut = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 20));
        int open = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(50, 50));
        sim.TickSeconds(90f);
        Assert.IsFalse(sim.IsBlueprint(open), "the one it could reach");
        Assert.IsTrue(sim.IsBlueprint(shut));
    }

    [Test]
    public void ABuilderLeftOutOfReachDropsTheJobAndComesBackToIt()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(18.5f, 20.5f), UnitType.Builder);
        sim.Tick();
        int job = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(20, 20));
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(20, 20), builder[0]);
        sim.TickSeconds(2f);
        sim.Teleport(builder[0], new float2(40.5f, 40.5f)); // as a crowd can push it, once its order is over
        sim.TickSeconds(90f);
        Assert.IsFalse(sim.IsBlueprint(job), $"the Builder ended at {sim.UnitById(builder[0]).Position}");
    }

    [Test]
    public void ItLeavesRepairsBeyondTheRadius()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c => c.AutoWork.Radius = 8f);
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int far = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(40, 40), complete: true);
        sim.Tick();
        sim.SetBuildingHealthShare(far, 0.5f);
        sim.TickSeconds(20f);
        Assert.AreEqual(0.5f, sim.BuildingHealthShare(far), 0.01f);
    }

    [Test]
    public void ItLeavesATeammatesBlueprint()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Spawn(SimHarness.OwnerB, new float2(40.5f, 40.5f));
        sim.Tick();
        int theirs = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(16, 10));
        sim.TickSeconds(20f);
        Assert.IsTrue(sim.IsBlueprint(theirs));
    }

    [Test]
    public void ItNeverTakesABuilderOffAnOrder()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(14, 10));
        sim.Move(SimHarness.OwnerA, new int2(10, 50), builder[0]);
        sim.TickSeconds(4f);
        Assert.Greater(sim.UnitById(builder[0]).Position.y, 15f, "still walking to its goal");
    }

    [Test]
    public void ItRepairsAnOwnBuildingBelowTheShareAndNotAnEnemys()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48), c => c.AutoWork.Radius = 8f); // the enemy's own Builder stays out of it
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 13.5f), UnitType.Builder);
        sim.Spawn(SimHarness.OwnerB, new float2(40.5f, 40.5f), UnitType.Builder);
        sim.Tick();
        int own = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(16, 10), complete: true);
        int enemy = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(16, 17), complete: true);
        sim.Tick();
        sim.SetBuildingHealthShare(own, 0.5f);
        sim.SetBuildingHealthShare(enemy, 0.5f);
        sim.TickSeconds(40f);
        Assert.Greater(sim.BuildingHealthShare(own), 0.85f);
        Assert.AreEqual(0.5f, sim.BuildingHealthShare(enemy), 0.01f);
    }

    [Test]
    public void ABuilderGoesOnPastAQueuedJobThatIsGone()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int2 first = new int2(14, 10), gone = new int2(20, 10), last = new int2(26, 10);
        sim.PlaceWall(SimHarness.OwnerA, first);
        sim.PlaceWall(SimHarness.OwnerA, gone);
        sim.PlaceWall(SimHarness.OwnerA, last);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Build, first, builder[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Build, true, gone, builder[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Build, true, last, builder[0]);
        sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        sim.Context.Data.WallDamage.Enqueue(new TileWork { Tile = sim.Map.Grid.Index(gone), Amount = 1f }); // a shot blueprint goes at once
        sim.TickSeconds(40f);
        Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(last));
    }

    [Test]
    public void ALongWallDragGetsBuiltToTheEnd()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        int[] a = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 18.5f), UnitType.Builder);
        int[] b = sim.Spawn(SimHarness.OwnerA, new float2(11.5f, 18.5f), UnitType.Builder);
        sim.Tick(); // owner A exists from here, so it can own walls
        for (int x = 10; x < 70; x++) sim.PlaceWall(SimHarness.OwnerA, new int2(x, 20));
        sim.Tick();
        int queued = sim.Config.Orders.MaxQueued + 1; // what the drag sends Build orders for
        for (int x = 10; x < 10 + queued; x++) sim.Order(SimHarness.OwnerA, OrderKind.Build, x > 10, new int2(x, 20), a[0], b[0]);
        sim.TickSeconds(900f);
        for (int x = 10; x < 70; x++) Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(new int2(x, 20)), $"tile {x}");
    }
}
