using NUnit.Framework;

/// <summary>v0.18: remembered lobby settings and who may ask for a rematch.</summary>
public class LobbyMemoryTests
{
    private static Config.LobbyConfig Lobby() { Config.ConfigLoader.ResetForTests(); return Config.ConfigLoader.LoadConfig().Lobby; }

    [Test]
    public void AStaleValueFallsBackAndTheSeedStaysNew()
    {
        Config.LobbyConfig lobby = Lobby();
        var defaults = new MatchSettings { MapSize = lobby.MapSizes[0], Seed = 7, StartingResources = lobby.StartingResources[0], StartOptions = 3, StartPickSeconds = 45 };
        var saved = new RememberedLobby { MapSize = 999, Diplomacy = true, StartingResources = lobby.StartingResources[1], StartOptions = 3, StartPickSeconds = 45 };
        MatchSettings s = LobbyMemory.Apply(defaults, saved, lobby);
        Assert.AreEqual(lobby.MapSizes[0], s.MapSize, "no longer offered");
        Assert.AreEqual(lobby.StartingResources[1], s.StartingResources);
        Assert.IsTrue(s.Diplomacy);
        Assert.AreEqual(7u, s.Seed);
    }

    [Test]
    public void NothingRememberedKeepsTheDefaults()
    {
        var defaults = new MatchSettings { MapSize = 5 };
        Assert.AreEqual(5, LobbyMemory.Apply(defaults, null, Lobby()).MapSize);
    }

    [TestCase(GameState.GameOver, true, false, true)]
    [TestCase(GameState.GameOver, false, false, false)]
    [TestCase(GameState.GameOver, true, true, false)]
    [TestCase(GameState.Playing, true, false, false)]
    public void OnlyTheOwnerAfterTheReplayIsSaved(GameState state, bool owner, bool pending, bool may) =>
        Assert.AreEqual(may, RematchRules.May(state, owner, pending));
}
