using System;
using System.Collections.Generic;

namespace WAR2D.Net.Steam
{
    /// <summary>One lobby as the browser shows it, and the metadata keys the host publishes.</summary>
    public struct LobbyInfo : IEquatable<LobbyInfo>
    {
        public const string KeyVersion = "ver", KeyState = "state", KeyMode = "mode", KeyMapSize = "size", KeyHostName = "host";
        /// <summary>The server's name (playtest 2026-11-02); optional, so older hosts still list.</summary>
        public const string KeyServerName = "name";

        public ulong LobbyId, HostSteamId;
        public string HostName, Version, State, Mode;
        /// <summary>The server's name as the host set it, or "&lt;host&gt;'s server".</summary>
        public string ServerName;
        public int MapSize, Players;

        /// <summary>The key/value pairs the host sets on the Steam lobby.</summary>
        public Dictionary<string, string> Encode() => new Dictionary<string, string>
        {
            [KeyVersion] = Version, [KeyState] = State, [KeyMode] = Mode,
            [KeyMapSize] = MapSize.ToString(System.Globalization.CultureInfo.InvariantCulture), [KeyHostName] = HostName,
            [KeyServerName] = ServerName ?? "",
        };

        /// <summary>Reads lobby data a stranger published. Anything missing or malformed is rejected; the name is sanitised.</summary>
        public static bool TryDecode(ulong lobbyId, ulong hostSteamId, int players, IReadOnlyDictionary<string, string> data, out LobbyInfo info)
        {
            info = default;
            if (data == null) return false;
            if (!data.TryGetValue(KeyVersion, out string ver) || string.IsNullOrEmpty(ver)) return false;
            if (!data.TryGetValue(KeyState, out string state) || string.IsNullOrEmpty(state)) return false;
            if (!data.TryGetValue(KeyMode, out string mode) || string.IsNullOrEmpty(mode)) return false;
            if (!data.TryGetValue(KeyMapSize, out string sizeText)
                || !int.TryParse(sizeText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int size)) return false;
            if (!data.TryGetValue(KeyHostName, out string rawName) || !CommandValidator.TrySanitizeNickname(rawName, out string name)) return false;
            string serverName = data.TryGetValue(KeyServerName, out string rawServer) && CommandValidator.TrySanitizeNickname(rawServer, out string cleanServer)
                ? cleanServer : null;
            info = new LobbyInfo
            {
                LobbyId = lobbyId, HostSteamId = hostSteamId, HostName = name, Version = ver, State = state,
                Mode = mode, MapSize = size, Players = players, ServerName = WAR2D.Net.ServerNames.Display(serverName, name),
            };
            return true;
        }

        public bool Equals(LobbyInfo o) =>
            LobbyId == o.LobbyId && HostSteamId == o.HostSteamId && HostName == o.HostName && ServerName == o.ServerName && Version == o.Version
            && State == o.State && Mode == o.Mode && MapSize == o.MapSize && Players == o.Players;
        public override bool Equals(object obj) => obj is LobbyInfo o && Equals(o);
        public override int GetHashCode() => LobbyId.GetHashCode();
    }
}
