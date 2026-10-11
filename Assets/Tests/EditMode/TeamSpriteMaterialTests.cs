using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Client.Render;

/// <summary>The cached team-colour materials for building sprites.</summary>
public class TeamSpriteMaterialTests
{
    private string palette;

    [SetUp]
    public void SetUp() => palette = Palettes.Current;

    [TearDown]
    public void TearDown() => Palettes.Current = palette;

    [Test]
    public void SameColourAndGhostFlagShareAMaterial()
    {
        Color32 blue = Palettes.Player(Palettes.Standard, 0);
        Material a = TeamSpriteMaterial.ForColour(blue, false);
        Assert.AreSame(a, TeamSpriteMaterial.ForColour(blue, false));
        Assert.AreSame(TeamSpriteMaterial.For(12345, true), TeamSpriteMaterial.For(12345, true));
        Material ghost = TeamSpriteMaterial.ForColour(blue, true);
        Assert.AreNotSame(a, ghost);
        Assert.AreEqual(0f, a.GetFloat("_Ghost"));
        Assert.AreEqual(1f, ghost.GetFloat("_Ghost"));
        Assert.AreNotSame(a, TeamSpriteMaterial.ForColour(Palettes.Player(Palettes.Standard, 1), false));
    }

    [Test]
    public void MaterialsUseTheTeamShaderAndTheBuildingMask()
    {
        Material m = TeamSpriteMaterial.ForColour(Palettes.Player(Palettes.Standard, 2), false);
        Assert.AreEqual("WAR2D/TeamSprite", m.shader.name);
        ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Buildings);
        Assert.IsNotNull(atlas);
        Assert.AreSame(atlas.Mask, m.GetTexture("_MaskTex"));
        Assert.AreEqual(InstancedUnitRenderer.TeamGain(), m.GetFloat("_TeamGain"), 1e-5);
        Assert.AreSame(atlas.Texture, TeamSpriteMaterial.Sprite(BuildingType.Base).texture);
        Assert.AreEqual(4, atlas.Frames(nameof(BuildingType.Miner)), "the drill loop");
        Assert.AreEqual(4, atlas.Directions(nameof(BuildingType.Miner)), "one facing per 90°");
    }

    [Test]
    public void RotationPicksTheMinersFacingOnly()
    {
        Assert.AreEqual(0, TeamSpriteMaterial.Facing(BuildingType.Miner, 0f));
        Assert.AreEqual(1, TeamSpriteMaterial.Facing(BuildingType.Miner, 90f));
        Assert.AreEqual(3, TeamSpriteMaterial.Facing(BuildingType.Miner, 270f));
        Assert.AreEqual(0, TeamSpriteMaterial.Facing(BuildingType.Miner, 360f));
        Assert.AreEqual(0, TeamSpriteMaterial.Facing(BuildingType.Base, 90f), "one-facing buildings never turn");
        ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Buildings);
        Assert.AreEqual(atlas.Sprite(nameof(BuildingType.Miner), 2 * atlas.Frames(nameof(BuildingType.Miner))),
            TeamSpriteMaterial.FacingSprite(BuildingType.Miner, 2));
    }

    [Test]
    public void APaletteChangeGivesTheNewColoursMaterial()
    {
        Palettes.Current = Palettes.Standard;
        Material standard = TeamSpriteMaterial.ForColour(PlayerPalette.Of(7), false);
        Palettes.Current = Palettes.Colourblind;
        Material colourblind = TeamSpriteMaterial.ForColour(PlayerPalette.Of(7), false);
        Assert.AreNotSame(standard, colourblind);
        Assert.AreEqual(Palettes.Player(Palettes.Colourblind, 7), (Color32)colourblind.GetColor("_TeamColor"));
    }
}
