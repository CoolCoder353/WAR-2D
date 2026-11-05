using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>An Attack-move fires at the closest unit or building in range, then at a wall, unless the player named a target.</summary>
public class SimClosestTargetTests
{
    /// <summary>Nothing dies, so each test reads the choice itself. Tank range 5, sight 8.</summary>
    private static SimHarness Sim() => new SimHarness(SimHarness.OpenMap(64), c =>
    {
        c.Simulation.TargetSearchSliceTicks = 1;
        c.GetUnit(UnitType.Tank).Health = 1000000;
        c.GetBuilding(BuildingType.Barracks).Health = 1000000;
    });

    private static float2 At => new float2(20.5f, 20.5f);

    /// <summary>The tank goes on an Attack-move past what the test placed, and stands to fight within a second.</summary>
    private static Unit AttackMoved(SimHarness sim, int[] tank)
    {
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(50, 20), tank[0]);
        sim.TickSeconds(1f);
        return sim.UnitById(tank[0]);
    }

    [Test]
    public void ABuildingCloserThanAUnitTakesTheFire()
    {
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        sim.Spawn(SimHarness.OwnerB, At + new float2(4f, 0f));
        int building = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(20, 23), true);
        Unit u = AttackMoved(sim, tank);
        Assert.AreEqual(building, u.TargetId);
        Assert.AreEqual(TargetKinds.Building, u.TargetKind);
    }

    [Test]
    public void AUnitAsNearAsABuildingKeepsTheFire()
    {
        // Exactly 1.5 tiles each: the Barracks' footprint (19..20, 22..23) starts 1.5 above the tank, the unit 1.5 below.
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, At + new float2(0f, -1.5f));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(20, 23), true);
        Unit u = AttackMoved(sim, tank);
        Assert.AreEqual(TargetKinds.Unit, u.TargetKind, "a tie goes to the unit, as when units are searched alone");
        Assert.AreEqual(enemy[0], u.TargetId);
    }

    [Test]
    public void AUnitInRangeTakesTheFireFromACloserWall()
    {
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, At + new float2(0f, -4f));
        int2 tile = new int2(22, 20);
        sim.PlaceWall(SimHarness.OwnerB, tile);
        sim.Tick();
        sim.ForceBuiltWall(tile);
        Unit u = AttackMoved(sim, tank);
        Assert.AreEqual(TargetKinds.Unit, u.TargetKind, "a wall is a last resort");
        Assert.AreEqual(enemy[0], u.TargetId);
    }

    [Test]
    public void AWallInRangeTakesTheFireWithNothingElse()
    {
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        int2 tile = new int2(22, 20);
        sim.PlaceWall(SimHarness.OwnerB, tile);
        sim.Tick();
        sim.ForceBuiltWall(tile);
        Unit u = AttackMoved(sim, tank);
        Assert.AreEqual(TargetKinds.Wall, u.TargetKind);
        Assert.AreEqual(sim.Map.Grid.Index(tile), u.TargetId);
    }

    [Test]
    public void AWallInsideTheMinimumRangeIsNotATarget()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64), c =>
        {
            c.Simulation.TargetSearchSliceTicks = 1;
            c.GetUnit(UnitType.Artillery).Health = 1000000;
        });
        int[] artillery = sim.Spawn(SimHarness.OwnerA, At, UnitType.Artillery);
        int2 tile = new int2(22, 20); // 2 tiles off: inside Artillery's minimum range of 4
        sim.PlaceWall(SimHarness.OwnerB, tile);
        sim.Tick();
        sim.ForceBuiltWall(tile);
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(50, 20), artillery[0]);
        sim.Tick();
        Unit u = sim.UnitById(artillery[0]);
        Assert.IsFalse(u.TargetKind == TargetKinds.Wall && u.TargetId == sim.Map.Grid.Index(tile), "inside the minimum range");
    }

    [Test]
    public void AUnitCloserThanABuildingTakesTheFire()
    {
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, At + new float2(1.5f, 0f));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(20, 24), true);
        Unit u = AttackMoved(sim, tank);
        Assert.AreEqual(enemy[0], u.TargetId);
        Assert.AreEqual(TargetKinds.Unit, u.TargetKind);
    }

    [Test]
    public void AnIdleUnitStillTakesUnitsFirst()
    {
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        int[] enemy = sim.Spawn(SimHarness.OwnerB, At + new float2(4f, 0f));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(20, 23), true);
        sim.TickSeconds(1f);
        Assert.AreEqual(enemy[0], sim.UnitById(tank[0]).TargetId);
    }

    [Test]
    public void ATargetThePlayerNamedComesBeforeACloserOne()
    {
        using var sim = Sim();
        int[] tank = sim.Spawn(SimHarness.OwnerA, At);
        int[] named = sim.Spawn(SimHarness.OwnerB, At + new float2(4f, 0f));
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, new int2(20, 23), true);
        sim.Tick();
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.Attack, OwnerId = SimHarness.OwnerA, TargetId = named[0], Tile = new int2(24, 20), Ids = tank });
        sim.TickSeconds(1f);
        Assert.AreEqual(named[0], sim.UnitById(tank[0]).TargetId);
    }
}
