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
        private readonly Dictionary<int, Queue<(int2 goal, byte stance, byte work, int target)>> queues = new Dictionary<int, Queue<(int2, byte, byte, int)>>();
        private readonly Stack<Queue<(int2, byte, byte, int)>> spare = new Stack<Queue<(int2, byte, byte, int)>>();

        public WaypointBook(int maxQueued)
        {
            this.maxQueued = math.max(0, maxQueued);
        }

        /// <summary>Queues a waypoint after the unit's others; dropped when the unit already has the maximum.</summary>
        public void Append(int unitId, int2 goal, byte stance, byte work = WorkKinds.None, int target = 0)
        {
            if (maxQueued == 0) return;
            if (!queues.TryGetValue(unitId, out Queue<(int2, byte, byte, int)> queue))
            {
                queue = spare.Count > 0 ? spare.Pop() : new Queue<(int2, byte, byte, int)>(maxQueued);
                queues[unitId] = queue;
            }
            if (queue.Count < maxQueued) queue.Enqueue((goal, stance, work, target));
        }

        /// <summary>Takes the unit's next waypoint. False when it has none.</summary>
        public bool TryPop(int unitId, out int2 goal, out byte stance) => TryPop(unitId, out goal, out stance, out _);

        /// <summary>Takes the unit's next waypoint with the work kind it carries (<see cref="WorkKinds"/>). False when it has none.</summary>
        public bool TryPop(int unitId, out int2 goal, out byte stance, out byte work) => TryPop(unitId, out goal, out stance, out work, out _);

        /// <summary>As above, with an Attack order's target id (0 for any other order).</summary>
        public bool TryPop(int unitId, out int2 goal, out byte stance, out byte work, out int target)
        {
            goal = default;
            stance = Stances.Idle;
            work = WorkKinds.None;
            target = 0;
            if (!queues.TryGetValue(unitId, out Queue<(int2 goal, byte stance, byte work, int target)> queue)) return false;
            (goal, stance, work, target) = queue.Dequeue();
            if (queue.Count == 0) Release(unitId, queue);
            return true;
        }

        /// <summary>Drops the unit's waypoints (a non-queued order or Stop).</summary>
        public void Clear(int unitId)
        {
            if (queues.TryGetValue(unitId, out Queue<(int2, byte, byte, int)> queue)) Release(unitId, queue);
        }

        /// <summary>Drops a dead unit's waypoints.</summary>
        public void Forget(int unitId) => Clear(unitId);

        /// <summary>Waypoints queued for the unit.</summary>
        public int CountOf(int unitId) => queues.TryGetValue(unitId, out Queue<(int2, byte, byte, int)> queue) ? queue.Count : 0;

        private void Release(int unitId, Queue<(int2, byte, byte, int)> queue)
        {
            queues.Remove(unitId);
            queue.Clear();
            spare.Push(queue);
        }
    }
}
