using System;
using System.Collections.Generic;

namespace WAR2D.Net.Steam
{
    /// <summary>The client's steps from "find a game" to a <see cref="ConnectionRoute"/>. UI-free.</summary>
    public sealed class SteamJoinFlow
    {
        private readonly ISteamBackend backend;
        public SteamJoinFlow(ISteamBackend backend) { this.backend = backend; }

        /// <summary>Requests the lobby list and reports the joinable ones.</summary>
        public void Refresh(Action<List<LobbyInfo>> shown) =>
            backend.RequestLobbyList(all => shown(LobbyListModel.Visible(all, GameVersion.Current)));

        /// <summary>Joins the Steam lobby, then reports the host's route (null on failure).</summary>
        public void JoinLobby(ulong lobbyId, Action<ConnectionRoute?> done) =>
            backend.JoinLobby(lobbyId, hostId => done(hostId == 0 ? (ConnectionRoute?)null : ConnectionRoute.ForSteam(hostId)));

        /// <summary>Calls back when the player accepts a Steam invite.</summary>
        public void ListenForInvites(Action<ulong> lobbyId) => backend.InviteAccepted += lobbyId;
    }
}
