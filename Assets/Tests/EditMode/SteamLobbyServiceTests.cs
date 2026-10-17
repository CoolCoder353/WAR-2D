using NUnit.Framework;
using WAR2D.Net.Steam;

public class SteamLobbyServiceTests
{
    private static MatchSettings Settings(int size = 128) => new MatchSettings { Mode = MatchMode.FreeForAll, MapSize = size, Seed = 1, StartingResources = 100, StartOptions = 3 };

    [Test]
    public void OpenCreatesALobbyWithMetadata()
    {
        var fake = new FakeSteamBackend();
        var service = new SteamLobbyService(fake);
        service.Open("Ann", Settings(), 1);
        Assert.AreNotEqual(0UL, service.LobbyId);
        var data = fake.LobbyData[service.LobbyId];
        Assert.AreEqual("lobby", data[LobbyInfo.KeyState]);
        Assert.AreEqual(GameVersion.Current, data[LobbyInfo.KeyVersion]);
        Assert.AreEqual("Ann", data[LobbyInfo.KeyHostName]);
        Assert.IsTrue(fake.Joinable[service.LobbyId]);
    }

    [Test]
    public void StartingTheMatchClosesTheLobbyToJoiners()
    {
        var fake = new FakeSteamBackend();
        var service = new SteamLobbyService(fake);
        service.Open("Ann", Settings(), 2);
        service.Update(GameState.Countdown, Settings(), 2);
        Assert.IsFalse(fake.Joinable[service.LobbyId]);
        Assert.AreEqual("playing", fake.LobbyData[service.LobbyId][LobbyInfo.KeyState]);
    }

    [Test]
    public void ChangingTheMapUpdatesTheListing()
    {
        var fake = new FakeSteamBackend();
        var service = new SteamLobbyService(fake);
        service.Open("Ann", Settings(128), 1);
        service.Update(GameState.Lobby, Settings(256), 1);
        Assert.AreEqual("256", fake.LobbyData[service.LobbyId][LobbyInfo.KeyMapSize]);
    }

    [Test]
    public void CloseLeavesTheLobbyOnce()
    {
        var fake = new FakeSteamBackend();
        var service = new SteamLobbyService(fake);
        service.Open("Ann", Settings(), 1);
        service.Close();
        service.Close();
        Assert.AreEqual(1, fake.Left.Count);
    }
}
