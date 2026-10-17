using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Net.Steam;

public class SteamJoinFlowTests
{
    private static LobbyInfo L(ulong id, int players, string state = "lobby", string ver = null) => new LobbyInfo
    { LobbyId = id, HostSteamId = id + 1, HostName = "h" + id, Version = ver ?? GameVersion.Current, State = state, Mode = "FreeForAll", MapSize = 128, Players = players };

    [Test]
    public void RefreshShowsOnlyJoinableLobbies()
    {
        var fake = new FakeSteamBackend();
        fake.Listing.AddRange(new[] { L(1, 2), L(2, 2, state: "playing"), L(3, 2, ver: "0.0.1") });
        List<LobbyInfo> shown = null;
        new SteamJoinFlow(fake).Refresh(l => shown = l);
        CollectionAssert.AreEqual(new ulong[] { 1 }, shown.ConvertAll(l => l.LobbyId));
    }

    [Test]
    public void JoiningALobbyYieldsTheHostsSteamRoute()
    {
        var fake = new FakeSteamBackend();
        fake.Listing.Add(L(10, 1));
        ConnectionRoute? route = null;
        new SteamJoinFlow(fake).JoinLobby(10, r => route = r);
        Assert.AreEqual(RouteKind.Steam, route.Value.Kind);
        Assert.AreEqual(11UL, route.Value.SteamId);
    }

    [Test]
    public void JoiningAMissingLobbyYieldsNull()
    {
        ConnectionRoute? route = new ConnectionRoute();
        new SteamJoinFlow(new FakeSteamBackend()).JoinLobby(99, r => route = r);
        Assert.IsNull(route);
    }

    [Test]
    public void AnAcceptedInviteIsForwarded()
    {
        var fake = new FakeSteamBackend();
        ulong got = 0;
        new SteamJoinFlow(fake).ListenForInvites(id => got = id);
        fake.RaiseInvite(42);
        Assert.AreEqual(42UL, got);
    }
}

public class SteamInviteTests
{
    [Test]
    public void InviteOpensTheDialogForTheHostedLobby()
    {
        var fake = new FakeSteamBackend();
        var previous = SteamServices.Backend;
        SteamServices.Backend = fake;
        try
        {
            Assert.IsFalse(SteamHostLobby.CanInvite);
            SteamHostLobby.Open("Ann", new MatchSettings { Mode = MatchMode.FreeForAll, MapSize = 128, Seed = 1, StartingResources = 100, StartOptions = 3 }, 1);
            Assert.IsTrue(SteamHostLobby.CanInvite);
            SteamHostLobby.OpenInvite();
            CollectionAssert.AreEqual(new[] { fake.NextLobbyId - 1 }, fake.InviteDialogs);
        }
        finally { SteamHostLobby.Close(); SteamServices.Backend = previous; }
    }

    [Test]
    public void StopListeningDropsTheInviteHandler()
    {
        var fake = new FakeSteamBackend();
        ulong got = 0;
        var flow = new SteamJoinFlow(fake);
        flow.ListenForInvites(id => got = id);
        flow.StopListening();
        fake.RaiseInvite(7);
        Assert.AreEqual(0UL, got);
    }
}
