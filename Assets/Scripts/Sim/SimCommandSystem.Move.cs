using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    public partial class SimCommandSystem
    {
        private readonly List<int>[] moversByClass = { new List<int>(), new List<int>() };
        private readonly Dictionary<(int2 goal, byte stance, int sizeClass), List<int>> legs = new Dictionary<(int2, byte, int), List<int>>();
        private readonly Stack<List<int>> spareLegs = new Stack<List<int>>();

        /// <summary>
        /// Gives the owner's units (the listed ids, or every unit inside the box) an order. Move and
        /// AttackMove issue one order (one flow field) per size class present; Stop and Hold clear the
        /// order. A queued Move or AttackMove is appended to the <see cref="WaypointBook"/> of each unit
        /// that has a live order, and applies at once to the rest; any other order clears the units'
        /// waypoints. Ids that are not live or not the owner's are skipped. Reads the settled SoA, so it
        /// must run right after the boundary.
        /// </summary>
        private void ApplyOrder(SimData data, SimContext context, in SimCommand command, int unitCount)
        {
            foreach (List<int> list in moversByClass) list.Clear();
            bool moves = command.Order == OrderKind.Move || command.Order == OrderKind.AttackMove;
            byte stance = command.Order switch
            {
                OrderKind.Move => Stances.Move,
                OrderKind.AttackMove => Stances.AttackMove,
                OrderKind.Hold => Stances.Hold,
                _ => Stances.Idle,
            };
            bool queue = command.Queue && moves;
            if (command.Ids != null)
            {
                foreach (int id in command.Ids)
                {
                    int slot = data.SlotOf(id, unitCount);
                    if (slot < 0 || data.OwnerId[slot] != command.OwnerId || data.Health[slot] <= 0f) continue;
                    Route(data, context, slot, queue, command.Tile, stance);
                }
            }
            else
            {
                int2 lo = math.min(command.BoxMin, command.BoxMax), hi = math.max(command.BoxMin, command.BoxMax) + 1;
                for (int slot = 0; slot < unitCount; slot++)
                {
                    float2 p = data.Positions[slot];
                    if (data.OwnerId[slot] != command.OwnerId || data.Health[slot] <= 0f) continue;
                    if (p.x < lo.x || p.y < lo.y || p.x >= hi.x || p.y >= hi.y) continue;
                    Route(data, context, slot, queue, command.Tile, stance);
                }
            }

            if (!moves)
            {
                foreach (List<int> slots in moversByClass)
                    AddPending(data, slots, new PendingOrder { Slot = -1, Stance = stance });
                return;
            }
            int tick = SystemAPI.GetSingleton<SimClock>().Tick;
            for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
                Issue(data, context, moversByClass[sizeClass], sizeClass, command.Tile, stance, tick);
        }

        /// <summary>Queues the unit's waypoint, or clears its waypoints and adds it to this order's movers.</summary>
        private void Route(SimData data, SimContext context, int slot, bool queue, int2 goal, byte stance)
        {
            int id = data.IdOf[slot];
            if (queue && HasLiveOrder(data, slot, id))
            {
                context.Waypoints.Append(id, goal, stance);
                return;
            }
            context.Waypoints.Clear(id);
            moversByClass[data.SizeClass[slot] & 1].Add(slot);
        }

        /// <summary>True when the unit follows a live order, or was given one earlier in this boundary.</summary>
        private static bool HasLiveOrder(SimData data, int slot, int id)
        {
            if (data.PendingOrders.TryGetValue(id, out PendingOrder pending)) return pending.Slot >= 0;
            int order = data.OrderSlot[slot];
            if (data.Arrived[slot] != 0) return false; // it finished its order on the settled tick
            return (uint)order < (uint)data.OrderLive.Length && data.OrderLive[order] != 0;
        }

        /// <summary>
        /// Sends each unit that arrived last tick on to its next queued waypoint. Units popping the same
        /// leg (goal, stance and size class) share one order, so one flow field.
        /// </summary>
        private void AdvanceWaypoints(SimData data, SimContext context, int unitCount, int tick)
        {
            while (data.Arrivals.TryDequeue(out int id))
            {
                if (!context.Waypoints.TryPop(id, out int2 goal, out byte stance)) continue;
                int slot = data.SlotOf(id, unitCount);
                if (slot < 0 || data.Health[slot] <= 0f)
                {
                    context.Waypoints.Forget(id);
                    continue;
                }
                var key = (goal, stance, data.SizeClass[slot] & 1);
                if (!legs.TryGetValue(key, out List<int> slots))
                {
                    slots = spareLegs.Count > 0 ? spareLegs.Pop() : new List<int>();
                    legs[key] = slots;
                }
                slots.Add(slot);
            }
            foreach (KeyValuePair<(int2 goal, byte stance, int sizeClass), List<int>> leg in legs)
            {
                Issue(data, context, leg.Value, leg.Key.sizeClass, leg.Key.goal, leg.Key.stance, tick);
                leg.Value.Clear();
                spareLegs.Push(leg.Value);
            }
            legs.Clear();
        }

        /// <summary>Issues (or joins) the order for the goal and gives it to the units.</summary>
        private static void Issue(SimData data, SimContext context, List<int> slots, int sizeClass, int2 goal, byte stance, int tick)
        {
            if (slots.Count == 0) return;
            var starts = new NativeArray<int>(slots.Count, Allocator.Temp);
            for (int i = 0; i < slots.Count; i++)
            {
                int2 tile = math.clamp((int2)math.floor(data.Positions[slots[i]]), 0, new int2(data.Width - 1, data.Height - 1));
                starts[i] = tile.y * data.Width + tile.x;
            }
            int handle = context.Orders.Issue(goal, sizeClass, starts, tick);
            starts.Dispose();
            if (handle < 0) return;
            AddPending(data, slots, new PendingOrder { Slot = handle, Stance = stance });
        }

        private static void AddPending(SimData data, List<int> slots, PendingOrder order)
        {
            if (slots.Count == 0) return;
            NativeParallelHashMap<int, PendingOrder> pending = data.PendingOrders;
            if (pending.Capacity < pending.Count() + slots.Count) pending.Capacity = pending.Count() + slots.Count + 1024;
            foreach (int slot in slots) pending[data.IdOf[slot]] = order;
        }
    }
}
