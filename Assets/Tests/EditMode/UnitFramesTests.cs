using System;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Client.Render;

/// <summary>Direction and frame selection for units drawn from the Units atlas.</summary>
public class UnitFramesTests
{
    [Test]
    public void DirectionRoundsToTheNearestFacingAndWraps()
    {
        Assert.AreEqual(0, UnitFrames.Direction(0f, 8));
        Assert.AreEqual(1, UnitFrames.Direction(Mathf.PI / 4f, 8));
        Assert.AreEqual(2, UnitFrames.Direction(Mathf.PI / 2f + 0.3f, 8));
        Assert.AreEqual(0, UnitFrames.Direction(2f * Mathf.PI, 8));
        Assert.AreEqual(0, UnitFrames.Direction(2f * Mathf.PI - 0.1f, 8));
        Assert.AreEqual(7, UnitFrames.Direction(-Mathf.PI / 4f, 8));
        Assert.AreEqual(4, UnitFrames.Direction(-Mathf.PI, 8));
        Assert.AreEqual(4, UnitFrames.Direction(Mathf.PI, 8));
    }

    [Test]
    public void FramesStayInsideEveryTypesCells()
    {
        ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Units);
        Assert.IsNotNull(atlas);
        var table = new UnitFrames.Table(atlas);
        foreach (UnitType t in Enum.GetValues(typeof(UnitType)))
        {
            if (t == UnitType.None) continue;
            int first = atlas.FirstFrame(t.ToString()), count = atlas.FrameCount(t.ToString());
            Assert.GreaterOrEqual(first, 0, $"{t} is in the atlas");
            for (float rot = -7f; rot < 7f; rot += 0.37f)
            for (float time = 0f; time < 1f; time += 0.05f)
            foreach (bool moving in new[] { false, true })
            foreach (float since in new[] { 0f, 0.15f, 1f })
            {
                uint slow = UnitFrames.Frame(atlas, t, rot, moving, time, since, 3);
                Assert.That(slow, Is.InRange((uint)first, (uint)(first + count - 1)), $"{t} {rot} {time} {moving} {since}");
                Assert.AreEqual(slow, table.Cell((int)t, rot, moving, time, since, 3));
            }
        }
        // A type with no art (None, or one not built yet) draws as the Tank.
        Assert.AreEqual(atlas.FirstFrame(nameof(UnitType.Tank)), (int)table.Cell((int)UnitType.None, 0f, false, 0f, 9f, 0));
    }

    [Test]
    public void StillUnitsHoldFrameZeroAndMovingOnesAlternate()
    {
        ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Units);
        int first = atlas.FirstFrame(nameof(UnitType.Tank));
        const int frames = 4;
        float east = 0f, north = Mathf.PI / 2f;
        Assert.AreEqual(first, UnitFrames.Frame(atlas, UnitType.Tank, east, false, 0.37f));
        Assert.AreEqual(first + 2 * frames, UnitFrames.Frame(atlas, UnitType.Tank, north, false, 5.2f));
        Assert.AreEqual(first + 2 * frames, UnitFrames.Frame(atlas, UnitType.Tank, north, true, 0.0f));
        Assert.AreEqual(first + 2 * frames + 1, UnitFrames.Frame(atlas, UnitType.Tank, north, true, 0.1f));
        Assert.AreEqual(first + 2, UnitFrames.Frame(atlas, UnitType.Tank, east, true, 0.1f, sinceAction: 0.02f), "firing beats moving");
        Assert.AreEqual(first + 3, UnitFrames.Frame(atlas, UnitType.Tank, east, false, 0f, sinceAction: 0.15f));
        Assert.AreEqual(first, UnitFrames.Frame(atlas, UnitType.Tank, east, false, 0f, sinceAction: 0.5f), "the action has ended");
    }
}
