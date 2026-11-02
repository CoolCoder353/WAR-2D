using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Idle Builders build and repair nearby on their own (v0.16).</summary>
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
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        int far = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Barracks, new int2(40, 40));
        sim.TickSeconds(10f);
        Assert.IsTrue(sim.IsBlueprint(far));
    }

    [Test]
    public void ItLeavesATeammatesBlueprint()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f), UnitType.Builder);
        sim.Spawn(SimHarness.OwnerB, new float2(40.5f, 40.5f), UnitType.Builder);
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
        using var sim = new SimHarness(SimHarness.OpenMap(48));
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
