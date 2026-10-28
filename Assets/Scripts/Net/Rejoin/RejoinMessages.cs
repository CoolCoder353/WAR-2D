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
    /// Sent by a client that leaves a running match on purpose, just before it disconnects: the server tears up its ticket, so
    /// its seat is not kept for a rejoin, and closes the connection (which the client waits for, so the message is sent).
    /// </summary>
    public struct LeaveMatch : NetworkMessage
    {
    }

    /// <summary>Why the server will not give a seat back (v0.15.1, Figma <c>v0.15.1 Hotkeys and polish</c> R1).</summary>
    public enum RejoinRefusal : byte
    {
        /// <summary>The server does not know the ticket (a new match, or a ticket replaced by a later one).</summary>
        Unknown,
        /// <summary>The away window ran out.</summary>
        TimedOut,
        /// <summary>The player's HQ fell while they were away.</summary>
        Eliminated,
        /// <summary>The server removed the player for flooding commands.</summary>
        Kicked,
        /// <summary>The player left the match on purpose.</summary>
        Left,
        /// <summary>The match is no longer being played.</summary>
        MatchOver,
    }

    /// <summary>
    /// Sent to a connection whose rejoin the server refuses, before it closes it: the client stops offering Rejoin and says why. The
    /// client disconnects on receipt; the server closes the connection shortly after if it has not (<see cref="RejoinGrace.RefusedSeconds"/>).
    /// </summary>
    public struct RejoinRefused : NetworkMessage
    {
        public RejoinRefusal Reason;
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

        /// <summary>Why the server refused the last rejoin (the Play screen shows it until dismissed), or null.</summary>
        public static RejoinRefusal? Refusal { get; private set; }

        private static float droppedAt;

        /// <summary>Remembers how the client joined (called by every connect).</summary>
        public static void Remember(Steam.ConnectionRoute route) => Route = route;

        /// <summary>The server's ticket for this client.</summary>
        public static void Set(string token, float window)
        {
            Token = token;
            Window = window;
            Dropped = false; // a fresh ticket means the client is in the match
            Refusal = null;
        }

        /// <summary>The server refused the rejoin: the ticket is dead, so Rejoin is no longer offered, and the reason is kept to show.</summary>
        public static void Refuse(RejoinRefusal reason)
        {
            Clear();
            Refusal = reason;
        }

        /// <summary>The player read the refusal (the Play screen's Dismiss).</summary>
        public static void DismissRefusal() => Refusal = null;

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
