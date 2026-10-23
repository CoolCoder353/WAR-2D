using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.Net.Steam;
using WAR2D.UI;

/// <summary>The friends' list on the Play screen, with a stub Steam backend (no Steam client needed).</summary>
public class FriendsListTests
{
    private sealed class StubSteam : ISteamBackend
    {
        public bool Available => true;
        public ulong LocalSteamId => 1;
        public string LocalName => "Tester";
        public event Action<ulong, bool> AuthSessionResult { add { } remove { } }
        public event Action<ulong> InviteAccepted { add { } remove { } }
        public List<LobbyInfo> Listing = new List<LobbyInfo>();
        public bool TryGetAuthTicket(out byte[] ticket) { ticket = null; return false; }
        public bool BeginAuthSession(ulong steamId, byte[] ticket) => false;
        public void EndAuthSession(ulong steamId) { }
        public void CreateLobby(int maxPlayers, LobbyVisibility visibility, Action<ulong> done) => done(0);
        public void SetLobbyType(ulong lobbyId, LobbyVisibility visibility) { }
        public void SetLobbyData(ulong lobbyId, IDictionary<string, string> data) { }
        public void SetLobbyJoinable(ulong lobbyId, bool joinable) { }
        public void LeaveLobby(ulong lobbyId) { }
        public void RequestLobbyList(Action<IReadOnlyList<LobbyInfo>> done) => done(Listing);
        public void JoinLobby(ulong lobbyId, Action<ulong> done) => done(0);
        public void OpenInviteDialog(ulong lobbyId) { }
        public void SetRichPresence(string status) { }
    }

    private ISteamBackend previous;
    private StubSteam stub;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        previous = SteamServices.Backend;
        stub = new StubSteam();
        SteamServices.Backend = stub;
        PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        SteamServices.Backend = previous;
        yield return PlayModeMatch.TearDown();
    }

    private static VisualElement Root() => UnityEngine.Object.FindAnyObjectByType<MenuController>().GetComponent<UIDocument>().rootVisualElement;

    [UnityTest, Timeout(60000)]
    public IEnumerator PlayScreenShowsTheEmptyStateWhenNoFriendIsHosting()
    {
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("play-button"));
        yield return PlayModeMatch.WaitUntil(() => root.Q("friends-empty").ClassListContains("play-panel__notice--visible"), 5f);
        Assert.That(root.Q("game-list").childCount, Is.EqualTo(0));
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator PlayScreenListsAFriendsGame()
    {
        stub.Listing.Add(new LobbyInfo { LobbyId = 5, HostSteamId = 6, HostName = "Ann", Version = GameVersion.Current, State = "lobby", Mode = "FreeForAll", MapSize = 256, Players = 3 });
        VisualElement root = Root();
        HudTests.Click(root.Q<Button>("play-button"));
        yield return PlayModeMatch.WaitUntil(() => root.Q("game-list").childCount == 1, 5f);
        Assert.That(root.Q<Label>("game-host").text, Is.EqualTo("Ann"));
        Assert.That(root.Q("friends-empty").ClassListContains("play-panel__notice--visible"), Is.False);
    }
}
