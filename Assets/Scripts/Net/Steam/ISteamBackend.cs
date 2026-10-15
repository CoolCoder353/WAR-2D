using System;
using System.Collections.Generic;

namespace WAR2D.Net.Steam
{
    /// <summary>The only surface of Steam the game sees. The real one lives in the WAR2D.Steam assembly; tests use a fake.</summary>
    public interface ISteamBackend
    {
        bool Available { get; }
        ulong LocalSteamId { get; }
        string LocalName { get; }

        // auth
        bool TryGetAuthTicket(out byte[] ticket);
        /// <summary>False = rejected immediately.</summary>
        bool BeginAuthSession(ulong steamId, byte[] ticket);
        void EndAuthSession(ulong steamId);
        /// <summary>Steam's verdict: steamId, accepted.</summary>
        event Action<ulong, bool> AuthSessionResult;

        // lobbies
        /// <summary>Calls back with the lobby id, 0 = failed.</summary>
        void CreateLobby(int maxPlayers, Action<ulong> done);
        void SetLobbyData(ulong lobbyId, IDictionary<string, string> data);
        void SetLobbyJoinable(ulong lobbyId, bool joinable);
        void LeaveLobby(ulong lobbyId);
        void RequestLobbyList(Action<IReadOnlyList<LobbyInfo>> done);
        /// <summary>Calls back with the host's Steam ID, 0 = failed.</summary>
        void JoinLobby(ulong lobbyId, Action<ulong> done);
        /// <summary>The player accepted a Steam invite: the lobby id.</summary>
        event Action<ulong> InviteAccepted;
        /// <summary>Steam overlay "invite friends".</summary>
        void OpenInviteDialog(ulong lobbyId);
        void SetRichPresence(string status);
    }

    /// <summary>Holds the active backend (null when Steam is not running).</summary>
    public static class SteamServices
    {
        public static ISteamBackend Backend;
        public static bool Available => Backend != null && Backend.Available;
    }
}
