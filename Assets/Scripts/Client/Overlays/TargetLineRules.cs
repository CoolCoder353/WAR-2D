using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Net.Replication;

namespace WAR2D.Client.Overlays
{
    /// <summary>What a target line points at: a unit or building (red) or a wall (orange).</summary>
    public enum TargetLineKind : byte { Attack, Wall }

    /// <summary>
    /// The pure rules behind target lines (v0.16, Figma T): each attacker's last named target and when it fired, how long a
    /// line holds after the last shot, which kind it is, and which lines survive the cap. No Unity objects.
    /// </summary>
    public sealed class TargetLineRules
    {
        private readonly Dictionary<int, (int target, float at)> last = new Dictionary<int, (int, float)>();
        private readonly List<int> stale = new List<int>();
        private readonly List<int> kept = new List<int>();
        private float[] sortKeys = new float[0];
        private (int id, float2 at)[] sortItems = new (int, float2)[0];
        private readonly float hold;

        /// <summary>Most lines drawn at once.</summary>
        public int Max { get; }

        public TargetLineRules(float holdSeconds, int max) { hold = holdSeconds; Max = max; }

        /// <summary>An attack event: <paramref name="attackerId"/> fired at <paramref name="targetId"/> (0 when unnamed).</summary>
        public void Shot(int attackerId, int targetId, float now)
        {
            if (targetId == 0) return;
            last[attackerId] = (targetId, now);
        }

        /// <summary>The attacker's target while its line holds.</summary>
        public bool TryTarget(int attackerId, float now, out int targetId)
        {
            targetId = 0;
            if (!last.TryGetValue(attackerId, out var shot) || now - shot.at > hold) return false;
            targetId = shot.target;
            return true;
        }

        /// <summary>Drops attackers whose lines expired (call once a frame).</summary>
        public void Forget(float now)
        {
            stale.Clear();
            foreach (var pair in last) if (now - pair.Value.at > hold) stale.Add(pair.Key);
            foreach (int id in stale) last.Remove(id);
        }

        /// <summary>Red for units and buildings, orange for walls.</summary>
        public static TargetLineKind KindOf(int targetId) => AttackTargetIds.IsWall(targetId) ? TargetLineKind.Wall : TargetLineKind.Attack;

        /// <summary>
        /// Sorts <paramref name="from"/> nearest <paramref name="centre"/> first and returns the indices of the first
        /// <paramref name="max"/> (into the sorted list). The returned list is reused.
        /// </summary>
        public List<int> Nearest(List<(int id, float2 at)> from, float2 centre, int max)
        {
            int n = from.Count;
            if (sortKeys.Length < n) { sortKeys = new float[n]; sortItems = new (int, float2)[n]; }
            for (int i = 0; i < n; i++) { sortKeys[i] = math.distancesq(from[i].at, centre); sortItems[i] = from[i]; }
            System.Array.Sort(sortKeys, sortItems, 0, n);
            for (int i = 0; i < n; i++) from[i] = sortItems[i];
            kept.Clear();
            for (int i = 0; i < from.Count && i < max; i++) kept.Add(i);
            return kept;
        }
    }
}
