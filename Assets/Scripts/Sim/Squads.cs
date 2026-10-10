using System.Collections.Generic;

namespace WAR2D.Sim
{
    /// <summary>
    /// Each player's ten server-side squads: lists of unit ids. Membership is not checked here; orders
    /// to a squad go through the simulation, which skips ids that are dead or not the owner's.
    /// </summary>
    public sealed class Squads
    {
        public const int Count = 10;
        private static readonly IReadOnlyList<int> Empty = new int[0];
        private readonly Dictionary<int, List<int>[]> byOwner = new Dictionary<int, List<int>[]>();

        /// <summary>Replaces a squad's members.</summary>
        public void Assign(int ownerId, int squad, IReadOnlyList<int> ids)
        {
            if ((uint)squad >= Count) return;
            if (!byOwner.TryGetValue(ownerId, out List<int>[] squads)) byOwner[ownerId] = squads = new List<int>[Count];
            squads[squad] = new List<int>(ids);
        }

        /// <summary>A squad's members (possibly including units that have since died).</summary>
        public IReadOnlyList<int> Members(int ownerId, int squad)
        {
            if ((uint)squad >= Count || !byOwner.TryGetValue(ownerId, out List<int>[] squads) || squads[squad] == null) return Empty;
            return squads[squad];
        }

        /// <summary>Drops a squad's members that are no longer live.</summary>
        public void Prune(int ownerId, int squad, NetIdAllocator ids)
        {
            if ((uint)squad >= Count || !byOwner.TryGetValue(ownerId, out List<int>[] squads) || squads[squad] == null) return;
            squads[squad].RemoveAll(id => !ids.IsLive(id));
        }

        /// <summary>
        /// Drops a squad's members for which <paramref name="ownsLive"/> is false (dead, gone, or not the
        /// owner's) and returns how many remain.
        /// </summary>
        public int PruneToOwned(int ownerId, int squad, System.Func<int, bool> ownsLive)
        {
            if ((uint)squad >= Count || !byOwner.TryGetValue(ownerId, out List<int>[] squads) || squads[squad] == null) return 0;
            squads[squad].RemoveAll(id => !ownsLive(id));
            return squads[squad].Count;
        }

        /// <summary>Owners with at least one squad assigned.</summary>
        public IEnumerable<int> Owners => byOwner.Keys;

        /// <summary>Forgets every squad of a player who left or was eliminated.</summary>
        public void Forget(int ownerId) => byOwner.Remove(ownerId);
    }
}
