using System;
using System.Collections.Generic;
using Steamworks;
using WAR2D.Net.Steam;

namespace WAR2D.Steam
{
    /// <summary>ISteamBackend on Steamworks.NET. Everything here runs on the main thread (callbacks are pumped by SteamPump).</summary>
    public sealed class SteamworksBackend : ISteamBackend
    {
        public bool Available { get; private set; }
        public ulong LocalSteamId => SteamUser.GetSteamID().m_SteamID;
        public string LocalName => SteamFriends.GetPersonaName();

        public event Action<ulong, bool> AuthSessionResult;
        public event Action<ulong> InviteAccepted;

        private HAuthTicket ticketHandle;
        private Callback<ValidateAuthTicketResponse_t> authResponse;
        private Callback<GameLobbyJoinRequested_t> joinRequested;
        private CallResult<LobbyCreated_t> created;
        private CallResult<LobbyMatchList_t> matchList;
        private CallResult<LobbyEnter_t> entered;

        public SteamworksBackend(bool initialised)
        {
            Available = initialised;
            if (!initialised) return;
            authResponse = Callback<ValidateAuthTicketResponse_t>.Create(r =>
                AuthSessionResult?.Invoke(r.m_SteamID.m_SteamID, r.m_eAuthSessionResponse == EAuthSessionResponse.k_EAuthSessionResponseOK));
            joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => InviteAccepted?.Invoke(r.m_steamIDLobby.m_SteamID));
            created = CallResult<LobbyCreated_t>.Create();
            matchList = CallResult<LobbyMatchList_t>.Create();
            entered = CallResult<LobbyEnter_t>.Create();
        }

        public bool TryGetAuthTicket(out byte[] ticket)
        {
            byte[] buffer = new byte[SteamAuthPolicy.MaxTicketBytes];
            var identity = new SteamNetworkingIdentity();
            identity.Clear();
            ticketHandle = SteamUser.GetAuthSessionTicket(buffer, buffer.Length, out uint length, ref identity);
            if (ticketHandle == HAuthTicket.Invalid || length == 0) { ticket = null; return false; }
            ticket = new byte[length];
            Array.Copy(buffer, ticket, length);
            return true;
        }

        public bool BeginAuthSession(ulong steamId, byte[] ticket) =>
            SteamUser.BeginAuthSession(ticket, ticket.Length, new CSteamID(steamId)) == EBeginAuthSessionResult.k_EBeginAuthSessionResultOK;

        public void EndAuthSession(ulong steamId) => SteamUser.EndAuthSession(new CSteamID(steamId));

        // Friends-only for the playtest: the browser lists friends' lobbies. Switch to k_ELobbyTypePublic for public games.
        public void CreateLobby(int maxPlayers, Action<ulong> done) =>
            created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers),
                (r, ioFailure) => done(!ioFailure && r.m_eResult == EResult.k_EResultOK ? r.m_ulSteamIDLobby : 0));

        public void SetLobbyData(ulong lobbyId, IDictionary<string, string> data)
        {
            foreach (var kv in data) SteamMatchmaking.SetLobbyData(new CSteamID(lobbyId), kv.Key, kv.Value);
        }

        public void SetLobbyJoinable(ulong lobbyId, bool joinable) => SteamMatchmaking.SetLobbyJoinable(new CSteamID(lobbyId), joinable);
        public void LeaveLobby(ulong lobbyId) => SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));

        public void RequestLobbyList(Action<IReadOnlyList<LobbyInfo>> done)
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(LobbyInfo.KeyState, "lobby", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            matchList.Set(SteamMatchmaking.RequestLobbyList(), (r, ioFailure) =>
            {
                var list = new List<LobbyInfo>();
                if (!ioFailure)
                    for (int i = 0; i < r.m_nLobbiesMatching; i++)
                    {
                        CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                        var data = new Dictionary<string, string>();
                        foreach (string key in new[] { LobbyInfo.KeyVersion, LobbyInfo.KeyState, LobbyInfo.KeyMode, LobbyInfo.KeyMapSize, LobbyInfo.KeyHostName })
                            data[key] = SteamMatchmaking.GetLobbyData(id, key);
                        if (LobbyInfo.TryDecode(id.m_SteamID, SteamMatchmaking.GetLobbyOwner(id).m_SteamID,
                                SteamMatchmaking.GetNumLobbyMembers(id), data, out LobbyInfo info)) list.Add(info);
                    }
                done(list);
            });
        }

        public void JoinLobby(ulong lobbyId, Action<ulong> done) =>
            entered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)), (r, ioFailure) =>
                done(!ioFailure && r.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                    ? SteamMatchmaking.GetLobbyOwner(new CSteamID(r.m_ulSteamIDLobby)).m_SteamID : 0));

        public void OpenInviteDialog(ulong lobbyId) => SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(lobbyId));

        // Needs the "#Status" token in the Steamworks Rich Presence settings; without it friends see nothing, which is harmless.
        public void SetRichPresence(string status)
        {
            if (status == null) { SteamFriends.ClearRichPresence(); return; }
            SteamFriends.SetRichPresence("steam_display", "#Status");
            SteamFriends.SetRichPresence("status", status);
        }
    }
}
