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
        private readonly Dictionary<int, List<bool>> work = new Dictionary<int, List<bool>>();
        private readonly Dictionary<int, float> stoppedSince = new Dictionary<int, float>();
        private readonly List<int> gone = new List<int>();

        /// <summary>
        /// Seconds a unit must stand still before its current move goal counts as reached (playtest 2026-11-02: units that
        /// stop further than the reach from their goal, in a formation slot or a packed group, kept a stale goal).
        /// </summary>
        public const float StoppedSeconds = 1f;
        private static readonly List<float2> None = new List<float2>();

        /// <summary>Goals one unit can hold: its order plus <c>Orders/MaxQueued</c>.</summary>
        public int Limit { get; }

        public OrderQueueTracker(int maxQueued) => Limit = math.max(1, maxQueued + 1);

        /// <summary>
        /// An order with a goal was sent: queued ones are added (while there is room), others replace the list.
        /// <paramref name="isWork"/> marks a Build, Repair or Dig job, which the unit does standing still.
        /// </summary>
        public void Sent(IEnumerable<int> ids, float2 goal, bool queue, bool isWork = false)
        {
            foreach (int id in ids)
            {
                if (!goals.TryGetValue(id, out List<float2> list)) goals[id] = list = new List<float2>(Limit);
                if (!work.TryGetValue(id, out List<bool> kinds)) work[id] = kinds = new List<bool>(Limit);
                if (!queue || list.Count == 0) { list.Clear(); kinds.Clear(); list.Add(goal); kinds.Add(isWork); stoppedSince.Remove(id); }
                else if (list.Count < Limit) { list.Add(goal); kinds.Add(isWork); }
            }
        }

        /// <summary>An order without a goal (Stop, Hold) was sent: the units have no goals left.</summary>
        public void Cleared(IEnumerable<int> ids)
        {
            foreach (int id in ids) Forget(id);
        }

        private void Forget(int id)
        {
            goals.Remove(id);
            work.Remove(id);
            stoppedSince.Remove(id);
        }

        /// <summary>The unit's goals, current first.</summary>
        public IReadOnlyList<float2> GoalsOf(int id) => goals.TryGetValue(id, out List<float2> list) ? list : None;

        /// <summary>True when a Shift-queued order would be dropped for the unit.</summary>
        public bool IsFull(int id) => goals.TryGetValue(id, out List<float2> list) && list.Count >= Limit;

        /// <summary>
        /// Forgets units <paramref name="positionOf"/> no longer knows and drops each unit's current goal once it is
        /// within <paramref name="reached"/> tiles of it (the next one becomes current).
        /// </summary>
        public void Prune(Func<int, float2?> positionOf, float reached) => Prune(positionOf, reached, null, 0f);

        /// <summary>
        /// As <see cref="Prune(Func{int, float2?}, float)"/>; with <paramref name="isMoving"/>, a unit that has stood still for
        /// <see cref="StoppedSeconds"/> has also reached its current goal unless that goal is a job (it works standing still).
        /// </summary>
        public void Prune(Func<int, float2?> positionOf, float reached, Func<int, bool> isMoving, float now)
        {
            gone.Clear();
            foreach (KeyValuePair<int, List<float2>> pair in goals)
            {
                int id = pair.Key;
                float2? at = positionOf(id);
                if (at == null) { gone.Add(id); continue; }
                List<float2> list = pair.Value;
                List<bool> kinds = work[id];
                bool stopped = false;
                if (isMoving != null)
                {
                    if (isMoving(id)) stoppedSince.Remove(id);
                    else if (!stoppedSince.TryGetValue(id, out float since)) stoppedSince[id] = now;
                    else stopped = now - since >= StoppedSeconds;
                }
                while (list.Count > 0 && (math.distance(at.Value, list[0]) <= reached || (stopped && !kinds[0])))
                {
                    list.RemoveAt(0);
                    kinds.RemoveAt(0);
                    stoppedSince.Remove(id);
                    stopped = false; // the next goal gets its own second
                }
                if (list.Count == 0) gone.Add(id);
            }
            foreach (int id in gone) Forget(id);
        }
    }
}
