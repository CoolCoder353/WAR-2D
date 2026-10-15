using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.World;

/// <summary>Which tile a Digger digs next, and where it stands to dig it.</summary>
public class DigRulesTests
{
    private static DigStep Next(MapStore map, int2 from, int2 target, out int2 dig, out int2 stand) =>
        DigRules.Next(map.Grid.Tiles, map.Grid.Width, map.Grid.Height, from, target, out dig, out stand);

    [Test]
    public void DigsTheFirstSolidTileAndStandsBeforeIt()
    {
        using var map = MapStore.FromAscii("BBBBBBBB", "B..##..B", "BBBBBBBB");
        Assert.AreEqual(DigStep.Dig, Next(map, new int2(1, 1), new int2(6, 1), out int2 dig, out int2 stand));
        Assert.AreEqual(new int2(3, 1), dig);
        Assert.AreEqual(new int2(2, 1), stand);
    }

    [Test]
    public void AnOpenLineIsDone()
    {
        using var map = MapStore.FromAscii("BBBBBB", "B....B", "BBBBBB");
        Assert.AreEqual(DigStep.Done, Next(map, new int2(1, 1), new int2(4, 1), out _, out _));
    }

    [Test]
    public void TheBorderBlocksDigging()
    {
        using var map = MapStore.FromAscii("BBBBB", "B...B", "BBBBB");
        Assert.AreEqual(DigStep.Blocked, Next(map, new int2(1, 1), new int2(4, 1), out _, out _));
    }

    [Test]
    public void GemsAreDug()
    {
        using var map = MapStore.FromAscii("BBBBBB", "B.g..B", "BBBBBB");
        Assert.AreEqual(DigStep.Dig, Next(map, new int2(1, 1), new int2(4, 1), out int2 dig, out _));
        Assert.AreEqual(new int2(2, 1), dig);
    }

    [Test]
    public void Line4MatchesTheWallLine()
    {
        var rng = new System.Random(5);
        for (int n = 0; n < 100; n++)
        {
            int2 a = new int2(rng.Next(-30, 30), rng.Next(-30, 30)), b = new int2(rng.Next(-30, 30), rng.Next(-30, 30));
            var wall = new System.Collections.Generic.List<int2>();
            WallRules.Line(a, b, wall);
            var line = new Line4(a, b);
            int i = 0;
            while (line.Next(out int2 p)) Assert.AreEqual(wall[i++], p);
            Assert.AreEqual(wall.Count, i);
        }
    }

    [Test]
    public void MinersOnlyEarnFromGems()
    {
        Assert.IsTrue(MinerRules.Produces(TileType.Gem));
        Assert.IsFalse(MinerRules.Produces(TileType.Ground), "a dug gem stops paying");
    }
}
