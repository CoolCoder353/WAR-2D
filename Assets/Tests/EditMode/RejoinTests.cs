using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Net.Rejoin;

/// <summary>v0.15 reconnect: the ticket book, the client's ticket, and what "away" freezes for a player.</summary>
public class RejoinTests
{
    private int made;

    private AwayRegistry Registry() => new AwayRegistry(() => "t" + made++);

    [Test]
    public void AnAwaySeatIsClaimedWithItsTokenInsideTheWindow()
    {
        AwayRegistry book = Registry();
        string token = book.Issue(7);
        Assert.IsTrue(book.MarkAway(7, now: 100.0, seconds: 60f));
        Assert.IsTrue(book.IsAway(7));
        Assert.AreEqual(160.0, book.UntilOf(7));
        Assert.IsTrue(book.TryClaim(token, 130.0, out int owner));
        Assert.AreEqual(7, owner);
        Assert.IsFalse(book.TryClaim(token, 161.0, out _), "too late");
        Assert.IsFalse(book.TryClaim("wrong", 130.0, out _));
        Assert.IsFalse(book.TryClaim(null, 130.0, out _));
    }

    [Test]
    public void ASeatThatIsNotAwayCannotBeClaimed()
    {
        AwayRegistry book = Registry();
        string token = book.Issue(7);
        Assert.IsFalse(book.TryClaim(token, 0.0, out _), "a player who is still connected cannot be taken over");
    }

    [Test]
    public void NoTicketOrNoWindowMeansNoAwaySeat()
    {
        AwayRegistry book = Registry();
        Assert.IsFalse(book.MarkAway(1, 0.0, 60f), "no ticket");
        book.Issue(1);
        Assert.IsFalse(book.MarkAway(1, 0.0, 0f), "ReconnectSeconds 0 turns rejoin off");
    }

    [Test]
    public void ReturningReplacesTheTokenSoTheOldOneStopsWorking()
    {
        AwayRegistry book = Registry();
        string old = book.Issue(3);
        book.MarkAway(3, 0.0, 60f);
        string fresh = book.Return(3);
        Assert.AreNotEqual(old, fresh);
        Assert.IsFalse(book.IsAway(3));
        Assert.IsFalse(book.TryClaim(old, 1.0, out _));
        book.MarkAway(3, 10.0, 60f);
        Assert.IsFalse(book.TryClaim(old, 11.0, out _), "the old token is dead");
        Assert.IsTrue(book.TryClaim(fresh, 11.0, out _));
    }

    [Test]
    public void WindowsThatRanOutAreListedOnce()
    {
        AwayRegistry book = Registry();
        book.Issue(1); book.Issue(2);
        book.MarkAway(1, 0.0, 10f);
        book.MarkAway(2, 0.0, 100f);
        var expired = new List<int>();
        book.CollectExpired(50.0, expired);
        CollectionAssert.AreEqual(new[] { 1 }, expired);
        book.Revoke(1);
        expired.Clear();
        book.CollectExpired(50.0, expired);
        Assert.IsEmpty(expired);
        Assert.AreEqual(1, book.AwayCount);
    }

    [Test]
    public void TheClientTicketCountsDownFromTheDropAndClearsWhenTheMatchEnds()
    {
        RejoinTicket.Clear();
        RejoinTicket.MarkDropped(0f);
        Assert.IsFalse(RejoinTicket.Available(1f), "no ticket, nothing to rejoin");
        RejoinTicket.Set("abc", 60f);
        RejoinTicket.MarkDropped(10f);
        Assert.AreEqual(40f, RejoinTicket.SecondsLeft(30f), 1e-3f);
        Assert.IsTrue(RejoinTicket.Available(30f));
        Assert.IsFalse(RejoinTicket.Available(71f));
        RejoinTicket.Clear();
        Assert.IsFalse(RejoinTicket.Available(11f));
        Assert.IsNull(RejoinTicket.Token);
    }

