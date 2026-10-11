using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;

/// <summary>The style guide's values: the palette file, player-colour clearance and the team key.</summary>
public class ArtStyleTests
{
    private const string PalettePath = "Assets/Art/palette.png";

    [Test]
    public void PaletteFileMatchesTheCode()
    {
        Assert.IsTrue(File.Exists(PalettePath), PalettePath);
        var texture = new Texture2D(2, 2);
        try
        {
            Assert.IsTrue(texture.LoadImage(File.ReadAllBytes(PalettePath)));
            Assert.AreEqual((ArtStyle.Palette.Length, 1), (texture.width, texture.height));
            CollectionAssert.AreEqual(ArtStyle.Palette, texture.GetPixels32());
        }
        finally { Object.DestroyImmediate(texture); }
    }

    [Test]
    public void NoPaletteColourIsCloseToAPlayerColour()
    {
        foreach (Color32 player in ArtStyle.PlayerColours())
        foreach (Color32 c in ArtStyle.Palette.Where(c => c.a > 0))
            Assert.GreaterOrEqual(ArtStyle.DeltaE(c, player), ArtStyle.PlayerClearance, $"{c} vs player {player}");
    }

    [Test]
    public void TeamKeyIsOutsideThePaletteAndDistinct()
    {
        Assert.AreEqual(ArtStyle.TeamKeyRamp.Length, ArtStyle.TeamKeyRamp.Distinct().Count());
        foreach (Color32 k in ArtStyle.TeamKeyRamp) CollectionAssert.DoesNotContain(ArtStyle.Palette, k);
    }

    [Test]
    public void PaletteHasNoDuplicatesAndStartsTransparent()
    {
        Assert.AreEqual(0, ArtStyle.Palette[0].a);
        Assert.AreEqual(ArtStyle.Palette.Length, ArtStyle.Palette.Distinct().Count());
        Assert.AreEqual(ArtStyle.TileSize, ArtStyle.PixelsPerUnit);
    }
}
