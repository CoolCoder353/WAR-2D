using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace WAR2D.Net
{
    /// <summary>
    /// Sent to a player the host kicked or banned from the lobby (playtest 2026-11-02, Figma K1/K2), just before the server
    /// closes the connection, so their Play screen can say why.
    /// </summary>
    public struct RemovedFromLobby : NetworkMessage
    {
        public bool Banned;
        /// <summary>The server's name as the lobby showed it.</summary>
        public string ServerName;
    }

    /// <summary>The words about a removal, and the one the client is waiting to show (client side).</summary>
    public static class LobbyRemoval
    {
        /// <summary>The Play screen line for a removal: kicked, or banned (also the refusal a banned player gets on joining).</summary>
        public static string Text(bool banned, string serverName)
        {
            string server = string.IsNullOrEmpty(serverName) ? "the server" : serverName;
            return banned ? $"You are banned from {server}." : $"You were removed from {server} by the host.";
        }

        /// <summary>The removal this client was told about and hasn't shown yet, or null.</summary>
        public static string Pending { get; private set; }

        /// <summary>Remembers a removal message from the server.</summary>
        public static void Received(RemovedFromLobby message) => Pending = Text(message.Banned, message.ServerName);

        /// <summary>Remembers that a running match refused this client, which never had a seat in it.</summary>
        public static void MatchStarted() => Pending = "That match has already started.";

        /// <summary>Returns the pending removal (once).</summary>
        public static string Take()
        {
            string text = Pending;
            Pending = null;
            return text;
        }
    }

    /// <summary>
    /// The players the host banned (server side, kept while the server is open): by Steam ID on Steam, by address on a
    /// Direct connection. Checked where a joining connection is authenticated.
    /// </summary>
    public sealed class LobbyBans
    {
        private readonly HashSet<string> keys = new HashSet<string>();

        /// <summary>How many bans are held.</summary>
        public int Count => keys.Count;

        public static string SteamKey(ulong steamId) => "steam:" + steamId;
        public static string AddressKey(string address) => "addr:" + (address ?? "");

        /// <summary>The key for a connection: its Steam ID when it signed in through Steam, else its address.</summary>
        public static string KeyOf(NetworkConnectionToClient conn) =>
            Steam.SteamIdentity.TryGet(conn.connectionId, out ulong steamId) ? SteamKey(steamId) : AddressKey(conn.address);

        public void Add(string key) { if (!string.IsNullOrEmpty(key)) keys.Add(key); }
        public bool Contains(string key) => key != null && keys.Contains(key);
        public void Clear() => keys.Clear();
    }

    /// <summary>Closing a connection just after a last message to it (a refusal, a kick or a ban).</summary>
    public static class Disconnects
    {
        /// <summary>
        /// Seconds between the message and the close: Mirror sends batched messages at the end of the frame, so closing at
        /// once would drop the message.
        /// </summary>
        public const float AfterMessageSeconds = 0.3f;

        /// <summary>
        /// Waits <see cref="AfterMessageSeconds"/>, then calls <paramref name="close"/> if <paramref name="conn"/> is still
        /// connected: the same connection, not a newer one that took its id (code review 2026-11-03). Run as a coroutine.
        /// </summary>
        public static IEnumerator CloseSoon(NetworkConnectionToClient conn, Action<NetworkConnectionToClient> close)
        {
            yield return new WaitForSecondsRealtime(AfterMessageSeconds);
            if (IsStillConnected(conn)) close(conn);
        }

        /// <summary>True when the server still lists this very connection under its id.</summary>
        public static bool IsStillConnected(NetworkConnectionToClient conn) =>
            conn != null && NetworkServer.connections.TryGetValue(conn.connectionId, out NetworkConnectionToClient listed) && listed == conn;
    }

    /// <summary>The server's name as players see it: the host's choice, else "&lt;host&gt;'s server".</summary>
    public static class ServerNames
    {
        public static string Display(string chosen, string hostNickname) =>
            !string.IsNullOrEmpty(chosen) ? chosen : string.IsNullOrEmpty(hostNickname) ? "Server" : $"{hostNickname}'s server";
    }
}
