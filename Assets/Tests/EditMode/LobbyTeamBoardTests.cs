using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WAR2D.UI;

/// <summary>
/// Playtest 2026-11-05: the lobby team board's quick setups (Figma L1), and the unit cap notice (L4).
/// </summary>
public class LobbyTeamBoardTests
{
    private const int Me = 1;

    private static List<LobbyPlayer> Lobby(int humans, int ais)
    {
        var players = new List<LobbyPlayer>();
        for (int h = 0; h < humans; h++) players.Add(new LobbyPlayer(Me + h, "H" + h, h, TeamRules.NoTeam, false, h == 0));
        for (int a = 0; a < ais; a++) players.Add(new LobbyPlayer(100 + a, "A" + a, humans + a, a % 3, true, false, isAi: true));
        return players;
    }

    [Test]
    public void MeVsTheAisPutsTheHostAloneOnTeamOne()
    {
        List<LobbyPlayer> players = Lobby(2, 3);
        int[] teams = LobbyTeamSetup.Teams(QuickSetup.MeVsTheAis, players, Me, new System.Random(1));
        Assert.That(teams, Is.EqualTo(new[] { 0, 1, 1, 1, 1 }));
    }

    [Test]
    public void HumansVsAisSplitsByKind()
    {
        int[] teams = LobbyTeamSetup.Teams(QuickSetup.HumansVsAis, Lobby(2, 3), Me, new System.Random(1));
        Assert.That(teams, Is.EqualTo(new[] { 0, 0, 1, 1, 1 }));
    }

    [Test]
    public void SplitEvenlyGivesEachHumanATeamAndBalancesTheAis()
    {
        int[] three = LobbyTeamSetup.Teams(QuickSetup.SplitEvenly, Lobby(3, 3), Me, new System.Random(1));
        Assert.That(three.Take(3), Is.EqualTo(new[] { 0, 1, 2 }), "one human per team");
        Assert.That(three.GroupBy(t => t).Select(g => g.Count()), Is.All.EqualTo(2), "teams of two");

        int[] alone = LobbyTeamSetup.Teams(QuickSetup.SplitEvenly, Lobby(1, 3), Me, new System.Random(1));
        Assert.That(alone.Distinct().OrderBy(t => t), Is.EqualTo(new[] { 0, 1 }), "at least two teams");
        Assert.That(alone.Count(t => t == 0), Is.EqualTo(2), "the host's team gets an AI too: 2 against 2");
    }

    [Test]
    public void ShuffleKeepsTheTeamSizes()
    {
        List<LobbyPlayer> players = Lobby(2, 6);
        int[] before = players.Select(p => p.Team).ToArray();
        int[] after = LobbyTeamSetup.Teams(QuickSetup.Shuffle, players, Me, new System.Random(7));
        Assert.That(after.OrderBy(t => t), Is.EqualTo(before.OrderBy(t => t)));
    }

    [Test]
    public void EveryoneSoloMakesEveryoneSolo()
    {
        Assert.That(LobbyTeamSetup.Teams(QuickSetup.EveryoneSolo, Lobby(2, 2), Me, new System.Random(1)), Is.All.EqualTo(TeamRules.NoTeam));
    }

    [Test]
    public void EveryQuickSetupGivesTeamsTheServerAccepts()
    {
        foreach ((QuickSetup setup, string _) in LobbyTeamSetup.Buttons)
            foreach (int humans in new[] { 1, 2, 4, 6 })
                foreach (int team in LobbyTeamSetup.Teams(setup, Lobby(humans, 8 - humans), Me, new System.Random(3)))
                    Assert.That(LobbyRules.IsTeamChoiceValid(MatchMode.Teams, team), Is.True, $"{setup} with {humans} humans");
    }

    [Test]
    public void TheUnitCapAlertComesOnceUntilASpawnSucceeds()
    {
        var player = new ServerPlayer(null, 100f);
        Assert.That(player.SetAtUnitCap(true), Is.True, "the first refusal alerts");
        Assert.That(player.SetAtUnitCap(true), Is.False, "later refusals don't");
        Assert.That(player.AtUnitCap, Is.True);
        Assert.That(player.SetAtUnitCap(false), Is.False, "a spawn below the cap clears it quietly");
        Assert.That(player.AtUnitCap, Is.False);
        Assert.That(player.SetAtUnitCap(true), Is.True, "reaching the cap again alerts again");
    }

    [Test]
    public void TheUnitCapAlertSaysTheCap()
    {
        string text = AlertFeedModel.Text(new Alert { Kind = AlertKind.UnitCapReached, Amount = 10000 }, _ => "", null);
        Assert.That(text, Is.EqualTo("Unit cap reached (10,000): producers wait"));
    }
}
