using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Server-side queued waypoints per unit id (Shift-queued Move and Attack-move orders). Main thread
    /// only, touched at the tick boundary: <see cref="SimCommandSystem"/> appends to it and pops a unit's
    /// next waypoint when the unit arrives.
    /// </summary>
    public sealed class WaypointBook
    {
        private readonly int maxQueued;
        private readonly Dictionary<int, Queue<(int2 goal, byte stance)>> queues = new Dictionary<int, Queue<(int2, byte)>>();
        private readonly Stack<Queue<(int2, byte)>> spare = new Stack<Queue<(int2, byte)>>();

        public WaypointBook(int maxQueued)
        {
            this.maxQueued = math.max(0, maxQueued);
        }

        /// <summary>Queues a waypoint after the unit's others; dropped when the unit already has the maximum.</summary>
        public void Append(int unitId, int2 goal, byte stance)
        {
            if (maxQueued == 0) return;
            if (!queues.TryGetValue(unitId, out Queue<(int2, byte)> queue))
            {
                queue = spare.Count > 0 ? spare.Pop() : new Queue<(int2, byte)>(maxQueued);
                queues[unitId] = queue;
            }
            if (queue.Count < maxQueued) queue.Enqueue((goal, stance));
        }

        /// <summary>Takes the unit's next waypoint. False when it has none.</summary>
        public bool TryPop(int unitId, out int2 goal, out byte stance)
        {
            goal = default;
            stance = Stances.Idle;
            if (!queues.TryGetValue(unitId, out Queue<(int2 goal, byte stance)> queue)) return false;
            (goal, stance) = queue.Dequeue();
            if (queue.Count == 0) Release(unitId, queue);
            return true;
        }

        /// <summary>Drops the unit's waypoints (a non-queued order or Stop).</summary>
        public void Clear(int unitId)
        {
            if (queues.TryGetValue(unitId, out Queue<(int2, byte)> queue)) Release(unitId, queue);
        }

        /// <summary>Drops a dead unit's waypoints.</summary>
        public void Forget(int unitId) => Clear(unitId);

        /// <summary>Waypoints queued for the unit.</summary>
        public int CountOf(int unitId) => queues.TryGetValue(unitId, out Queue<(int2, byte)> queue) ? queue.Count : 0;

        private void Release(int unitId, Queue<(int2, byte)> queue)
        {
            queues.Remove(unitId);
            queue.Clear();
            spare.Push(queue);
        }
    }
}
