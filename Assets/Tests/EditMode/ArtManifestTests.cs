using System;
using System.Linq;
using NUnit.Framework;
using WAR2D.Art;

/// <summary>The art manifest covers every unit, building and tile type, and the v0.7 kit.</summary>
public class ArtManifestTests
{
    [Test]
    public void EveryUnitTypeHasArt()
    {
        foreach (UnitType t in Enum.GetValues(typeof(UnitType)))
            if (t != UnitType.None) Assert.IsTrue(ArtManifest.TryGet(t.ToString(), out ArtEntry e) && e.Kind == ArtKind.Unit, t.ToString());
    }

    [Test]
    public void EveryBuildingTypeHasArt()
    {
        foreach (BuildingType t in Enum.GetValues(typeof(BuildingType)))
            if (t != BuildingType.None) Assert.IsTrue(ArtManifest.TryGet(t.ToString(), out ArtEntry e) && e.Kind == ArtKind.Building, t.ToString());
    }

    [Test]
    public void EveryTileTypeHasArt()
    {
        foreach (TileType t in Enum.GetValues(typeof(TileType)))
            Assert.IsTrue(ArtManifest.TryGet(t.ToString(), out ArtEntry e) && e.Kind == ArtKind.Tile, t.ToString());
    }

    [TestCase("Builder"), TestCase("Digger"), TestCase("Bomb"), TestCase("WallBlueprint"), TestCase("PlayerWall")]
    public void V07KitIsPresent(string id) => Assert.IsTrue(ArtManifest.TryGet(id, out _), id);

    [Test]
    public void IdsAreUnique() => Assert.AreEqual(ArtManifest.All.Count, ArtManifest.All.Select(e => e.Id).Distinct().Count());

    [Test]
    public void BuildingFootprintsMatchTheConfig()
    {
        Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
        foreach (var pair in config.Buildings)
        {
            Assert.IsTrue(ArtManifest.TryGet(pair.Key.ToString(), out ArtEntry e), pair.Key.ToString());
            Assert.AreEqual((pair.Value.Width, pair.Value.Height), (e.WidthTiles, e.HeightTiles), pair.Key.ToString());
        }
    }

    [Test]
    public void EveryEntryHasAtLeastOneFrameAndDirection()
    {
        foreach (ArtEntry e in ArtManifest.All)
        {
            Assert.That(e.Frames, Is.GreaterThanOrEqualTo(1), e.Id);
            Assert.That(e.Directions, Is.GreaterThanOrEqualTo(1), e.Id);
            Assert.That(e.WidthTiles, Is.GreaterThanOrEqualTo(1), e.Id);
            Assert.That(e.HeightTiles, Is.GreaterThanOrEqualTo(1), e.Id);
        }
    }

    [Test]
    public void TryGetMissesUnknownIds() => Assert.IsFalse(ArtManifest.TryGet("NoSuchSprite", out _));
}
