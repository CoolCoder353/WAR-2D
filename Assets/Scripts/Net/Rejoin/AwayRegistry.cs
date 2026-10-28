using System;
using System.Collections.Generic;

namespace WAR2D.Net.Rejoin
{
    /// <summary>
    /// The server's book of rejoin tickets (v0.15). Each human player in a running match holds one secret token; when
    /// the player drops, their seat goes "away" until a deadline, and a connection that presents the token before it
    /// gets the seat back. Pure: the caller supplies the clock (the match's network time).
    /// </summary>
    public sealed class AwayRegistry
    {
        private readonly Dictionary<string, int> ownerByToken = new Dictionary<string, int>();
        private readonly Dictionary<int, string> tokenByOwner = new Dictionary<int, string>();
        private readonly Dictionary<int, double> awayUntil = new Dictionary<int, double>();
        private readonly Func<string> makeToken;

        /// <param name="makeToken">Makes a fresh unguessable token (a GUID by default).</param>
        public AwayRegistry(Func<string> makeToken = null) => this.makeToken = makeToken ?? (() => Guid.NewGuid().ToString("N"));

        /// <summary>The owners currently away.</summary>
        public IEnumerable<int> Away => awayUntil.Keys;

        /// <summary>How many seats are away.</summary>
        public int AwayCount => awayUntil.Count;

        /// <summary>Gives the owner a new ticket (replacing the old one) and returns its token.</summary>
        public string Issue(int ownerId)
        {
            Revoke(ownerId);
            string token = makeToken();
            ownerByToken[token] = ownerId;
            tokenByOwner[ownerId] = token;
            return token;
        }

        /// <summary>Forgets the owner's ticket and away state (they left for good).</summary>
        public void Revoke(int ownerId)
        {
            if (tokenByOwner.Remove(ownerId, out string token)) ownerByToken.Remove(token);
            awayUntil.Remove(ownerId);
        }

        /// <summary>Forgets every ticket and away seat (a new match, or the server stopped).</summary>
        public void RevokeAll()
        {
            ownerByToken.Clear();
            tokenByOwner.Clear();
            awayUntil.Clear();
        }

        /// <summary>The owner's token, or null.</summary>
        public string TokenOf(int ownerId) => tokenByOwner.TryGetValue(ownerId, out string t) ? t : null;

        /// <summary>Marks the owner away until <paramref name="now"/> + <paramref name="seconds"/>. False without a ticket or with no window.</summary>
        public bool MarkAway(int ownerId, double now, float seconds)
        {
            if (seconds <= 0f || !tokenByOwner.ContainsKey(ownerId)) return false;
            awayUntil[ownerId] = now + seconds;
            return true;
        }

        /// <summary>True while the owner is away.</summary>
        public bool IsAway(int ownerId) => awayUntil.ContainsKey(ownerId);

        /// <summary>When the owner's window closes (match time), or 0 when not away.</summary>
        public double UntilOf(int ownerId) => awayUntil.TryGetValue(ownerId, out double until) ? until : 0.0;

        /// <summary>
        /// A connection presents a token: true with the owner it belongs to when that owner is away and the window is still
        /// open. The seat stays away until <see cref="Return"/>.
        /// </summary>
        public bool TryClaim(string token, double now, out int ownerId)
        {
            ownerId = 0;
            if (string.IsNullOrEmpty(token) || !ownerByToken.TryGetValue(token, out int owner)) return false;
            if (!awayUntil.TryGetValue(owner, out double until) || now > until) return false;
            ownerId = owner;
            return true;
        }

        /// <summary>
        /// The owner a token belongs to, whether or not they are away (a player coming back before the server noticed the
        /// drop presents a token for a seat that still has its old connection). False for an unknown token.
        /// </summary>
        public bool TryOwnerOf(string token, out int ownerId)
        {
            ownerId = 0;
            return !string.IsNullOrEmpty(token) && ownerByToken.TryGetValue(token, out ownerId);
        }

        /// <summary>How many tickets are out (a running match takes new connections only while some are).</summary>
        public int TicketCount => tokenByOwner.Count;

        /// <summary>The owner is back: the away state ends and the old token is replaced by a new one, which is returned.</summary>
        public string Return(int ownerId)
        {
            awayUntil.Remove(ownerId);
            return Issue(ownerId);
        }

        /// <summary>Adds the owners whose window has closed to <paramref name="into"/>.</summary>
        public void CollectExpired(double now, List<int> into)
        {
            foreach (KeyValuePair<int, double> pair in awayUntil)
                if (now > pair.Value) into.Add(pair.Key);
        }
    }
}
