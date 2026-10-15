using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

/// <summary>Wall drags: 4-connected lines with no diagonal gaps.</summary>
public class WallRulesTests
{
    private static List<int2> Line(int2 a, int2 b)
    {
        var tiles = new List<int2>();
        WallRules.Line(a, b, tiles);
        return tiles;
    }

    [Test]
    public void StraightLinesAreEveryTileBetweenInclusive()
    {
        CollectionAssert.AreEqual(new[] { new int2(2, 5), new int2(3, 5), new int2(4, 5) }, Line(new int2(2, 5), new int2(4, 5)));
        CollectionAssert.AreEqual(new[] { new int2(1, 3), new int2(1, 2), new int2(1, 1) }, Line(new int2(1, 3), new int2(1, 1)));
        CollectionAssert.AreEqual(new[] { new int2(7, 7) }, Line(new int2(7, 7), new int2(7, 7)));
    }

    [Test]
    public void DiagonalsBecomeAStaircaseWithNoCornerGaps()
    {
        List<int2> tiles = Line(new int2(0, 0), new int2(3, 3));
        Assert.AreEqual(7, tiles.Count, "|dx| + |dy| + 1 tiles");
        for (int i = 1; i < tiles.Count; i++)
        {
            int2 step = math.abs(tiles[i] - tiles[i - 1]);
            Assert.AreEqual(1, step.x + step.y, $"tile {i} touches the previous one by an edge");
        }
        Assert.AreEqual(new int2(3, 3), tiles[tiles.Count - 1]);
    }

    [Test]
    public void AnyAngleStaysEdgeConnectedAndEndsOnTheTarget()
    {
        var rng = new System.Random(3);
        for (int n = 0; n < 200; n++)
        {
            int2 a = new int2(rng.Next(-20, 20), rng.Next(-20, 20)), b = new int2(rng.Next(-20, 20), rng.Next(-20, 20));
            List<int2> tiles = Line(a, b);
            Assert.AreEqual(a, tiles[0]);
            Assert.AreEqual(b, tiles[tiles.Count - 1]);
            int2 d = math.abs(b - a);
            Assert.AreEqual(d.x + d.y + 1, tiles.Count);
        }
    }

    [Test]
    public void OnlyFreeGroundTakesAWall()
    {
        System.Func<int2, TileType> tileAt = t => t.x == 0 ? TileType.Wall : t.x == 1 ? TileType.WallBlueprint : TileType.Ground;
        System.Func<int2, bool> used = t => t.x == 2;
        Assert.IsFalse(WallRules.CanPlace(new int2(0, 0), tileAt, used), "rock");
        Assert.IsFalse(WallRules.CanPlace(new int2(1, 0), tileAt, used), "already a wall blueprint");
        Assert.IsFalse(WallRules.CanPlace(new int2(2, 0), tileAt, used), "under a building");
        Assert.IsTrue(WallRules.CanPlace(new int2(3, 0), tileAt, used));
    }
}
