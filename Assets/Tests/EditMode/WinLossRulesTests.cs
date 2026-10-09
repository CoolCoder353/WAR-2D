using System.Collections.Generic;
using NUnit.Framework;

public class WinLossRulesTests
{
    private static PlayerStatus P(int id, bool hq, bool elim = false) => new PlayerStatus(id, hq, elim);

    /// <summary>The v0.5 rule: players attack exactly those on other starting teams; hostile when there were ≥ 2 teams.</summary>
    private static MatchOutcome FromTeams(IReadOnlyList<PlayerStatus> players, int matchStartTeamCount)
    {
        var teamOf = new Dictionary<int, int>();
        foreach (PlayerStatus p in players) teamOf[p.PlayerId] = p.Team;
        return WinLossRules.Evaluate(players, (a, b) => teamOf[a] != teamOf[b], matchStartTeamCount > 1);
    }

    [Test]
    public void TwoPlayersWithHQ_Continue()
    {
        var o = FromTeams(new[] { P(1, true), P(2, true) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
        Assert.That(o.NewlyEliminated, Is.Empty);
    }

    [Test]
    public void OneHQLeft_InMultiplayerMatch_Wins_AndOthersEliminated()
    {
        var o = FromTeams(new[] { P(1, true), P(2, false) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
        Assert.That(o.Winners, Is.EquivalentTo(new[] { 1 }));
        Assert.That(o.NewlyEliminated, Is.EquivalentTo(new[] { 2 }));
    }

    [Test]
    public void LastPlayer_AfterOpponentLeft_Wins()
    {
        // Opponent disconnected and was removed; match started with 2.
        var o = FromTeams(new[] { P(1, true) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
    }

    [Test]
    public void SoloMatch_NeverWins()
    {
        Assert.That(FromTeams(new[] { P(1, true) }, 1).Kind, Is.EqualTo(OutcomeKind.Continue));
    }

    [Test]
    public void NoHQsLeft_IsDraw()
    {
        var o = FromTeams(new[] { P(1, false), P(2, false) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Draw));
    }

    [Test]
    public void AlreadyEliminated_IsNotReEliminated()
    {
        var o = FromTeams(new[] { P(1, true), P(2, true), P(3, false, elim: true) }, 3);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
        Assert.That(o.NewlyEliminated, Is.Empty);
    }

    [Test]
    public void NoPlayers_Continue()
    {
        Assert.That(FromTeams(new PlayerStatus[0], 2).Kind, Is.EqualTo(OutcomeKind.Continue));
    }

    [Test]
    public void LastTeamStandingWins()
    {
        var o = FromTeams(new[] { new PlayerStatus(1, true, false, 0), new PlayerStatus(2, true, false, 0), new PlayerStatus(3, false, false, 1) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
        Assert.That(o.Winners, Is.EquivalentTo(new[] { 1, 2 }));
        Assert.That(o.NewlyEliminated, Is.EquivalentTo(new[] { 3 }));
    }

    [Test]
    public void EliminatedTeammateSharesTheWin()
    {
        var o = FromTeams(new[] { new PlayerStatus(1, true, false, 0), new PlayerStatus(2, false, true, 0), new PlayerStatus(3, false, false, 1) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
        Assert.That(o.Winners, Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void AlliedSurvivorsDoNotEndTheMatchEarly()
    {
        var o = FromTeams(new[] { new PlayerStatus(1, true, false, 0), new PlayerStatus(2, false, false, 0), new PlayerStatus(3, true, false, 1) }, 2);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
    }

    [Test]
    public void MutualPeaceEndsMatch()
    {
        var players = new[] { P(1, true), P(2, true), P(3, true) };
        var o = WinLossRules.Evaluate(players, (a, b) => false, startedHostile: true);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Winner));
        Assert.That(o.Winners, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void OneWayPeaceContinues()
    {
        // A (1) does not attack B (2), but B attacks A.
        var o = WinLossRules.Evaluate(new[] { P(1, true), P(2, true) }, (a, b) => a == 2 && b == 1, startedHostile: true);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
    }

    [Test]
    public void NoHostilityAtStartNeverWins()
    {
        var o = WinLossRules.Evaluate(new[] { P(1, true), P(2, true) }, (a, b) => false, startedHostile: false);
        Assert.That(o.Kind, Is.EqualTo(OutcomeKind.Continue));
    }
}
