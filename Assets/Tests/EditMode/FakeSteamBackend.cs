using System;
using System.Collections.Generic;
using WAR2D.Net.Steam;

public sealed class FakeSteamBackend : ISteamBackend
{
    public bool Available { get; set; } = true;
    public ulong LocalSteamId { get; set; } = 76561198000000001UL;
    public string LocalName { get; set; } = "Tester";
    public bool IssueTickets = true, AcceptBegin = true;
    public readonly List<ulong> Ended = new List<ulong>();
    public readonly Dictionary<ulong, Dictionary<string, string>> LobbyData = new Dictionary<ulong, Dictionary<string, string>>();
    public readonly List<LobbyInfo> Listing = new List<LobbyInfo>();
    public ulong NextLobbyId = 900;
    public event Action<ulong, bool> AuthSessionResult;
    public event Action<ulong> InviteAccepted;

    public bool TryGetAuthTicket(out byte[] ticket) { ticket = IssueTickets ? new byte[] { 1, 2, 3, 4 } : null; return IssueTickets; }
    public bool BeginAuthSession(ulong steamId, byte[] ticket) => AcceptBegin;
    public void EndAuthSession(ulong steamId) => Ended.Add(steamId);
    public void RaiseAuthResult(ulong steamId, bool accepted) => AuthSessionResult?.Invoke(steamId, accepted);
    public void RaiseInvite(ulong lobbyId) => InviteAccepted?.Invoke(lobbyId);

    public void CreateLobby(int maxPlayers, Action<ulong> done) { ulong id = NextLobbyId++; LobbyData[id] = new Dictionary<string, string>(); done(id); }
    public void SetLobbyData(ulong lobbyId, IDictionary<string, string> data) { foreach (var kv in data) LobbyData[lobbyId][kv.Key] = kv.Value; }
    public readonly Dictionary<ulong, bool> Joinable = new Dictionary<ulong, bool>();
    public void SetLobbyJoinable(ulong lobbyId, bool joinable) => Joinable[lobbyId] = joinable;
    public readonly List<ulong> Left = new List<ulong>();
    public void LeaveLobby(ulong lobbyId) => Left.Add(lobbyId);
    public void RequestLobbyList(Action<IReadOnlyList<LobbyInfo>> done) => done(Listing);
    public void JoinLobby(ulong lobbyId, Action<ulong> done)
    {
        foreach (LobbyInfo l in Listing) if (l.LobbyId == lobbyId) { done(l.HostSteamId); return; }
        done(0);
    }
    public readonly List<ulong> InviteDialogs = new List<ulong>();
    public void OpenInviteDialog(ulong lobbyId) => InviteDialogs.Add(lobbyId);
    public readonly List<string> Presence = new List<string>();
    public void SetRichPresence(string status) => Presence.Add(status);
}
