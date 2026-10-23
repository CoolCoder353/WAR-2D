using WAR2D.Dev.Logging;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace WAR2D.Net.Steam
{
    /// <summary>
    /// Authenticates Steam relay connections with Steam auth tickets, and lets Direct (KCP) connections
    /// through only where <see cref="SteamAuthPolicy.DirectAllowed"/> says so and their <see cref="VersionHello"/>
    /// matches <see cref="GameVersion.Protocol"/>.
    /// </summary>
    public class SteamAuthenticator : NetworkAuthenticator
    {
        private const double RequestTimeoutSeconds = 10;

        /// <summary>Why the host last refused this client (shown on the Play screen); null when there was none.</summary>
        public static string LastRefusal { get; private set; }

        private SteamAuthSessions sessions;
        private readonly Dictionary<int, (NetworkConnectionToClient conn, double deadline)> waiting =
            new Dictionary<int, (NetworkConnectionToClient, double)>();
        private readonly List<int> expired = new List<int>();
        /// <summary>Direct connections waiting for their <see cref="VersionHello"/> (the rest of <see cref="waiting"/> wait on Steam).</summary>
        private readonly HashSet<int> directWaiting = new HashSet<int>();

        public override void OnStartServer()
        {
            NetworkServer.RegisterHandler<SteamAuthRequest>(OnRequest, false);
            NetworkServer.RegisterHandler<VersionHello>(OnHello, false);
            if (SteamServices.Available) sessions = new SteamAuthSessions(SteamServices.Backend, GameVersion.Current);
        }

        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<SteamAuthRequest>();
            NetworkServer.UnregisterHandler<VersionHello>();
            sessions?.Dispose();
            sessions = null;
            waiting.Clear();
            directWaiting.Clear();
        }

        public override void OnServerAuthenticate(NetworkConnectionToClient conn)
        {
            if (conn is LocalConnectionToClient) { ServerAccept(conn); return; } // the host's own client
            if (TransportSteamId(conn.connectionId) == 0)
            {
                // A KCP connection: no Steam identity to verify, only its version (its hello is on its way).
                if (SteamAuthPolicy.DirectAllowed(Application.isEditor, DevApi.Allowed, System.Environment.GetCommandLineArgs()))
                {
                    waiting[conn.connectionId] = (conn, NetworkTime.localTime + RequestTimeoutSeconds);
                    directWaiting.Add(conn.connectionId);
                }
                else Reject(conn, "Direct connections are off on this host.");
                return;
            }
            // The client's request is on its way; Update() enforces the timeout.
            waiting[conn.connectionId] = (conn, NetworkTime.localTime + RequestTimeoutSeconds);
        }

        private void OnHello(NetworkConnectionToClient conn, VersionHello msg)
        {
            if (!directWaiting.Remove(conn.connectionId)) return; // not a Direct connection waiting on its hello
            if (!DirectHandshake.Check(GameVersion.Protocol, GameVersion.Current, msg, out string reason)) { Reject(conn, reason); return; }
            waiting.Remove(conn.connectionId);
            conn.Send(new SteamAuthResponse { accepted = true });
            ServerAccept(conn);
        }

        private void OnRequest(NetworkConnectionToClient conn, SteamAuthRequest msg)
        {
            if (directWaiting.Contains(conn.connectionId)) return; // a Direct connection says hello, not Steam
            if (sessions == null) { Reject(conn, "This host is not signed in to Steam."); return; }
            if (!waiting.ContainsKey(conn.connectionId)) return; // not a connection waiting on Steam
            ulong claimed = TransportSteamId(conn.connectionId);
            if (sessions.OnRequest(conn.connectionId, msg, claimed, NetworkTime.localTime, out string reason) == SteamAuthSessions.Begin.Rejected)
                Reject(conn, reason);
            else
                waiting[conn.connectionId] = (conn, double.MaxValue); // SteamAuthSessions owns the timeout now
        }

        private void Update()
        {
            if (!NetworkServer.active) return;
            double now = NetworkTime.localTime;
            if (sessions != null)
                foreach (var (connectionId, accepted, reason) in sessions.Drain(now))
                {
                    if (!waiting.TryGetValue(connectionId, out var w)) continue;
                    waiting.Remove(connectionId);
                    if (!accepted) { Reject(w.conn, reason); continue; }
                    ulong steamId = TransportSteamId(connectionId);
                    SteamIdentity.Set(connectionId, steamId);
                    WarLog.Info(LogCategory.Steam, $"connection {connectionId} signed in as Steam {steamId}");
                    w.conn.Send(new SteamAuthResponse { accepted = true });
                    ServerAccept(w.conn);
                }
            expired.Clear();
            foreach (var kv in waiting) if (now >= kv.Value.deadline) expired.Add(kv.Key);
            foreach (int id in expired)
            {
                var w = waiting[id];
                Reject(w.conn, directWaiting.Contains(id)
                    ? "The client never said which game version it runs (an older build?). " + GameVersion.MismatchReason(GameVersion.Current, GameVersion.Protocol)
                    : "Steam sign-in timed out.");
            }
        }

        /// <summary>
        /// A connection closed (called from <see cref="GameManager.OnServerDisconnect"/>): drops its sign-in, so a
        /// verdict that arrives later neither accepts the dead connection nor records its Steam ID, and ends the
        /// Steam session it opened.
        /// </summary>
        public void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            if (conn == null) return;
            waiting.Remove(conn.connectionId);
            directWaiting.Remove(conn.connectionId);
            sessions?.Forget(conn.connectionId);
        }

        private void Reject(NetworkConnectionToClient conn, string reason)
        {
            WarLog.Warn(LogCategory.Steam, $"rejecting connection {conn.connectionId}: {reason}");
            waiting.Remove(conn.connectionId);
            directWaiting.Remove(conn.connectionId);
            sessions?.Forget(conn.connectionId);
            conn.Send(new SteamAuthResponse { accepted = false, reason = reason });
            ServerReject(conn);
        }

        /// <summary>The Steam ID behind a connection, or 0 when it is not a Steam relay connection.</summary>
        private static ulong TransportSteamId(int connectionId) =>
            ulong.TryParse(Transport.active?.ServerGetClientAddress(connectionId), out ulong id) ? id : 0;

        public override void OnStartClient() => NetworkClient.RegisterHandler<SteamAuthResponse>(OnResponse, false);
        public override void OnStopClient() => NetworkClient.UnregisterHandler<SteamAuthResponse>();

        public override void OnClientAuthenticate()
        {
            LastRefusal = null;
            if (NetworkServer.active) { ClientAccept(); return; } // the host's own client: accepted on the server side
            if (GameManager.Instance == null || GameManager.Instance.ActiveRoute.Kind != RouteKind.Steam)
            {
                // Direct (KCP): say which protocol this build speaks; the host answers with a SteamAuthResponse.
                NetworkClient.Send(new VersionHello { protocol = GameVersion.Protocol, version = GameVersion.Current });
                return;
            }
            if (!SteamServices.Available || !SteamServices.Backend.TryGetAuthTicket(out byte[] ticket)) { ClientReject(); return; }
            NetworkClient.Send(new SteamAuthRequest { version = GameVersion.Current, protocol = GameVersion.Protocol, steamId = SteamServices.Backend.LocalSteamId, ticket = ticket });
        }

        private void OnResponse(SteamAuthResponse msg)
        {
            if (msg.accepted) ClientAccept();
            else { WarLog.Warn(LogCategory.Steam, $"the host refused you: {msg.reason}"); LastRefusal = msg.reason; ClientReject(); }
        }
    }
}
