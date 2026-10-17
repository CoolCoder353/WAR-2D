using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Client
{
    /// <summary>
    /// The goals the local client sent each of its units (the current order first, then the Shift-queued ones),
    /// for the numbered markers shown while Shift is held and the "Queue full" state (Figma <c>v0.12 Fixes</c>,
    /// <c>Orders – goal moved and queue full</c>). The server never sends order queues, so this is the client's
    /// own record: a goal drops off when the unit gets near it.
    /// </summary>
    public sealed class OrderQueueTracker
    {
        private readonly Dictionary<int, List<float2>> goals = new Dictionary<int, List<float2>>();
        private readonly List<int> gone = new List<int>();
        private static readonly List<float2> None = new List<float2>();

        /// <summary>Goals one unit can hold: its order plus <c>Orders/MaxQueued</c>.</summary>
        public int Limit { get; }

        public OrderQueueTracker(int maxQueued) => Limit = math.max(1, maxQueued + 1);

        /// <summary>An order with a goal was sent: queued ones are added (while there is room), others replace the list.</summary>
        public void Sent(IEnumerable<int> ids, float2 goal, bool queue)
        {
            foreach (int id in ids)
            {
                if (!goals.TryGetValue(id, out List<float2> list)) goals[id] = list = new List<float2>(Limit);
                if (!queue || list.Count == 0) { list.Clear(); list.Add(goal); }
                else if (list.Count < Limit) list.Add(goal);
            }
        }

        /// <summary>An order without a goal (Stop, Hold) was sent: the units have no goals left.</summary>
        public void Cleared(IEnumerable<int> ids)
        {
            foreach (int id in ids) goals.Remove(id);
        }

        /// <summary>The unit's goals, current first.</summary>
        public IReadOnlyList<float2> GoalsOf(int id) => goals.TryGetValue(id, out List<float2> list) ? list : None;

        /// <summary>True when a Shift-queued order would be dropped for the unit.</summary>
        public bool IsFull(int id) => goals.TryGetValue(id, out List<float2> list) && list.Count >= Limit;

        /// <summary>
        /// Forgets units <paramref name="positionOf"/> no longer knows and drops each unit's current goal once it is
        /// within <paramref name="reached"/> tiles of it (the next one becomes current).
        /// </summary>
        public void Prune(Func<int, float2?> positionOf, float reached)
        {
            gone.Clear();
            foreach (KeyValuePair<int, List<float2>> pair in goals)
            {
                float2? at = positionOf(pair.Key);
                if (at == null) { gone.Add(pair.Key); continue; }
                List<float2> list = pair.Value;
                while (list.Count > 0 && math.distance(at.Value, list[0]) <= reached) list.RemoveAt(0);
                if (list.Count == 0) gone.Add(pair.Key);
            }
            foreach (int id in gone) goals.Remove(id);
        }
    }
}
