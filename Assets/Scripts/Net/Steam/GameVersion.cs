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
        /// Visibility, the end screen's AI styles).
        /// </summary>
        public const int Protocol = 14;

        public static string Current => UnityEngine.Application.version;

        public static bool IsCompatible(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a == b;

        /// <summary>The refusal shown to a client whose build speaks another protocol or version.</summary>
        public static string MismatchReason(string hostVersion, int hostProtocol) =>
            $"Different game version: the host runs {hostVersion} (protocol {hostProtocol}). Both players need the same build.";
    }
}
