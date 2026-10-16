using System;
using System.Collections.Generic;
using Mirror;

namespace WAR2D.Net.Steam
{
    public struct SteamAuthRequest : NetworkMessage { public string version; public ulong steamId; public byte[] ticket; }
    public struct SteamAuthResponse : NetworkMessage { public bool accepted; public string reason; }

    /// <summary>The rules a Steam sign-in must pass before Steam is even asked. Pure.</summary>
    public static class SteamAuthPolicy
    {
        public const int MaxTicketBytes = 1024;

        public static bool Check(string hostVersion, SteamAuthRequest req, ulong transportSteamId, out string reason)
        {
            if (!GameVersion.IsCompatible(hostVersion, req.version)) { reason = $"Wrong game version (host {hostVersion})."; return false; }
            if (transportSteamId == 0 || req.steamId != transportSteamId) { reason = "Steam identity does not match the connection."; return false; }
            if (req.ticket == null || req.ticket.Length == 0 || req.ticket.Length > MaxTicketBytes) { reason = "Bad Steam ticket."; return false; }
            reason = null;
            return true;
        }

        /// <summary>Direct (KCP, no Steam sign-in) connections: the editor, <c>-perf</c> runs, or <c>-direct</c>.</summary>
        public static bool DirectAllowed(bool isEditor, bool devApi, string[] commandLine) =>
            isEditor || devApi || Array.IndexOf(commandLine ?? new string[0], "-direct") >= 0;
    }

    /// <summary>Server-side bookkeeping of sign-ins waiting for Steam's verdict. Pure apart from the backend calls.</summary>
    public sealed class SteamAuthSessions
    {
        public enum Begin { Rejected, Pending }

        private readonly ISteamBackend backend;
        private readonly string hostVersion;
        private readonly double timeout;
        private readonly Dictionary<ulong, (int conn, double deadline)> pending = new Dictionary<ulong, (int, double)>();
        private readonly List<(int connectionId, ulong steamId, bool accepted, string reason)> finished = new List<(int, ulong, bool, string)>();

        public SteamAuthSessions(ISteamBackend backend, string hostVersion, double timeoutSeconds = 10)
        {
            this.backend = backend;
            this.hostVersion = hostVersion;
            timeout = timeoutSeconds;
            backend.AuthSessionResult += OnResult;
        }

        public Begin OnRequest(int connectionId, SteamAuthRequest req, ulong transportSteamId, double now, out string reason)
        {
            if (!SteamAuthPolicy.Check(hostVersion, req, transportSteamId, out reason)) return Begin.Rejected;
            if (pending.ContainsKey(req.steamId)) { reason = "Already signing in."; return Begin.Rejected; }
            if (!backend.BeginAuthSession(req.steamId, req.ticket)) { reason = "Steam refused the ticket."; return Begin.Rejected; }
            pending[req.steamId] = (connectionId, now + timeout);
            return Begin.Pending;
        }

        private void OnResult(ulong steamId, bool accepted)
        {
            if (!pending.TryGetValue(steamId, out var p)) return;
            pending.Remove(steamId);
            if (!accepted) backend.EndAuthSession(steamId);
            finished.Add((p.conn, steamId, accepted, accepted ? null : "Steam could not verify you."));
        }

        /// <summary>Verdicts since the last call; also times out sign-ins Steam never answered.</summary>
        public List<(int connectionId, bool accepted, string reason)> Drain(double now)
        {
            List<ulong> late = null;
            foreach (var kv in pending) if (now >= kv.Value.deadline) (late ??= new List<ulong>()).Add(kv.Key);
            if (late != null)
                foreach (ulong id in late)
                {
                    finished.Add((pending[id].conn, id, false, "Steam did not answer in time."));
                    pending.Remove(id);
                    backend.EndAuthSession(id);
                }
            var result = new List<(int connectionId, bool accepted, string reason)>(finished.Count);
            foreach (var f in finished) result.Add((f.connectionId, f.accepted, f.reason));
            finished.Clear();
            return result;
        }

        /// <summary>
        /// Drops a connection's sign-in (it disconnected or was rejected): a pending one, or a verdict not yet
        /// drained. Ends the Steam session it opened; a refused one was already ended.
        /// </summary>
        public void Forget(int connectionId)
        {
            ulong key = 0; bool found = false;
            foreach (var kv in pending) if (kv.Value.conn == connectionId) { key = kv.Key; found = true; break; }
            if (found)
            {
                pending.Remove(key);
                backend.EndAuthSession(key);
            }
            for (int i = finished.Count - 1; i >= 0; i--)
            {
                if (finished[i].connectionId != connectionId) continue;
                if (finished[i].accepted) backend.EndAuthSession(finished[i].steamId);
                finished.RemoveAt(i);
            }
        }

        public void Dispose() => backend.AuthSessionResult -= OnResult;
    }

    /// <summary>Server-side map from connection to verified Steam ID. Never sent to clients.</summary>
    public static class SteamIdentity
    {
        private static readonly Dictionary<int, ulong> Ids = new Dictionary<int, ulong>();
        public static void Set(int connectionId, ulong steamId) => Ids[connectionId] = steamId;
        public static bool TryGet(int connectionId, out ulong steamId) => Ids.TryGetValue(connectionId, out steamId);
        public static void Forget(int connectionId) => Ids.Remove(connectionId);
        public static void Clear() => Ids.Clear();
    }
}
