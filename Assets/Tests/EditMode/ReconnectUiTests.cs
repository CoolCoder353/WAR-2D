using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Net.Rejoin;
using WAR2D.UI;

/// <summary>v0.15 item 19: the reconnect banners' model, the countdown texts and the client ticket's clock.</summary>
public class ReconnectUiTests
{
    private static AwaySighting Seen(int owner, bool away, float left = 0f) =>
        new AwaySighting { OwnerId = owner, Name = "Claude", ColourIndex = 1, IsAway = away, SecondsLeft = left };

    [Test]
    public void TheClockRoundsUpAndShowsMinutesAndSeconds()
    {
        Assert.AreEqual("0:42", ReconnectText.Clock(41.2f));
        Assert.AreEqual("1:00", ReconnectText.Clock(60f));
        Assert.AreEqual("0:01", ReconnectText.Clock(0.1f));
        Assert.AreEqual("0:00", ReconnectText.Clock(0f));
        Assert.AreEqual("0:00", ReconnectText.Clock(-3f));
        Assert.AreEqual("0:00", ReconnectText.Clock(float.NaN));
    }

    [Test]
    public void TheDangerStateIsTheLastTenSeconds()
    {
        Assert.IsFalse(ReconnectText.IsDanger(10f), "0:10 is not yet danger");
        Assert.IsFalse(ReconnectText.IsDanger(9.5f), "shows 0:10");
        Assert.IsTrue(ReconnectText.IsDanger(9f));
        Assert.IsTrue(ReconnectText.IsDanger(0f));
    }

    [Test]
    public void TheBarDrainsWithTheWindow()
    {
        Assert.AreEqual(1f, ReconnectText.Fraction(60f, 60f), 1e-5f);
        Assert.AreEqual(0.5f, ReconnectText.Fraction(30f, 60f), 1e-5f);
        Assert.AreEqual(0f, ReconnectText.Fraction(-1f, 60f), 1e-5f);
        Assert.AreEqual(1f, ReconnectText.Fraction(90f, 60f), 1e-5f);
        Assert.AreEqual(0f, ReconnectText.Fraction(30f, 0f), 1e-5f);
    }

    [Test]
    public void TheTextsMatchTheFrames()
    {
        Assert.AreEqual("Claude disconnected", ReconnectText.Title(ReconnectBannerKind.Away, "Claude"));
        Assert.AreEqual("· 0:42 to return", ReconnectText.Tail(ReconnectBannerKind.Away, 42f));
        Assert.AreEqual("Claude returned", ReconnectText.Title(ReconnectBannerKind.Returned, "Claude"));
        Assert.IsNull(ReconnectText.Tail(ReconnectBannerKind.Returned, 0f));
        Assert.AreEqual("Claude did not return", ReconnectText.Title(ReconnectBannerKind.DidNotReturn, "Claude"));
        Assert.AreEqual("· eliminated", ReconnectText.Tail(ReconnectBannerKind.DidNotReturn, 0f));
        Assert.AreEqual("Oscar’s match", ReconnectText.MatchOf("Oscar"));
        Assert.AreEqual("Your match", ReconnectText.MatchOf(null));
        Assert.AreEqual("· 0:08 left to rejoin", ReconnectText.RejoinLeft(7.4f));
        Assert.AreEqual("Your match is still running", ReconnectText.RejoinTitle(false, "Oscar"));
        Assert.AreEqual("Rejoining Oscar’s match…", ReconnectText.RejoinTitle(true, "Oscar"));
    }

    [Test]
    public void ADropShowsACountdownBannerAndOneDisconnectedEvent()
    {
        var model = new ReconnectModel();
        int disconnected = 0;
        model.Disconnected += (_, _) => disconnected++;
        model.Update(new[] { Seen(7, false) }, 0f);
        model.Update(new[] { Seen(7, true, 60f) }, 1f);
        model.Update(new[] { Seen(7, true, 59f) }, 2f);
        Assert.AreEqual(1, disconnected);
        Assert.AreEqual(1, model.Banners.Count);
        Assert.AreEqual(ReconnectBannerKind.Away, model.Banners[0].Kind);
        Assert.AreEqual(59f, model.Banners[0].SecondsLeft, 1e-5f);
    }

    [Test]
    public void APlayerAlreadyAwayWhenTheHudOpensGetsABannerButNoAlert()
    {
        var model = new ReconnectModel();
        int disconnected = 0;
        model.Disconnected += (_, _) => disconnected++;
        model.Update(new[] { Seen(7, true, 30f) }, 0f);
        Assert.AreEqual(0, disconnected);
        Assert.AreEqual(1, model.Banners.Count);
    }

