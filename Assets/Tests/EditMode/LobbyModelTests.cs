using NUnit.Framework;
using WAR2D.UI;

public class LobbyModelTests
{
    private static readonly MatchSettings Settings = new MatchSettings { Mode = MatchMode.Teams, MapSize = 512, Seed = 3, StartingResources = 1000f };

    private static LobbyModel Lobby(params LobbyPlayer[] players)
    {
        var model = new LobbyModel();
        model.Set(players, Settings, 1);
        return model;
    }

    [Test]
    public void StartNeedsEveryoneReady()
    {
        LobbyModel model = Lobby(new LobbyPlayer(1, "Host", 0, TeamRules.NoTeam, true, true), new LobbyPlayer(2, "Rook", 1, TeamRules.NoTeam, false, false));
        Assert.IsFalse(model.CanStart);
        Assert.AreEqual("Waiting for Rook to be ready", model.Status);
        model.Set(new[] { new LobbyPlayer(1, "Host", 0, TeamRules.NoTeam, true, true), new LobbyPlayer(2, "Rook", 1, TeamRules.NoTeam, true, false) }, Settings, 1);
        Assert.IsTrue(model.CanStart);
    }

    [Test]
    public void StartNeedsTwoTeams()
    {
        LobbyModel model = Lobby(new LobbyPlayer(1, "Host", 0, 0, true, true), new LobbyPlayer(2, "Rook", 1, 0, true, false));
        Assert.IsFalse(model.CanStart, "everyone on one team means nobody attacks anyone");
        Assert.AreEqual("Needs at least two players or teams", model.Status);
        Assert.IsFalse(Lobby(new LobbyPlayer(1, "Host", 0, TeamRules.NoTeam, true, true)).CanStart, "a lone player can't start");
    }

    [Test]
    public void ChangedOnlyWhenSomethingShownChanges()
    {
        var model = new LobbyModel();
        int raised = 0;
        model.Changed += () => raised++;
        var players = new[] { new LobbyPlayer(1, "Host", 0, TeamRules.NoTeam, false, true) };
        model.Set(players, Settings, 1);
        model.Set(players, Settings, 1);
        Assert.AreEqual(1, raised);
        MatchSettings rerolled = Settings;
        rerolled.Seed = 4;
        model.Set(players, rerolled, 1);
        Assert.AreEqual(2, raised);
    }

    [Test]
    public void TakenColoursExcludeMine()
    {
        LobbyModel model = Lobby(new LobbyPlayer(1, "Host", 2, TeamRules.NoTeam, false, true), new LobbyPlayer(2, "Rook", 5, TeamRules.NoTeam, false, false));
        Assert.IsTrue(model.IsColourTaken(5));
        Assert.IsFalse(model.IsColourTaken(2), "my own colour is not taken from me");
        Assert.IsTrue(model.LocalIsHost);
        Assert.AreEqual("Waiting for you and Rook to be ready", model.Status);
    }
}
