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

    // ---- v0.14 Visibility ----

    private static MatchSettings With(LobbyVisibility v) { MatchSettings s = Settings(); s.Visibility = v; return s; }

    [Test]
    public void TheLobbyIsCreatedWithTheSettingsVisibility()
    {
        var fake = new FakeSteamBackend();
        new SteamLobbyService(fake).Open("Ann", With(LobbyVisibility.Public), 1);
        Assert.That(fake.Created, Has.Count.EqualTo(1));
        Assert.AreEqual(LobbyVisibility.Public, fake.Created[0].type);
        var friends = new FakeSteamBackend();
        new SteamLobbyService(friends).Open("Ann", Settings(), 1);
        Assert.AreEqual(LobbyVisibility.Friends, friends.Created[0].type, "Friends by default");
    }

    [Test]
    public void PrivateMakesNoSteamLobby()
    {
        var fake = new FakeSteamBackend();
        var service = new SteamLobbyService(fake);
        service.Open("Ann", With(LobbyVisibility.Private), 1);
        service.Update(GameState.Lobby, With(LobbyVisibility.Private), 3);
        Assert.That(fake.Created, Is.Empty);
        Assert.AreEqual(0UL, service.LobbyId);
        Assert.IsFalse(service.IsOpen);
    }

    [Test]
    public void ChangingVisibilityRetypesOpensOrClosesTheLobby()
    {
        var fake = new FakeSteamBackend();
        var service = new SteamLobbyService(fake);
        service.Open("Ann", With(LobbyVisibility.Friends), 1);
        ulong first = service.LobbyId;

        service.Update(GameState.Lobby, With(LobbyVisibility.Public), 1);
        Assert.That(fake.Retyped, Is.EqualTo(new[] { (first, LobbyVisibility.Public) }), "Friends → Public retypes the open lobby");
        Assert.AreEqual(first, service.LobbyId);
        service.Update(GameState.Lobby, With(LobbyVisibility.Public), 1);
        Assert.That(fake.Retyped, Has.Count.EqualTo(1), "no retype without a change");

        service.Update(GameState.Lobby, With(LobbyVisibility.Private), 1);
        Assert.That(fake.Left, Is.EqualTo(new[] { first }), "Private closes it");
        Assert.AreEqual(0UL, service.LobbyId);

        service.Update(GameState.Lobby, With(LobbyVisibility.Friends), 1);
        Assert.That(fake.Created, Has.Count.EqualTo(2), "leaving Private opens a new one");
        Assert.AreEqual(LobbyVisibility.Friends, fake.Created[1].type);
        Assert.AreNotEqual(first, service.LobbyId);
        Assert.AreEqual("lobby", fake.LobbyData[service.LobbyId][LobbyInfo.KeyState], "and lists it");
    }

    [Test]
    public void ALobbySteamCreatesAfterGoingPrivateIsLeftAtOnce()
    {
        var fake = new FakeSteamBackend { DeferCreate = true };
        var service = new SteamLobbyService(fake);
        service.Open("Ann", With(LobbyVisibility.Friends), 1);
        service.Update(GameState.Lobby, With(LobbyVisibility.Private), 1);
        fake.FinishCreate();
        Assert.That(fake.Left, Is.EqualTo(new[] { fake.Created[0].lobby }), "nobody may find a Private host's lobby");
        Assert.AreEqual(0UL, service.LobbyId);
        Assert.IsFalse(service.IsOpen);
    }

    [Test]
    public void APrivateHostHasNoLobbyToInviteToAndSteamOffHasNone()
    {
        ISteamBackend previous = SteamServices.Backend;
        try
        {
            var fake = new FakeSteamBackend();
            SteamServices.Backend = fake;
            SteamHostLobby.Open("Ann", With(LobbyVisibility.Private), 1);
            Assert.IsFalse(SteamHostLobby.CanInvite, "Private: no lobby, no Invite");
            Assert.That(fake.Created, Is.Empty);
            SteamHostLobby.Current.Update(GameState.Lobby, With(LobbyVisibility.Friends), 1);
            Assert.IsTrue(SteamHostLobby.CanInvite, "leaving Private opens one (O9: only by changing Visibility)");
            SteamHostLobby.Close();

            var off = new FakeSteamBackend { Available = false };
            SteamServices.Backend = off;
            SteamHostLobby.Open("Ann", With(LobbyVisibility.Private), 1);
            Assert.IsNull(SteamHostLobby.Current, "Steam off: Private hosting needs no Steam at all");
            Assert.That(off.Created, Is.Empty);
        }
        finally { SteamHostLobby.Close(); SteamServices.Backend = previous; }
    }
}
