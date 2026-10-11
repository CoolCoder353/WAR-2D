using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;

/// <summary>The shelf packer behind the generated atlases.</summary>
public class AtlasPackerTests
{
    private static List<(string id, int frame, int w, int h)> Rects(int seed, int count)
    {
        var rng = new System.Random(seed);
        var rects = new List<(string, int, int, int)>();
        for (int i = 0; i < count; i++) rects.Add(("r", i, 1 + rng.Next(120), 1 + rng.Next(160)));
        return rects;
    }

    [TestCase(1, 1), TestCase(2, 40), TestCase(3, 300)]
    public void RectsNeverOverlapAndStayInBounds(int seed, int count)
    {
        var rects = Rects(seed, count);
        const int padding = 2;
        PackResult p = AtlasPacker.Pack(rects, padding);
        Assert.IsTrue(Mathf.IsPowerOfTwo(p.Width) && Mathf.IsPowerOfTwo(p.Height), $"{p.Width}×{p.Height}");
        for (int i = 0; i < rects.Count; i++)
        {
            var a = new RectInt(p.Positions[i], new Vector2Int(rects[i].w, rects[i].h));
            Assert.IsTrue(a.xMin >= padding && a.yMin >= padding && a.xMax <= p.Width - padding && a.yMax <= p.Height - padding, $"rect {i} {a}");
            for (int j = i + 1; j < rects.Count; j++)
            {
                var b = new RectInt(p.Positions[j], new Vector2Int(rects[j].w, rects[j].h));
                var grown = new RectInt(a.x - padding, a.y - padding, a.width + 2 * padding, a.height + 2 * padding);
                Assert.IsFalse(grown.Overlaps(b), $"{i} {a} and {j} {b} are closer than the padding");
            }
        }
    }

    [Test]
    public void PackingIsDeterministic()
    {
        var rects = Rects(9, 100);
        PackResult a = AtlasPacker.Pack(rects), b = AtlasPacker.Pack(rects);
        Assert.AreEqual((a.Width, a.Height), (b.Width, b.Height));
        CollectionAssert.AreEqual(a.Positions, b.Positions);
    }

    [Test]
    public void SixtyFourEqualCellsFitTheSmallestSquare()
    {
        var rects = new List<(string, int, int, int)>();
        for (int i = 0; i < 64; i++) rects.Add(("c", i, 14, 14));
        PackResult p = AtlasPacker.Pack(rects, 1);
        Assert.AreEqual((128, 128), (p.Width, p.Height));
    }
}
