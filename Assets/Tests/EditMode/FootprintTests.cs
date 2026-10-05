using NUnit.Framework;
using Unity.Mathematics;

public class FootprintTests
{
    [Test]
    public void OneByOne_CoversOnlyAnchor()
    {
        Assert.That(Footprint.Tiles(new int2(4, 7), new int2(1, 1)), Is.EquivalentTo(new[] { new int2(4, 7) }));
    }

    [Test]
    public void ThreeByThree_IsCentredOnAnchor()
    {
        var tiles = Footprint.Tiles(new int2(0, 0), new int2(3, 3));
        Assert.That(tiles.Count, Is.EqualTo(9));
        Assert.That(tiles, Has.Member(new int2(-1, -1)));
        Assert.That(tiles, Has.Member(new int2(1, 1)));
    }

    [Test]
    public void TwoByTwo_ExtendsDownLeftOfAnchor()
    {
        Assert.That(Footprint.Tiles(new int2(5, 5), new int2(2, 2)),
            Is.EquivalentTo(new[] { new int2(4, 4), new int2(5, 4), new int2(4, 5), new int2(5, 5) }));
    }

    [TestCase(1, 0.5f)]
    [TestCase(2, 0.0f)]
    [TestCase(3, 0.5f)]
    public void VisualCenter_IsMiddleOfFootprint(int size, float expectedOffset)
    {
        float2 c = Footprint.VisualCenter(new int2(10, 10), new int2(size, size));
        Assert.That(c.x, Is.EqualTo(10 + expectedOffset).Within(1e-5));
        Assert.That(c.y, Is.EqualTo(10 + expectedOffset).Within(1e-5));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void SnapAnchor_RoundTripsVisualCenter(int size)
    {
        int2 s = new int2(size, size);
        for (int x = -5; x <= 5; x++)
        {
            int2 anchor = new int2(x, -x);
            Assert.That(Footprint.SnapAnchor(Footprint.VisualCenter(anchor, s), s), Is.EqualTo(anchor));
        }
    }
}