    [Test]
    public void ComingBackShowsReturnedForFourSeconds()
    {
        var model = new ReconnectModel();
        int returned = 0;
        model.Returned += (_, _) => returned++;
        model.Update(new[] { Seen(7, false) }, 0f);
        model.Update(new[] { Seen(7, true, 60f) }, 1f);
        model.Update(new[] { Seen(7, false) }, 10f);
        Assert.AreEqual(1, returned);
        Assert.AreEqual(ReconnectBannerKind.Returned, model.Banners[0].Kind);
        model.Update(new[] { Seen(7, false) }, 13.9f);
        Assert.AreEqual(1, model.Banners.Count);
        model.Update(new[] { Seen(7, false) }, 14f);
        Assert.AreEqual(0, model.Banners.Count);
    }

    [Test]
    public void VanishingWhileAwayShowsDidNotReturnAndLeavingNormallyShowsLeft()
    {
        var model = new ReconnectModel();
        int timedOut = 0, left = 0;
        model.TimedOut += (_, _) => timedOut++;
        model.Left += (_, _) => left++;
        model.Update(new[] { Seen(7, false), Seen(8, false) }, 0f);
        model.Update(new[] { Seen(7, true, 1f), Seen(8, false) }, 1f);
        model.Update(new List<AwaySighting>(), 2f);
        Assert.AreEqual(1, timedOut);
        Assert.AreEqual(1, left, "v0.15.1: a player who leaves on purpose no longer keeps a seat, so they are gone at once");
        Assert.AreEqual(2, model.Banners.Count);
        Assert.AreEqual(7, model.Banners[0].OwnerId);
        Assert.AreEqual(ReconnectBannerKind.DidNotReturn, model.Banners[0].Kind);
        Assert.AreEqual(8, model.Banners[1].OwnerId);
        Assert.AreEqual(ReconnectBannerKind.Left, model.Banners[1].Kind);
        model.Update(new List<AwaySighting>(), 6f);
        Assert.AreEqual(0, model.Banners.Count);
    }

    [Test]
    public void TheLeftBannerSaysThePlayerLeftWithNoCountdown()
    {
        Assert.AreEqual("Claude left the match", ReconnectText.Title(ReconnectBannerKind.Left, "Claude"));
        Assert.IsNull(ReconnectText.Tail(ReconnectBannerKind.Left, 0f));
        Assert.AreEqual("Their units and buildings are removed.", ReconnectText.Detail(ReconnectBannerKind.Left));
    }

    [Test]
    public void ARefusedRejoinKillsTheTicketAndKeepsTheReasonUntilDismissed()
    {
        RejoinTicket.Set("abc", 60f);
        RejoinTicket.MarkDropped(0f);
        RejoinTicket.Refuse(WAR2D.Net.Rejoin.RejoinRefusal.Eliminated);
        Assert.IsFalse(RejoinTicket.Available(1f), "Rejoin is no longer offered");
        Assert.IsNull(RejoinTicket.Token);
        Assert.AreEqual(WAR2D.Net.Rejoin.RejoinRefusal.Eliminated, RejoinTicket.Refusal);
        Assert.AreEqual("· you were eliminated while away", ReconnectText.RefusalTail(RejoinTicket.Refusal.Value));
        RejoinTicket.DismissRefusal();
        Assert.IsNull(RejoinTicket.Refusal);
        RejoinTicket.Refuse(WAR2D.Net.Rejoin.RejoinRefusal.TimedOut);
        RejoinTicket.Set("def", 60f);
        Assert.IsNull(RejoinTicket.Refusal, "a new match's ticket clears an old refusal");
        RejoinTicket.Clear();
    }

    [Test]
    public void EveryRefusalHasItsOwnReason()
    {
        var tails = new HashSet<string>();
        foreach (WAR2D.Net.Rejoin.RejoinRefusal reason in System.Enum.GetValues(typeof(WAR2D.Net.Rejoin.RejoinRefusal)))
            Assert.IsTrue(tails.Add(ReconnectText.RefusalTail(reason)), reason.ToString());
    }

    [Test]
    public void AFailedRejoinAttemptDoesNotRestartTheWindow()
    {
        RejoinTicket.Set("abc", 60f);
        RejoinTicket.MarkDropped(0f);
        RejoinTicket.MarkDropped(20f); // the failed attempt's disconnect
        Assert.AreEqual(30f, RejoinTicket.SecondsLeft(30f), 1e-3f);
        RejoinTicket.Clear();
    }
}
