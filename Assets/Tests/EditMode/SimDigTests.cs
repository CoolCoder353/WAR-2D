using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>Diggers: tunnels, timing, gems, the border and player walls.</summary>
public class SimDigTests
{
    /// <summary>Open floor on the left (x 1..9), three rock tiles (x 10..12), open floor on the right.</summary>
    private static MapStore RockBand()
    {
        var rows = new string[16];
        for (int r = 0; r < 16; r++)
            rows[r] = r == 0 || r == 15 ? new string('B', 24) : "B" + new string('.', 9) + "###" + new string('.', 10) + "B";
        return MapStore.FromAscii(rows);
    }

    [Test]
    public void DigsAPermanentTunnelTileByTile()
    {
        using var sim = new SimHarness(RockBand());
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(8.5f, 8.5f), UnitType.Digger);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Dig, new int2(14, 8), digger[0]);
        float dig = sim.Config.Siege.DigTime;
        sim.TickSeconds(dig * 1.5f + 0.5f);
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(10, 8)), "first tile dug");
        Assert.AreEqual(TileType.Wall, sim.Map.Grid.TileAt(new int2(12, 8)), "not all at once");
        sim.TickSeconds(dig * 3f + 3f);
        for (int x = 10; x <= 12; x++) Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(x, 8)), $"x = {x}");
        Assert.AreEqual(WorkKinds.None, sim.UnitById(digger[0]).WorkKind, "done");
    }

    [Test]
    public void AGemPaysItsOwnerOnce()
    {
        var rows = new string[8];
        for (int r = 0; r < 8; r++) rows[r] = r == 0 || r == 7 ? new string('B', 12) : "B....g.....B";
        using var sim = new SimHarness(MapStore.FromAscii(rows));
        float paid = 0f;
        sim.Context.Income = (owner, amount) => { if (owner == SimHarness.OwnerA) paid += amount; };
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(3.5f, 4.5f), UnitType.Digger);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Dig, new int2(5, 4), digger[0]);
        sim.TickSeconds(sim.Config.Siege.DigTime + 2f);
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(5, 4)));
        Assert.AreEqual(sim.Config.Siege.GemPayout, paid);
    }

    [Test]
    public void TheBorderIsNeverDug()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(16));
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(2.5f, 8.5f), UnitType.Digger);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Dig, new int2(0, 8), digger[0]);
        sim.TickSeconds(5f);
        Assert.AreEqual(TileType.Border, sim.Map.Grid.TileAt(new int2(0, 8)));
    }

    [Test]
    public void APlayerWallIsDugByDamage()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(16));
        sim.PlaceWall(SimHarness.OwnerB, new int2(8, 8));
        sim.Tick();
        sim.ForceBuiltWall(new int2(8, 8));
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(6.5f, 8.5f), UnitType.Digger);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Dig, new int2(8, 8), digger[0]);
        sim.TickSeconds(10f); // 150 health, 4 damage x 6 per second
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(new int2(8, 8)));
    }

    [Test]
    public void AnAlliedWallIsNotBreached()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(16));
        sim.Context.TeamResolver = _ => 0; // everyone is allied
        sim.PlaceWall(SimHarness.OwnerB, new int2(8, 8));
        sim.Tick();
        sim.ForceBuiltWall(new int2(8, 8));
        int[] digger = sim.Spawn(SimHarness.OwnerA, new float2(6.5f, 8.5f), UnitType.Digger);
        sim.Tick();
        sim.Order(SimHarness.OwnerA, OrderKind.Dig, new int2(8, 8), digger[0]);
        sim.TickSeconds(10f);
        Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(new int2(8, 8)));
    }
}
