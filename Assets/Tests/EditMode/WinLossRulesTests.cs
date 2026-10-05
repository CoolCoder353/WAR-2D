using NUnit.Framework;

public class WinLossRulesTests
{
    private static PlayerStatus P(int id, bool hq, bool elim = false) => new PlayerStatus(id, hq, elim);

    [Test]
    public void TwoPlayersWithHQ_Continue()
    {
        var o = WinLossRules.Evaluate(new[] { P(1, true), P(2, true) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
        Assert.That(o.NewlyEliminated, Is.Empty);
    }

    [Test]
    public void OneHQLeft_InMultiplayerMatch_Wins_AndOthersEliminated()
    {
        var o = WinLossRules.Evaluate(new[] { P(1, true), P(2, false) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
        Assert.That(o.WinnerId, Is.EqualTo(1));
        Assert.That(o.NewlyEliminated, Is.EquivalentTo(new[] { 2 }));
    }

    [Test]
    public void LastPlayer_AfterOpponentLeft_Wins()
    {
        // Opponent disconnected and was removed; match started with 2.
        var o = WinLossRules.Evaluate(new[] { P(1, true) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
    }

    [Test]
    public void SoloMatch_NeverWins()
    {
        Assert.That(WinLossRules.Evaluate(new[] { P(1, true) }, 1).Kind, Is.EqualTo(OutcomeKind.Continue));
    }

    [Test]
    public void NoHQsLeft_IsDraw()
    {
        var o = WinLossRules.Evaluate(new[] { P(1, false), P(2, false) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Draw));
    }

    [Test]
    public void AlreadyEliminated_IsNotReEliminated()
    {
        var o = WinLossRules.Evaluate(new[] { P(1, true), P(2, true), P(3, false, elim: true) }, 3);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
        Assert.That(o.NewlyEliminated, Is.Empty);
    }

    [Test]
    public void NoPlayers_Continue()
    {
        Assert.That(WinLossRules.Evaluate(new PlayerStatus[0], 2).Kind, Is.EqualTo(OutcomeKind.Continue));
    }
}