    [Test]
    public void AFreshTicketMeansTheClientIsBackInTheMatch()
    {
        RejoinTicket.Set("abc", 60f);
        RejoinTicket.MarkDropped(0f);
        RejoinTicket.Set("def", 60f);
        Assert.IsFalse(RejoinTicket.Dropped);
        RejoinTicket.Clear();
    }

    [Test]
    public void AnAwayPlayerEarnsAndPaysNothingSoNothingDecays()
    {
        var player = new ServerPlayer(null, 500f);
        player.Away = true;
        player.Add(100f);
        player.AddIncome(50f);
        Assert.AreEqual(500f, player.Resources, "nothing is earned while away");
        Assert.IsTrue(player.TryPayUpkeep(10_000f), "upkeep is frozen, never missed");
        Assert.AreEqual(500f, player.Resources, "and nothing is charged");
        player.Away = false;
        player.Add(100f);
        Assert.AreEqual(600f, player.Resources);
        Assert.IsFalse(player.TryPayUpkeep(10_000f), "back to normal");
    }

    [Test]
    public void NothingIsBoughtForAnAwayPlayerButRefundsStillLand()
    {
        var player = new ServerPlayer(null, 500f);
        Assert.IsTrue(player.TrySpend(100f));
        player.Away = true;
        Assert.IsFalse(player.TrySpend(10f), "a wall or building queued before the drop is not free");
        Assert.AreEqual(400f, player.Resources);
        player.Refund(100f);
        Assert.AreEqual(500f, player.Resources, "a refund is the player's own money and lands while away");
        Assert.AreEqual(0f, player.SpentThisMatch);
    }

    [Test]
    public void ATokenNamesItsOwnerWhetherOrNotTheyAreAway()
    {
        AwayRegistry book = Registry();
        string token = book.Issue(4);
        Assert.IsTrue(book.TryOwnerOf(token, out int owner), "a seat whose old connection is still open can be taken over");
        Assert.AreEqual(4, owner);
        Assert.IsFalse(book.TryOwnerOf("nope", out _));
        Assert.IsFalse(book.TryOwnerOf(null, out _));
        Assert.AreEqual(1, book.TicketCount);
    }

    [Test]
    public void ANewMatchOrAStoppedServerTearsUpEveryTicket()
    {
        AwayRegistry book = Registry();
        string a = book.Issue(1);
        book.Issue(2);
        book.MarkAway(1, 0.0, 60f);
        book.RevokeAll();
        Assert.AreEqual(0, book.TicketCount);
        Assert.AreEqual(0, book.AwayCount);
        Assert.IsFalse(book.TryOwnerOf(a, out _));
        Assert.IsFalse(book.TryClaim(a, 1.0, out _));
    }

    [Test]
    public void ARevokedTicketCannotBeMarkedAwaySoALeaverIsNotKept()
    {
        AwayRegistry book = Registry();
        book.Issue(9);
        book.Revoke(9); // LeaveMatch, a kick, or an elimination while away
        Assert.IsFalse(book.MarkAway(9, 0.0, 60f));
    }

    [Test]
    public void AConnectionLetInToRejoinIsClosedIfItNeverComesBack()
    {
        var grace = new RejoinGrace();
        grace.Track(1, 100.0);
        grace.Track(2, 110.0);
        var expired = new List<int>();
        grace.TakeExpired(100.0 + RejoinGrace.Seconds + 1.0, expired);
        CollectionAssert.AreEqual(new[] { 1 }, expired);
        Assert.AreEqual(1, grace.Count, "taken once");
        grace.Forget(2); // it got its player
        expired.Clear();
        grace.TakeExpired(1000.0, expired);
        Assert.IsEmpty(expired);
    }

    [Test]
    public void NoConnectionReadsAsKickedUntilTheGateKicksIt()
    {
        Assert.IsFalse(CommandGate.TakeKicked(12345), "nobody was kicked");
    }

    [Test]
    public void TheConfigGivesDroppedPlayersAMinute()
    {
        Assert.AreEqual(60f, Config.ConfigLoader.LoadConfig().Match.ReconnectSeconds);
    }
}
