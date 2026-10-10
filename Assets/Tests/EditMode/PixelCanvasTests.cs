using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Generators;

/// <summary>The palette-indexed canvas, the PNG encoder and the prototype generators.</summary>
public class PixelCanvasTests
{
    private static readonly Color32[] Palette =
    {
        new Color32(0, 0, 0, 0), new Color32(10, 10, 10, 255), new Color32(200, 50, 50, 255), new Color32(50, 200, 50, 255),
    };

    [Test]
    public void OutOfBoundsWritesAreIgnored()
    {
        var c = new PixelCanvas(4, 4, Palette);
        Assert.DoesNotThrow(() => { c.Set(-1, 0, 2); c.Set(4, 0, 2); c.Set(0, 9, 2); c.Rect(-3, -3, 10, 10, 3); });
        Assert.AreEqual(0, c.Get(-1, 0), "reads outside are transparent");
        Assert.AreEqual(3, c.Get(3, 3));
    }

    [Test]
    public void OutlineOnlyWritesTransparentPixelsNextToOpaqueOnes()
    {
        var c = new PixelCanvas(5, 5, Palette);
        c.Set(2, 2, 2);
        c.Outline(1);
        Assert.AreEqual(2, c.Get(2, 2), "the opaque pixel is kept");
        foreach (var (x, y) in new[] { (1, 2), (3, 2), (2, 1), (2, 3) }) Assert.AreEqual(1, c.Get(x, y), $"{x},{y}");
        foreach (var (x, y) in new[] { (1, 1), (3, 3), (0, 0), (4, 2) }) Assert.AreEqual(0, c.Get(x, y), $"{x},{y} is not 4-adjacent");
    }

    [Test]
    public void ToPixelsOnlyReturnsPaletteColours()
    {
        var c = new PixelCanvas(8, 8, Palette);
        c.Disc(4, 4, 3, 2);
        c.Line(0, 0, 7, 5, 3);
        c.Dither(new RectInt(0, 0, 8, 2), 1, 3);
        foreach (Color32 p in c.ToPixels()) Assert.IsTrue(Palette.Contains(p), p.ToString());
    }

    [Test]
    public void PngStartsWithTheSignatureAndRoundTrips()
    {
        var c = new PixelCanvas(7, 5, Palette);
        c.Rect(1, 1, 3, 2, 2);
        c.Set(6, 4, 3);
        Color32[] pixels = c.ToPixels();
        byte[] png = Png.Encode(7, 5, pixels);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());

        var texture = new Texture2D(2, 2);
        try
        {
            Assert.IsTrue(texture.LoadImage(png));
            Assert.AreEqual((7, 5), (texture.width, texture.height));
            CollectionAssert.AreEqual(pixels, texture.GetPixels32());
        }
        finally { Object.DestroyImmediate(texture); }
    }

    [Test]
    public void GeneratorsAreDeterministicAndMatchTheManifest()
    {
        foreach (ISpriteGenerator g in PrototypeGenerators.All)
        {
            PixelCanvas[] a = g.Generate(7), b = g.Generate(7);
            Assert.IsTrue(ArtManifest.TryGet(g.Id, out ArtEntry entry), g.Id);
            Assert.AreEqual(entry.Frames * entry.Directions, a.Length, $"{g.Id}: one canvas per frame and direction");
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual((entry.WidthTiles * PrototypeGenerators.TileSize, entry.HeightTiles * PrototypeGenerators.TileSize), (a[i].Width, a[i].Height), g.Id);
                CollectionAssert.AreEqual(a[i].ToPixels(), b[i].ToPixels(), $"{g.Id} frame {i}");
            }
        }
    }

    [Test]
    public void UnitFacingsDiffer()
    {
        PixelCanvas[] tank = new TankGen().Generate(1);
        int frames = tank.Length / ArtManifest.UnitDirections;
        CollectionAssert.AreNotEqual(tank[0].ToPixels(), tank[2 * frames].ToPixels(), "east and north facings are drawn differently");
    }

    [Test]
    public void SheetsLayFramesOutInRows()
    {
        var a = new PixelCanvas(2, 2, Palette);
        var b = new PixelCanvas(2, 2, Palette);
        a.Set(0, 0, 2);
        b.Set(1, 1, 3);
        Color32[] sheet = PixelCanvas.Sheet(new[] { a, b }, 2, out int w, out int h);
        Assert.AreEqual((4, 2), (w, h));
        Assert.AreEqual(Palette[2], sheet[0]);
        Assert.AreEqual(Palette[3], sheet[1 * w + 3]);
    }
}
