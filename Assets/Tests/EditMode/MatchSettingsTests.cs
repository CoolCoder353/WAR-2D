using Config;
using NUnit.Framework;

public class MatchSettingsTests
{
    private static readonly LobbyConfig Lobby = new LobbyConfig { MapSizes = new[] { 256, 512, 1024 }, StartingResources = new[] { 500f, 1000f, 2500f, 5000f } };
    private static MatchSettings Valid => new MatchSettings { Mode = MatchMode.Teams, Diplomacy = true, MapSize = 512, Seed = 42, StartingResources = 1000f };

    [Test]
    public void ValidSettingsPass() => Assert.IsTrue(MatchSettingsRules.IsValid(Valid, Lobby));

    [Test]
    public void SizeNotInTheListIsRejected()
    {
        MatchSettings s = Valid;
        s.MapSize = 300;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        s.MapSize = 0;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby), "the scene's tilemaps are not a lobby choice");
    }

    [Test]
    public void ResourcesNotInTheListAreRejected()
    {
        MatchSettings s = Valid;
        s.StartingResources = 999f;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        s.StartingResources = float.NaN;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
    }

    [Test]
    public void OutOfRangeModeOrZeroSeedIsRejected()
    {
        MatchSettings s = Valid;
        s.Mode = (MatchMode)7;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
        s = Valid;
        s.Seed = 0;
        Assert.IsFalse(MatchSettingsRules.IsValid(s, Lobby));
    }

    [Test]
    public void ColoursAndTeams()
    {
        Assert.AreEqual(2, LobbyRules.FirstFreeColour(new[] { 0, 1, 3 }));
        Assert.AreEqual(-1, LobbyRules.FirstFreeColour(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        Assert.IsFalse(LobbyRules.IsColourFree(3, new[] { 3 }));
        Assert.IsFalse(LobbyRules.IsColourFree(8, new int[0]));
        Assert.IsTrue(LobbyRules.IsColourFree(4, new[] { 3 }));
        Assert.IsTrue(LobbyRules.IsTeamChoiceValid(MatchMode.FreeForAll, TeamRules.NoTeam));
        Assert.IsFalse(LobbyRules.IsTeamChoiceValid(MatchMode.FreeForAll, 0), "free-for-all has no teams");
        Assert.IsTrue(LobbyRules.IsTeamChoiceValid(MatchMode.Teams, 3));
        Assert.IsFalse(LobbyRules.IsTeamChoiceValid(MatchMode.Teams, 4));
    }

    [Test]
    public void OnlyTheLobbyTakesNewPlayersUpToTheColours()
    {
        Assert.IsTrue(LobbyRules.MayJoin(GameState.Lobby, 1), "the host");
        Assert.IsTrue(LobbyRules.MayJoin(GameState.Lobby, LobbyRules.MaxPlayers));
        Assert.IsFalse(LobbyRules.MayJoin(GameState.Lobby, LobbyRules.MaxPlayers + 1), "a ninth player would share a colour and have no HQ site");
        Assert.AreEqual(LobbyRules.Colours, LobbyRules.MaxPlayers);
        foreach (GameState state in System.Enum.GetValues(typeof(GameState)))
            if (state != GameState.Lobby) Assert.IsFalse(LobbyRules.MayJoin(state, 2), $"no joining in {state}");
    }
}
