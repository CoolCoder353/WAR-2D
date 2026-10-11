namespace WAR2D.Net.Steam
{
    /// <summary>How a client reaches a host.</summary>
    public enum RouteKind : byte { Direct, Steam }

    /// <summary>Where a client connects: an address and port (KCP), or a host's Steam ID (Steam relay).</summary>
    public readonly struct ConnectionRoute
    {
        public RouteKind Kind { get; }
        public string Host { get; }
        public ushort Port { get; }
        public ulong SteamId { get; }

        private ConnectionRoute(RouteKind kind, string host, ushort port, ulong steamId)
        {
            Kind = kind; Host = host; Port = port; SteamId = steamId;
        }

        public static ConnectionRoute ForAddress(string host, ushort port) => new ConnectionRoute(RouteKind.Direct, host, port, 0);
        public static ConnectionRoute ForSteam(ulong steamId) => new ConnectionRoute(RouteKind.Steam, null, 0, steamId);

        /// <summary><c>steam:&lt;id&gt;</c> is a Steam route; anything else must be a valid <see cref="JoinAddress"/>.</summary>
        public static bool TryParse(string text, ushort defaultPort, out ConnectionRoute route)
        {
            route = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith("steam:", System.StringComparison.OrdinalIgnoreCase))
            {
                string digits = text.Substring(6);
                if (digits.Length == 0 || digits[0] == '+' || digits[0] == '-') return false;
                if (!ulong.TryParse(digits, out ulong id) || id == 0) return false;
                route = ForSteam(id);
                return true;
            }
            if (!JoinAddress.TryParse(text, defaultPort, out string host, out ushort port)) return false;
            route = ForAddress(host, port);
            return true;
        }

        public override string ToString() => Kind == RouteKind.Steam ? $"steam:{SteamId}" : $"{Host}:{Port}";
    }
}
