using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;
using WAR2D.Art.Style;

/// <summary>The lit sprite renderer and its pixel-art finish (the look the owner chose on 2026-10-11).</summary>
public class RenderGenTests
{
    private static Color32[] Finished(LitCanvas c, float lift = 0f) =>
        PixelFinish.Finish(c.Render(Color.blue, shadow: false, lift: lift), c.Width, c.Height, 1,
            PixelFinish.Palette(StylePalettes.Charcoal, Color.blue), StylePalettes.Charcoal.Colours[StylePalette.Outline]);

    [Test]
    public void SpritesAreDeterministic()
    {
        CollectionAssert.AreEqual(Finished(RenderGen.Tank(Mathf.PI / 4, 1)), Finished(RenderGen.Tank(Mathf.PI / 4, 1)));
        CollectionAssert.AreEqual(Finished(RenderGen.Miner(2), RenderGen.Lift), Finished(RenderGen.Miner(2), RenderGen.Lift));
        CollectionAssert.AreEqual(Finished(RenderGen.Rock(1, 6)), Finished(RenderGen.Rock(1, 6)));
    }

    [Test]
    public void BuildingCanvasesFitTheirFootprintsWithHeadroomAbove()
    {
        foreach (var (canvas, type) in new[] { (RenderGen.Base(), BuildingType.Base), (RenderGen.Spawner(0), BuildingType.Barracks), (RenderGen.Miner(0), BuildingType.Miner), (RenderGen.HeavyFactory(0), BuildingType.HeavyFactory), (RenderGen.SiegeWorkshop(0), BuildingType.SiegeWorkshop), (RenderGen.GunTurretBase(0), BuildingType.GunTurret), (RenderGen.Jammer(0), BuildingType.Jammer) })
        {
            Assert.IsTrue(ArtManifest.TryGet(type.ToString(), out ArtEntry entry));
            Assert.AreEqual(entry.WidthTiles * RenderGen.TileSize, canvas.Width, type.ToString());
            Assert.Greater(canvas.Height, entry.HeightTiles * RenderGen.TileSize, $"{type}: room above the footprint for the 3/4 view");
            Assert.AreEqual(entry.WidthTiles * RenderGen.TileSize / 2f, canvas.PivotY, $"{type}: anchored at its footprint's centre");
        }
    }

    [Test]
    public void FacingsAreRenderedNotRotated()
    {
        CollectionAssert.AreNotEqual(Finished(RenderGen.Tank(0, 0)), Finished(RenderGen.Tank(Mathf.PI, 0)));
    }

    [Test]
    public void FinishUsesOnlyPaletteColours()
    {
        Color32[] palette = PixelFinish.Palette(StylePalettes.Charcoal, Color.blue);
        foreach (Color32 c in Finished(RenderGen.Spawner(0), RenderGen.Lift))
            Assert.IsTrue(System.Array.IndexOf(palette, c) >= 0, c.ToString());
    }

    [Test]
    public void TerrainTilesAreOpaque()
    {
        LitCanvas floor = RenderGen.Floor(1, 0);
        foreach (Color32 c in floor.Render(Color.white, shadow: false)) Assert.AreEqual(255, c.a);
    }
}
