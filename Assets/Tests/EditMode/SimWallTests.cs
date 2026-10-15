using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Walls: blueprints, construction waits for an empty tile, blocking, combat and destruction.</summary>
public class SimWallTests
{
    [Test]
    public void ABlueprintIsWalkableAndABuiltWallBlocks()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.PlaceWall(SimHarness.OwnerA, new int2(10, 10));
        sim.Tick();
        Assert.AreEqual(TileType.WallBlueprint, sim.Map.Grid.TileAt(new int2(10, 10)));
        Assert.IsTrue(sim.Map.Grid.IsWalkable(new int2(10, 10)));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(9.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(10, 10), builder[0]);
        sim.TickSeconds(10f); // 150 health at 25/s
        Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(new int2(10, 10)));
        Assert.AreEqual(WallStates.Built, sim.Context.Data.WallState[sim.Map.Grid.Index(new int2(10, 10))]);
        Assert.IsFalse(sim.Map.Grid.IsWalkable(new int2(10, 10)));
    }

    [Test]
    public void ConstructionWaitsWhileAUnitStandsOnTheTile()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.PlaceWall(SimHarness.OwnerA, new int2(10, 10));
        int[] sitter = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        int[] builder = sim.Spawn(SimHarness.OwnerA, new float2(9.5f, 10.5f), UnitType.Builder);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Hold, new int2(10, 10), sitter[0]);
        sim.Order(SimHarness.OwnerA, OrderKind.Build, new int2(10, 10), builder[0]);
        sim.TickSeconds(3f);
        Assert.AreEqual(TileType.WallBlueprint, sim.Map.Grid.TileAt(new int2(10, 10)), "nobody is walled in");
        sim.Move(SimHarness.OwnerA, new int2(20, 20), sitter[0]);
        sim.TickSeconds(10f);
        Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(new int2(10, 10)));
    }

    [Test]
    public void EnemiesOnAttackMoveBreakAWallAndItBecomesGround()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int tile = sim.Map.Grid.Index(new int2(15, 15));
        sim.PlaceWall(SimHarness.OwnerA, new int2(15, 15));
        sim.Tick();
        sim.ForceBuiltWall(new int2(15, 15));
        int[] tank = sim.Spawn(SimHarness.OwnerB, new float2(12.5f, 15.5f));
        sim.Tick();
        sim.Order(SimHarness.OwnerB, OrderKind.AttackMove, new int2(15, 15), tank[0]);
        sim.TickSeconds(40f); // 150 health, 10 damage x 0.5 per second
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(15, 15)));
        Assert.AreEqual(WallStates.None, sim.Context.Data.WallState[tile]);
    }

    [Test]
    public void OwnWallsAreNeverTargets()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        sim.PlaceWall(SimHarness.OwnerA, new int2(15, 15));
        sim.Tick();
        sim.ForceBuiltWall(new int2(15, 15));
        int[] tank = sim.Spawn(SimHarness.OwnerA, new float2(13.5f, 15.5f));
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(15, 15), tank[0]);
        sim.TickSeconds(5f);
        sim.Em.CompleteAllTrackedJobs();
        Assert.AreEqual(sim.Config.Siege.WallHealth, sim.Context.Data.WallHealth[sim.Map.Grid.Index(new int2(15, 15))], 1e-3f);
    }

    [Test]
    public void ADestroyedWallOpensTheRoute()
    {
        // A rock column at x = 15 splits the map; its tile at (15, 16) becomes a player wall.
        var rows = new string[32];
        for (int r = 0; r < 32; r++)
            rows[r] = r == 0 || r == 31 ? new string('B', 32) : "B" + new string('.', 14) + "#" + new string('.', 15) + "B";
        using var sim = new SimHarness(WAR2D.World.MapStore.FromAscii(rows));
        int2 gap = new int2(15, 16);
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = gap, TileType = TileType.Ground });
        sim.PlaceWall(SimHarness.OwnerA, gap);
        sim.Tick();
        sim.ForceBuiltWall(gap);
        int[] id = sim.Spawn(SimHarness.OwnerB, new float2(5.5f, 16.5f));
        sim.Tick();
        sim.Move(SimHarness.OwnerB, new int2(25, 16), id[0]);
        sim.TickSeconds(4f);
        Assert.Less(sim.UnitById(id[0]).Position.x, 15f, "the wall holds");
        sim.Em.CompleteAllTrackedJobs();
        sim.Context.Data.WallDamage.Enqueue(new TileWork { Tile = sim.Map.Grid.Index(gap), Amount = 1e6f });
        sim.Tick();
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(gap));
        sim.Move(SimHarness.OwnerB, new int2(25, 16), id[0]);
        sim.TickSeconds(8f);
        Assert.Greater(sim.UnitById(id[0]).Position.x, 20f, "the broken wall is a route");
    }

    [Test]
    public void WallsOnlyGoOnTilesTheOwnerSees()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(64));
        sim.Context.WallsNeedSight = true;
        sim.Spawn(SimHarness.OwnerA, new float2(8.5f, 8.5f));
        for (int i = 0; i < 6; i++) sim.Tick();
        sim.PlaceWall(SimHarness.OwnerA, new int2(10, 8));  // in sight
        sim.PlaceWall(SimHarness.OwnerA, new int2(50, 50)); // far out in the fog
        sim.Tick();
        Assert.AreEqual(TileType.WallBlueprint, sim.Map.Grid.TileAt(new int2(10, 8)));
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(50, 50)));
    }
}
