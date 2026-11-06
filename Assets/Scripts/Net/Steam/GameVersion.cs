namespace WAR2D.Net.Steam
{
    /// <summary>
    /// The build's version, compared exactly between host and client over Steam (the wire format changes between
    /// builds), and the wire-protocol number every connection (Steam and Direct) must match.
    /// </summary>
    public static class GameVersion
    {
        /// <summary>
        /// The wire-protocol version: bump it whenever a message, the replication format or a recording changes
        /// shape. Host and client must match exactly; Direct (KCP) joins check only this, Steam joins this and
        /// <see cref="Current"/>. 13: v0.13 (building attack events, attacker kinds). 14: v0.14 (AI players, lobby
        /// Visibility, the end screen's AI styles). 15: v0.15.1 (reconnecting: rejoin, leave and refusal messages, the
        /// producers resent on rejoin). 16: v0.16 (owner wall targets in attack events, the order formation flag). 17: v0.17 (the
        /// rich gem tile, a new terrain value). 19: the host-leaving message, and owed upkeep with an unpaid flag in the resources update.
        /// 20: the attack flag on a rally point. 21: route waypoints in half tiles.
        /// </summary>
        public const int Protocol = 21;

        public static string Current => UnityEngine.Application.version;

        public static bool IsCompatible(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a == b;

        /// <summary>The refusal shown to a client whose build speaks another protocol or version.</summary>
        public static string MismatchReason(string hostVersion, int hostProtocol) =>
            $"Different game version: the host runs {hostVersion} (protocol {hostProtocol}). Both players need the same build.";
    }
}
