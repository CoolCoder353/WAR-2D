using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

public class VisibilityRulesTests
{
    [Test]
    public void InBox_IncludesEdges_AndAcceptsAnyCornerOrder()
    {
        Assert.That(VisibilityRules.InBox(new float2(0, 0), new int2(0, 0), new int2(10, 10)), Is.True);
        Assert.That(VisibilityRules.InBox(new float2(10, 10), new int2(10, 10), new int2(0, 0)), Is.True);
        Assert.That(VisibilityRules.InBox(new float2(5, 11), new int2(0, 0), new int2(10, 10)), Is.False);
    }

    [Test]
    public void Filter_KeepsOnlyPointsInside()
    {
        var points = new List<float2> { new float2(1, 1), new float2(50, 50), new float2(-1, 3) };
        var kept = VisibilityRules.Filter(points, new int2(0, 0), new int2(10, 10));
        Assert.That(kept.Count, Is.EqualTo(1));
        Assert.That(kept[0].x, Is.EqualTo(1f));
    }
}
