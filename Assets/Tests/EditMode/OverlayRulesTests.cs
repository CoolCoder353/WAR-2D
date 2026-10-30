using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client.Overlays;

/// <summary>The pure rules behind the v0.15 world overlays: health bar and tracer settings, hover delay, group order sizes, hull.</summary>
public class OverlayRulesTests
{
    [Test]
    public void HealthBarModesPickTheBars()
    {
        Assert.IsTrue(OverlayRules.ShowHealthBar((int)HealthBarMode.Always, 1f, false));
        Assert.IsFalse(OverlayRules.ShowHealthBar((int)HealthBarMode.DamagedOnly, 1f, true), "full health: no bar, as before");
        Assert.IsTrue(OverlayRules.ShowHealthBar((int)HealthBarMode.DamagedOnly, 0.5f, false));
        Assert.IsTrue(OverlayRules.ShowHealthBar((int)HealthBarMode.SelectedOnly, 1f, true));
        Assert.IsFalse(OverlayRules.ShowHealthBar((int)HealthBarMode.SelectedOnly, 0.2f, false));
    }

    [Test]
    public void TracerModesThinTheTracers()
    {
        int full = 0, reduced = 0, off = 0;
        for (int i = 0; i < 100; i++)
        {
            if (OverlayRules.KeepTracer((int)TracerMode.Full, i)) full++;
            if (OverlayRules.KeepTracer((int)TracerMode.Reduced, i)) reduced++;
            if (OverlayRules.KeepTracer((int)TracerMode.Off, i)) off++;
        }
        Assert.AreEqual(100, full);
        Assert.AreEqual(50, reduced);
        Assert.AreEqual(0, off);
    }

    [Test]
    public void TooltipDelayFollowsTheSetting()
    {
        Assert.IsFalse(OverlayRules.TooltipOpen((int)TooltipDelayMode.Fast, 0.2f));
        Assert.IsTrue(OverlayRules.TooltipOpen((int)TooltipDelayMode.Fast, 0.3f));
        Assert.IsFalse(OverlayRules.TooltipOpen((int)TooltipDelayMode.Normal, 0.5f));
        Assert.IsTrue(OverlayRules.TooltipOpen((int)TooltipDelayMode.Normal, 0.7f));
        Assert.IsFalse(OverlayRules.TooltipOpen((int)TooltipDelayMode.Off, 100f), "Off never opens");
    }

    [Test]
    public void GatherRadiusMatchesTheArrivalRadius()
    {
        Assert.AreEqual(0.9f, OverlayRules.GatherRadius(1), 1e-5f);
        Assert.AreEqual(0.5f + 0.4f * 4f, OverlayRules.GatherRadius(16), 1e-5f);
        Assert.AreEqual(0.9f, OverlayRules.GatherRadius(0), 1e-5f, "never below one unit's");
    }

    [Test]
    public void SpokesAreSampledEvenlyAndCapped()
    {
        Assert.AreEqual(5, OverlayRules.SampleCount(5, OverlayRules.MaxSpokes));
        Assert.AreEqual(OverlayRules.MaxSpokes, OverlayRules.SampleCount(1000, OverlayRules.MaxSpokes));
        var picked = new HashSet<int>();
        for (int k = 0; k < OverlayRules.MaxSpokes; k++) picked.Add(OverlayRules.SampleIndex(k, 1000, OverlayRules.MaxSpokes));
        Assert.AreEqual(OverlayRules.MaxSpokes, picked.Count, "no unit twice");
        Assert.IsTrue(picked.Contains(0) && picked.Contains(999), "first and last kept");
        Assert.AreEqual(3, OverlayRules.SampleIndex(3, 10, OverlayRules.MaxSpokes), "all drawn when they fit");
    }

    [Test]
    public void FadeRunsFromOneToZero()
    {
        Assert.AreEqual(1f, OverlayRules.Fade(0f, 0.6f), 1e-5f);
        Assert.AreEqual(0.5f, OverlayRules.Fade(0.3f, 0.6f), 1e-5f);
        Assert.AreEqual(0f, OverlayRules.Fade(1f, 0.6f), 1e-5f);
    }

    [Test]
    public void HullKeepsOnlyTheOutsidePoints()
    {
        var points = new List<float2> { new float2(0, 0), new float2(4, 0), new float2(4, 4), new float2(0, 4), new float2(2, 2), new float2(1, 3) };
        var hull = new List<float2>();
        OverlayRules.ConvexHull(points, hull);
        Assert.AreEqual(4, hull.Count);
        CollectionAssert.DoesNotContain(hull, new float2(2, 2));
        CollectionAssert.DoesNotContain(hull, new float2(1, 3));
    }

    [Test]
    public void HullOfFewPointsIsThosePoints()
    {
        var hull = new List<float2>();
        OverlayRules.ConvexHull(new List<float2> { new float2(1, 1) }, hull);
        Assert.AreEqual(1, hull.Count);
        OverlayRules.ConvexHull(new List<float2>(), hull);
        Assert.AreEqual(0, hull.Count);
    }

    [Test]
    public void ChordsStayInsideTheCircle()
    {
        float2 n = math.normalize(new float2(1, -1));
        Assert.IsTrue(OverlayRules.Chord(float2.zero, 2f, n, 0f, out float2 a, out float2 b));
        Assert.AreEqual(4f, math.distance(a, b), 1e-4f, "through the centre: the diameter");
        Assert.IsFalse(OverlayRules.Chord(float2.zero, 2f, n, 2.5f, out _, out _));
    }
}
