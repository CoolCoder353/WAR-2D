using Mirror;

namespace WAR2D.Net.Rejoin
{
    /// <summary>
    /// Sent by a client that is coming back to a running match, right after it connects: the token its ticket holds. The server
    /// gives the connection the away seat that token belongs to, or closes it.
    /// </summary>
    public struct RejoinRequest : NetworkMessage
    {
        public string Token;
    }

    /// <summary>
    /// What a client keeps so it can come back to a match it dropped out of (in memory only: it does not survive closing the
    /// game). The server sends a fresh token at match start and after every rejoin.
    /// </summary>
    public static class RejoinTicket
    {
        /// <summary>The secret token, or null when there is no ticket.</summary>
        public static string Token { get; private set; }

        /// <summary>How long the server gives a dropped player to come back, in seconds.</summary>
        public static float Window { get; private set; }

        /// <summary>The route the client connected by, to connect again the same way.</summary>
        public static Steam.ConnectionRoute? Route { get; private set; }

        /// <summary>True when this client dropped out unasked (not by leaving) and may still be inside the window.</summary>
        public static bool Dropped { get; private set; }

        private static float droppedAt;

        /// <summary>Remembers how the client joined (called by every connect).</summary>
        public static void Remember(Steam.ConnectionRoute route) => Route = route;

        /// <summary>The server's ticket for this client.</summary>
        public static void Set(string token, float window)
        {
            Token = token;
            Window = window;
            Dropped = false; // a fresh ticket means the client is in the match
        }

        /// <summary>The connection broke while a match was running (not the player leaving): start the clock.</summary>
        public static void MarkDropped(float now)
        {
            if (string.IsNullOrEmpty(Token) || Window <= 0f) return;
            if (Dropped) return; // a failed or cancelled rejoin attempt disconnects too: the window keeps its first start
            Dropped = true;
            droppedAt = now;
        }

        /// <summary>Seconds left to rejoin; 0 when there is nothing to rejoin.</summary>
        public static float SecondsLeft(float now) => Dropped ? System.Math.Max(0f, Window - (now - droppedAt)) : 0f;

        /// <summary>True while a Rejoin is on offer.</summary>
        public static bool Available(float now) => SecondsLeft(now) > 0f;

        /// <summary>The player left on purpose, the window closed or the match is over: forget everything.</summary>
        public static void Clear()
        {
            Token = null;
            Dropped = false;
            Window = 0f;
        }
    }
}
