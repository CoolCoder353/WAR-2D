using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WAR2D.UI;

/// <summary>The HQ-placement minimap (v0.16, Figma P): a click near an option's ring snaps the camera to it.</summary>
public class MinimapStartsTests
{
    [Test]
    public void AClickInsideARingPicksThatOption()
    {
        var markers = new List<Vector2> { new Vector2(20, 20), new Vector2(100, 100) };
        Assert.AreEqual(1, MinimapController.SnapToStart(markers, new Vector2(104, 97), radius: 8f));
        Assert.AreEqual(-1, MinimapController.SnapToStart(markers, new Vector2(60, 60), radius: 8f));
    }

    [Test]
    public void TheNearestRingWinsWhenTwoOverlap()
    {
        var markers = new List<Vector2> { new Vector2(20, 20), new Vector2(26, 20) };
        Assert.AreEqual(1, MinimapController.SnapToStart(markers, new Vector2(25, 20), radius: 8f));
    }

    [Test]
    public void AnOwnRingIsAtLeastTheConfiguredSize()
    {
        Assert.AreEqual(12f, MinimapController.OwnRingPixels(8));
        Assert.AreEqual(16f, MinimapController.OwnRingPixels(16));
    }
}
