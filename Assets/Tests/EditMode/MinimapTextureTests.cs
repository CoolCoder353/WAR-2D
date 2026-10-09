using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client;
using WAR2D.Net.Replication;

public class MinimapTextureTests
{
    private static readonly Color32 Red = new Color32(255, 0, 0, 255);
    private MinimapTexture map;

    [SetUp]
    public void SetUp() => map = new MinimapTexture(4, 4, 2, _ => TileType.Ground);

    [TearDown]
    public void TearDown() => map.Dispose();

    private static byte[] Fog(FogState state)
    {
        var fog = new byte[16];
        for (int i = 0; i < fog.Length; i++) fog[i] = (byte)state;
        return fog;
    }

    [Test]
    public void UnexploredCellIsBlack()
    {
        map.Begin(Fog(FogState.Unexplored));
        map.PlotBuilding(new float2(1, 1), Red, ghost: false);
        map.End();
        Assert.That(map.Pixel(0, 0), Is.EqualTo(MinimapTexture.Unexplored));
    }

    [Test]
    public void UnitOnUnseenCellIsNotDrawn()
    {
        byte[] fog = Fog(FogState.Explored);
        map.Begin(fog);
        Color32 terrain = map.Pixel(1, 1);
        map.PlotUnit(new float2(3, 3), Red); // cell (1, 1): explored but not seen now
        map.PlotUnit(new float2(-5, 100), Red); // off the grid
        map.End();
        Assert.That(map.Pixel(1, 1), Is.EqualTo(terrain));
    }

    [Test]
    public void OwnUnitsAreDrawn()
    {
        byte[] fog = Fog(FogState.Explored);
        fog[1 * 4 + 2] = (byte)FogState.Visible; // cell (2, 1)
        map.Begin(fog);
        map.PlotUnit(new float2(4.5f, 2.5f), Red);
        map.End();
        Assert.That(map.Pixel(2, 1), Is.EqualTo(Red));
    }

    [Test]
    public void GhostBuildingsAreDimmed()
    {
        map.Begin(Fog(FogState.Explored));
        map.PlotBuilding(new float2(0, 0), Red, ghost: true);
        map.PlotBuilding(new float2(2, 0), Red, ghost: false);
        map.End();
        Assert.That(map.Pixel(0, 0).r, Is.LessThan(Red.r));
        Assert.That(map.Pixel(1, 0), Is.EqualTo(Red));
    }

    [Test]
    public void VisibleTerrainIsBrighterThanExplored()
    {
        byte[] fog = Fog(FogState.Explored);
        fog[0] = (byte)FogState.Visible;
        map.Begin(fog);
        map.End();
        Assert.That(map.Pixel(0, 0).r, Is.GreaterThan(map.Pixel(1, 0).r));
    }

    [Test]
    public void LocalAndWorldRoundTrip()
    {
        var size = new Vector2(244, 122);
        var world = new float2(1024, 512);
        float2 p = MinimapTexture.ToWorld(new Vector2(61, 30.5f), size, world);
        Assert.That(p.x, Is.EqualTo(256f).Within(1e-3f));
        Assert.That(p.y, Is.EqualTo(384f).Within(1e-3f), "the image's top is the map's top");
        Vector2 back = MinimapTexture.ToLocal(p, size, world);
        Assert.That(back.x, Is.EqualTo(61f).Within(1e-3f));
        Assert.That(back.y, Is.EqualTo(30.5f).Within(1e-3f));
    }
}
