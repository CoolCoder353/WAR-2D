using System.Collections.Generic;

namespace WAR2D.Net.Rejoin
{
    /// <summary>
    /// The connections a running match let in to present a rejoin token (v0.15), each with the time it must have its player by.
    /// A connection that never asks for its player would otherwise stay open for the rest of the match. Pure: the caller supplies
    /// the clock.
    /// </summary>
    public sealed class RejoinGrace
    {
        /// <summary>Seconds a connection let into a running match has to come back as a player before it is closed.</summary>
        public const double Seconds = 15.0;

        /// <summary>Seconds a refused connection is left open so its <see cref="RejoinRefused"/> arrives (the client normally leaves first).</summary>
        public const double RefusedSeconds = 2.0;

        private readonly Dictionary<int, double> deadline = new Dictionary<int, double>();

        /// <summary>How many connections are waiting.</summary>
        public int Count => deadline.Count;

        /// <summary>Starts the clock for a connection that was just let in (or refused, with <see cref="RefusedSeconds"/>).</summary>
        public void Track(int connectionId, double now, double seconds = Seconds) => deadline[connectionId] = now + seconds;

        /// <summary>The connection got its player, or is gone.</summary>
        public void Forget(int connectionId) => deadline.Remove(connectionId);

        /// <summary>Adds the connections whose time ran out to <paramref name="into"/> and stops tracking them.</summary>
        public void TakeExpired(double now, List<int> into)
        {
            int start = into.Count;
            foreach (KeyValuePair<int, double> pair in deadline)
                if (now > pair.Value) into.Add(pair.Key);
            for (int i = start; i < into.Count; i++) deadline.Remove(into[i]);
        }

        /// <summary>Forgets every connection (the server stopped).</summary>
        public void Clear() => deadline.Clear();
    }
}
