using NUnit.Framework;
using UnityEngine;
using WAR2D.UI;

/// <summary>The HQ placement prompt's text and the start option badges' placement (Figma: HUD / HQ placement, v0.11).</summary>
public class StartPromptTests
{
    [Test]
    public void TitleShowsTheTimerOnlyForATimedPick()
    {
        Assert.AreEqual("Pick your start · 0:38", HudOverlayController.Title(45, 37.2));
        Assert.AreEqual("Pick your start · 1:30", HudOverlayController.Title(90, 90));
        Assert.AreEqual("Pick your start · 0:00", HudOverlayController.Title(15, -3));
        Assert.AreEqual("Pick your start", HudOverlayController.Title(StartPickRules.Unlimited, 0));
        Assert.AreEqual("Pick your start", HudOverlayController.Title(0, 0));
    }

    [Test]
    public void BodyEndsWithTheRuleThatApplies()
    {
        StringAssert.EndsWith("When time runs out, your HQ goes to a random option.", HudOverlayController.Body(3, 45, false));
        StringAssert.EndsWith("The match waits until everyone has placed.", HudOverlayController.Body(3, StartPickRules.Unlimited, false));
        StringAssert.EndsWith("Your teammates' options are ringed in their colour.", HudOverlayController.Body(3, 45, true));
        StringAssert.Contains("one of your 3 start options", HudOverlayController.Body(3, 45, false));
        StringAssert.DoesNotContain("Option buttons", HudOverlayController.Body(1, 45, false));
    }

    [Test]
    public void ProgressCountsPlacedOrWaitingAndNamesTeammatesPicks()
    {
        Assert.AreEqual("2 of 4 players placed", HudOverlayController.Progress(4, 2, 45, null));
        Assert.AreEqual("Waiting for 2 players", HudOverlayController.Progress(4, 2, StartPickRules.Unlimited, null));
        Assert.AreEqual("Waiting for 1 player", HudOverlayController.Progress(4, 3, StartPickRules.Unlimited, null));
        Assert.AreEqual("3 of 4 players placed · Claude picked their start", HudOverlayController.Progress(4, 3, 45, new[] { "Claude" }));
    }

    [Test]
    public void EdgeBadgesSitOnTheEdgeTowardsTheOption()
    {
        var area = new Rect(0, 0, 1000, 600);
        var (right, toRight) = StartMarkersController.EdgePoint(area, new Vector2(3000, 300));
        Assert.AreEqual(0, toRight);
        Assert.AreEqual(1000 - 16, right.x, 1e-3);
        Assert.AreEqual(300, right.y, 1e-3);

        var (below, down) = StartMarkersController.EdgePoint(area, new Vector2(500, 5000)); // panel y grows downwards
        Assert.AreEqual(1, down);
        Assert.AreEqual(600 - 16, below.y, 1e-3);

        Assert.AreEqual(2, StartMarkersController.EdgePoint(area, new Vector2(-400, 250)).direction);
        Assert.AreEqual(3, StartMarkersController.EdgePoint(area, new Vector2(520, -900)).direction);

        var (corner, _) = StartMarkersController.EdgePoint(area, new Vector2(-5000, -5000));
        Assert.IsTrue(area.Contains(corner));
    }

    [Test]
    public void OnScreenBadgesStayInView()
    {
        var area = new Rect(0, 0, 1000, 600);
        Assert.AreEqual(new Vector2(500, 16), StartMarkersController.Inside(area, new Vector2(500, -200)));
        Assert.AreEqual(new Vector2(300, 200), StartMarkersController.Inside(area, new Vector2(300, 200)));
    }
}
