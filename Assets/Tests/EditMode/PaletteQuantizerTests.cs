using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;

/// <summary>Cleaning AI-tool output down to the art palette and size.</summary>
public class PaletteQuantizerTests
{
    private static readonly Color32 Clear = new Color32(0, 0, 0, 0);
    private static readonly Color32 Red = new Color32(200, 40, 40, 255), Blue = new Color32(40, 60, 200, 255), Grey = new Color32(120, 120, 120, 255);
    private static readonly Color32[] Palette = { Clear, Red, Blue, Grey };

    private static Color32[] Fill(int n, Color32 c) => Enumerable.Repeat(c, n).ToArray();

    [Test]
    public void OutputHoldsOnlyPaletteColours()
    {
        var src = new Color32[64 * 64];
        var rng = new System.Random(3);
        for (int i = 0; i < src.Length; i++) src[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        foreach (Color32 c in PaletteQuantizer.Quantize(src, 64, 64, 16, 16, Palette)) Assert.IsTrue(Palette.Contains(c), c.ToString());
    }

    [Test]
    public void SolidColourMapsToItsNearestPaletteColour()
    {
        Color32[] dst = PaletteQuantizer.Quantize(Fill(16, new Color32(190, 70, 60, 255)), 4, 4, 2, 2, Palette);
        Assert.IsTrue(dst.All(c => c.Equals(Red)));
    }

    [Test]
    public void DownscalePicksTheMajority()
    {
        var src = Fill(16, Blue);
        for (int i = 0; i < 10; i++) src[i * 3 % 16] = Red; // 10 red, 6 blue, spread through the block
        Assert.AreEqual(10, src.Count(c => c.Equals(Red)));
        Assert.AreEqual(Red, PaletteQuantizer.Quantize(src, 4, 4, 1, 1, Palette)[0]);
    }

    [Test]
    public void AlphaBelowTheCutoffIsTransparent()
    {
        var faint = new Color32(200, 40, 40, 100);
        Assert.AreEqual(Clear, PaletteQuantizer.Quantize(Fill(4, faint), 2, 2, 1, 1, Palette)[0]);
        Assert.AreEqual(Red, PaletteQuantizer.Quantize(Fill(4, faint), 2, 2, 1, 1, Palette, alphaCutoff: 64)[0]);
    }

    [Test]
    public void OpaqueColoursNeverMapToTheTransparentEntry()
    {
        Color32[] dst = PaletteQuantizer.Quantize(Fill(4, new Color32(0, 0, 0, 255)), 2, 2, 1, 1, Palette);
        Assert.AreNotEqual(Clear, dst[0], "black is opaque, so it maps to the nearest opaque colour");
    }
}
