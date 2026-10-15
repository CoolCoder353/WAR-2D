using NUnit.Framework;
using WAR2D.Art.Render;
using WAR2D.Client.Render;
using WAR2D.World;

/// <summary>Autotile masks and variants for the terrain index texture.</summary>
public class TileAutotileTests
{
    [Test]
    public void IsolatedWallHasNoNeighbours()
    {
        Assert.AreEqual(0, TileAutotile.Mask4((x, y) => x == 5 && y == 5, 5, 5));
    }

    [Test]
    public void NorthAndEastNeighboursSetThoseBits()
    {
        Assert.AreEqual(RenderGen.North | RenderGen.East, TileAutotile.Mask4((x, y) => (x == 5 && y == 6) || (x == 6 && y == 5), 5, 5));
        Assert.AreEqual(RenderGen.South | RenderGen.West, TileAutotile.Mask4((x, y) => (x == 5 && y == 4) || (x == 4 && y == 5), 5, 5));
    }

    [Test]
    public void TheMapEdgeCountsAsRock()
    {
        using MapStore map = MapStore.FromAscii(
            "#.",
            "..");
        MapGrid g = map.Grid;
        // FromAscii rows run top-down: '#' is at (0, 1), the top-left tile, open to the east and south.
        Assert.AreEqual((byte)TileType.Wall, g.Tiles[g.Index(new Unity.Mathematics.int2(0, 1))]);
        Assert.AreEqual(RenderGen.North | RenderGen.West, TileAutotile.Variant(g, 0, 1));
        Assert.IsTrue(TileAutotile.Solid(g, -1, 0));
        Assert.IsTrue(TileAutotile.Solid(g, 0, 2));
        Assert.IsFalse(TileAutotile.Solid(g, 1, 1));
    }

    [Test]
    public void VariantHashIsDeterministicAndInRange()
    {
        var seen = new bool[TileAutotile.RandomVariants];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            int h = TileAutotile.Hash(x, y, TileAutotile.RandomVariants);
            Assert.AreEqual(h, TileAutotile.Hash(x, y, TileAutotile.RandomVariants));
            Assert.That(h, Is.InRange(0, TileAutotile.RandomVariants - 1));
            seen[h] = true;
        }
        CollectionAssert.DoesNotContain(seen, false, "every variant gets used");
    }

    [Test]
    public void FlatTileColoursComeFromThePalette()
    {
        foreach (TileType t in System.Enum.GetValues(typeof(TileType)))
            CollectionAssert.Contains(WAR2D.Art.ArtStyle.Palette, MapView.TileColor(t), t.ToString());
        Assert.AreNotEqual(MapView.TileColor(TileType.Wall), MapView.TileColor(TileType.Gem));
    }

    [Test]
    public void IndexTexelsCarryTypeAndVariant()
    {
        using MapStore map = MapStore.FromAscii(
            "###",
            "#g.",
            "...");
        MapGrid g = map.Grid;
        UnityEngine.Color32 gem = MapView.IndexOf(g, 1, 1);
        Assert.AreEqual((byte)TileType.Gem, gem.r);
        // Rock to the north and west: mask N|W, in one of the 4 random variants.
        Assert.AreEqual(RenderGen.North | RenderGen.West, gem.g % WAR2D.Art.ArtManifest.AutotileVariants);
        Assert.Less(gem.g, 4 * WAR2D.Art.ArtManifest.AutotileVariants);
        Assert.AreEqual((byte)TileType.Ground, MapView.IndexOf(g, 2, 0).r);
    }

    [Test]
    public void PlayerWallsConnectToEachOtherAndBlueprintsAreFloor()
    {
        using MapStore map = MapStore.FromAscii(
            "...",
            "...",
            "...");
        MapGrid g = map.Grid;
        var tiles = g.Tiles;
        tiles[1 * 3 + 0] = (byte)TileType.PlayerWall;
        tiles[1 * 3 + 1] = (byte)TileType.PlayerWall;
        tiles[1 * 3 + 2] = (byte)TileType.WallBlueprint;
        Assert.AreEqual(RenderGen.East, TileAutotile.Variant(g, 0, 1), "a wall joins the wall beside it, not the map edge");
        Assert.AreEqual(RenderGen.West, TileAutotile.Variant(g, 1, 1), "a built wall joins only built walls");
        Assert.AreEqual(RenderGen.West, TileAutotile.Variant(g, 2, 1), "a blueprint's outline joins walls and blueprints");
        Assert.IsFalse(TileAutotile.Solid(g, 2, 1), "a wall blueprint is see-through floor: rock doesn't autotile against it");
        Assert.IsTrue(TileAutotile.Solid(g, 1, 1));
    }

    [Test]
    public void WallTexelsCarryOwnerColourAndDamage()
    {
        using MapStore map = MapStore.FromAscii("...");
        MapGrid g = map.Grid;
        g.Tiles[1] = (byte)TileType.PlayerWall;
        UnityEngine.Color32 healthy = MapView.IndexOf(g, 1, 0, i => (3, 90));
        UnityEngine.Color32 cracked = MapView.IndexOf(g, 1, 0, i => (3, 50));
        UnityEngine.Color32 broken = MapView.IndexOf(g, 1, 0, i => (3, 10));
        Assert.AreEqual(4, healthy.b, "palette index + 1");
        Assert.AreEqual(0, healthy.g / WAR2D.Art.ArtManifest.AutotileVariants);
        Assert.AreEqual(1, cracked.g / WAR2D.Art.ArtManifest.AutotileVariants);
        Assert.AreEqual(2, broken.g / WAR2D.Art.ArtManifest.AutotileVariants);
        Assert.AreEqual(0, MapView.IndexOf(g, 0, 0, i => (3, 90)).b, "ground has no owner");
    }

    [TestCase(100, 0)]
    [TestCase(66, 0)]
    [TestCase(65, 1)]
    [TestCase(33, 1)]
    [TestCase(32, 2)]
    [TestCase(0, 2)]
    public void WallDamageStateFollowsHealth(int percent, int state)
    {
        Assert.AreEqual(state, WallRules.DamageState(percent));
    }
}
