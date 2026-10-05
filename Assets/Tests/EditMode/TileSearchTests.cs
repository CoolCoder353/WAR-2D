using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

public class TileSearchTests
{
    [Test]
    public void ReturnsOriginWhenFree()
    {
        Assert.That(TileSearch.FindNearest(int2.zero, p => true, p => true, 100, out int2 t), Is.True);
        Assert.That(t, Is.EqualTo(int2.zero));
    }

    [Test]
    public void FindsNearestFreeTileOutsideBlockedSquare()
    {
        var blocked = new HashSet<int2>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                blocked.Add(new int2(x, y));

        Assert.That(TileSearch.FindNearest(int2.zero, p => !blocked.Contains(p), p => true, 1000, out int2 t), Is.True);
        Assert.That(math.max(math.abs(t.x), math.abs(t.y)), Is.EqualTo(2));
    }

    [Test]
    public void GivesUpAfterMaxVisited()
    {
        Assert.That(TileSearch.FindNearest(int2.zero, p => false, p => true, 50, out _), Is.False);
    }

    [Test]
    public void DoesNotCrossUntraversableTiles()
    {
        // Free tile at (5,0) but a vertical wall at x=2 that can't be crossed and is closed off by maxVisited.
        Assert.That(TileSearch.FindNearest(int2.zero, p => p.Equals(new int2(5, 0)), p => p.x < 2, 200, out _), Is.False);
    }
}
