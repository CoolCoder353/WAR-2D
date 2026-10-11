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
}
