using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>Terrain changes at the tick boundary: SetTile, refusals, paths and units on newly blocked tiles.</summary>
public class TerrainChangeTests
{
    private static void SetTile(SimHarness sim, int2 tile, TileType type) =>
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = tile, TileType = type });

    [Test]
    public void SetTileAppliesAtTheNextBoundary()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int2 tile = new int2(10, 10);
        int changes = 0;
        sim.Context.TerrainChanged += t => { if (t.Equals(tile)) changes++; };
        SetTile(sim, tile, TileType.Wall);
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(tile), "nothing changes before the boundary");
        sim.Tick();
        Assert.AreEqual(TileType.Wall, sim.Map.Grid.TileAt(tile));
        Assert.AreEqual(1, changes);
        CollectionAssert.Contains(sim.Map.ChangedSinceStart, sim.Map.Grid.Index(tile));
    }

    [Test]
    public void BorderTilesNeverChange()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        SetTile(sim, new int2(0, 5), TileType.Ground);
        SetTile(sim, new int2(5, 5), TileType.Border);
        sim.Tick();
        Assert.AreEqual(TileType.Border, sim.Map.Grid.TileAt(new int2(0, 5)));
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(5, 5)));
        Assert.IsEmpty(sim.Map.ChangedSinceStart);
    }

    [Test]
    public void AUnitOnANewlyBlockedTileIsPushedToFloor()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int[] id = sim.Spawn(SimHarness.OwnerA, new float2(10.5f, 10.5f));
        sim.Tick();
        SetTile(sim, new int2(10, 10), TileType.Wall);
        sim.Tick();
        sim.Tick(); // the push lands in the next gather
        float2 p = sim.UnitById(id[0]).Position;
        Assert.AreNotEqual(new int2(10, 10), (int2)math.floor(p), "moved off the new rock");
        Assert.IsTrue(sim.Map.Grid.IsWalkable((int2)math.floor(p)));
    }

    [Test]
    public void OpeningRockLetsAMoveOrderThrough()
    {
        // A wall column splits the map; a unit ordered across waits until a tile of it opens.
        var rows = new string[32];
        for (int r = 0; r < 32; r++)
            rows[r] = r == 0 || r == 31 ? new string('B', 32) : "B" + new string('.', 14) + "#" + new string('.', 15) + "B";
        using var sim = new SimHarness(MapStore.FromAscii(rows));
        int[] id = sim.Spawn(SimHarness.OwnerA, new float2(5.5f, 16.5f));
        sim.Tick();
        SetTile(sim, new int2(15, 16), TileType.Ground);
        sim.Tick();
        sim.Move(SimHarness.OwnerA, new int2(25, 16), id[0]);
        sim.TickSeconds(8f);
        Assert.Greater(sim.UnitById(id[0]).Position.x, 20f, "the opened tile is a route");
    }
}
