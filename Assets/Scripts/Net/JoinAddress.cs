using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WAR2D.Net
{
    /// <summary>
    /// Parses what a player types to join: a host (IPv4, IPv6 or DNS name), optionally with a port
    /// (<c>host:port</c>, or <c>[ipv6]:port</c>). A tunnel or a port forward to another external port gives
    /// the host an address with a port other than the default.
    /// </summary>
    public static class JoinAddress
    {
        /// <summary>
        /// Splits <paramref name="text"/> into a host and a port (<paramref name="defaultPort"/> when none is
        /// given). False for an empty or malformed address, or a port outside 1..65535.
        /// </summary>
        public static bool TryParse(string text, ushort defaultPort, out string host, out ushort port)
        {
            host = null;
            port = defaultPort;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return false;

            if (text[0] == '[')
            {
                int close = text.IndexOf(']');
                if (close < 0) return false;
                host = text.Substring(1, close - 1);
                string rest = text.Substring(close + 1);
                if (rest.Length > 0 && (rest[0] != ':' || !TryPort(rest.Substring(1), out port))) return false;
                return IPAddress.TryParse(host, out IPAddress v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
            }

            int colon = text.IndexOf(':');
            if (colon >= 0 && colon == text.LastIndexOf(':'))
            {
                if (!TryPort(text.Substring(colon + 1), out port)) return false;
                host = text.Substring(0, colon);
            }
            else host = text; // no port, or a bare IPv6 address (several colons)
            return IsHostValid(host);
        }

        /// <summary>True for an IPv4/IPv6 address or a DNS host name (no port).</summary>
        public static bool IsHostValid(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 253) return false;
            if (IPAddress.TryParse(host, out IPAddress ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? host.Split('.').Length == 4 : true;
            return Uri.CheckHostName(host) == UriHostNameType.Dns && !host.StartsWith(".") && !host.EndsWith(".");
        }

        private static bool TryPort(string text, out ushort port) =>
            ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port != 0;
    }
}
