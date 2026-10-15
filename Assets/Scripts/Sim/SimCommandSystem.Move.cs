using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    public partial class SimCommandSystem
    {
        private readonly List<int>[] moversByClass = { new List<int>(), new List<int>() };
        private readonly Dictionary<(int2 goal, byte stance, int sizeClass, int owner), List<int>> legs = new Dictionary<(int2, byte, int, int), List<int>>();
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
            if (command.Order == OrderKind.Build || command.Order == OrderKind.Repair || command.Order == OrderKind.Dig)
            {
                ApplyWorkOrder(data, context, command, unitCount);
                return;
            }
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
                Issue(data, context, moversByClass[sizeClass], sizeClass, command.Tile, stance, tick, command.OwnerId);
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

        /// <summary>True when the unit follows a live order, works on a target, or was given an order earlier in this boundary.</summary>
        private static bool HasLiveOrder(SimData data, int slot, int id)
        {
            if (data.WorkKind[slot] != WorkKinds.None && !data.PendingOrders.ContainsKey(id)) return true;
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
                int arrivedSlot = data.SlotOf(id, unitCount);
                // A worker that arrived at its job moves on only when the job is done (see ClearFinishedWork).
                if (arrivedSlot >= 0 && data.WorkKind[arrivedSlot] != WorkKinds.None && !data.PendingOrders.ContainsKey(id)) continue;
                if (!context.Waypoints.TryPop(id, out int2 goal, out byte stance, out byte work)) continue;
                int slot = arrivedSlot;
                if (slot < 0 || data.Health[slot] <= 0f)
                {
                    context.Waypoints.Forget(id);
                    continue;
                }
                if (work != WorkKinds.None)
                {
                    StartQueuedWork(data, context, slot, goal, work, tick);
                    continue;
                }
                var key = (goal, stance, data.SizeClass[slot] & 1, data.OwnerId[slot]);
                if (!legs.TryGetValue(key, out List<int> slots))
                {
                    slots = spareLegs.Count > 0 ? spareLegs.Pop() : new List<int>();
                    legs[key] = slots;
                }
                slots.Add(slot);
            }
            foreach (KeyValuePair<(int2 goal, byte stance, int sizeClass, int owner), List<int>> leg in legs)
            {
                Issue(data, context, leg.Value, leg.Key.sizeClass, leg.Key.goal, leg.Key.stance, tick, leg.Key.owner);
                leg.Value.Clear();
                spareLegs.Push(leg.Value);
            }
            legs.Clear();
        }

        /// <summary>Issues (or joins) the order for the goal and gives it to the units.</summary>
        private static void Issue(SimData data, SimContext context, List<int> slots, int sizeClass, int2 goal, byte stance, int tick, int owner)
        {
            if (slots.Count == 0) return;
            var starts = new NativeArray<int>(slots.Count, Allocator.Temp);
            for (int i = 0; i < slots.Count; i++)
            {
                int2 tile = math.clamp((int2)math.floor(data.Positions[slots[i]]), 0, new int2(data.Width - 1, data.Height - 1));
                starts[i] = tile.y * data.Width + tile.x;
            }
            int handle = context.Orders.Issue(goal, sizeClass, starts, tick, owner);
            starts.Dispose();
            if (handle < 0) return;
            AddPending(data, slots, new PendingOrder { Slot = handle, Stance = stance });
        }

        /// <summary>
        /// Gives the listed units of the right type (Builders for Build and Repair, Diggers for Dig) a work
        /// target at the order's tile, and a move to a floor tile next to it. Others are left alone. Shift
        /// queues the job behind the unit's current one.
        /// </summary>
        private void ApplyWorkOrder(SimData data, SimContext context, in SimCommand command, int unitCount)
        {
            if (command.Ids == null || !context.Map.Grid.Contains(command.Tile)) return;
            UnitType worker = command.Order == OrderKind.Dig ? UnitType.Digger : UnitType.Builder;
            byte kind = WorkKindOf(command.Order);
            int2 standGoal = command.Tile;
            if (command.Order == OrderKind.Dig)
            {
                if (context.Map.Grid.TileAt(command.Tile) == TileType.Border) return;
            }
            else if (!TryWorkTarget(data, context, command.OwnerId, command.Order, command.Tile, out standGoal)) return;
            foreach (List<int> list in moversByClass) list.Clear();
            foreach (int id in command.Ids)
            {
                int slot = data.SlotOf(id, unitCount);
                if (slot < 0 || data.OwnerId[slot] != command.OwnerId || data.Health[slot] <= 0f || data.Type[slot] != (byte)worker) continue;
                if (command.Queue && HasLiveOrder(data, slot, id))
                {
                    context.Waypoints.Append(id, command.Tile, Stances.Move, kind);
                    continue;
                }
                context.Waypoints.Clear(id);
                moversByClass[data.SizeClass[slot] & 1].Add(slot);
            }
            int tick = SystemAPI.GetSingleton<SimClock>().Tick;
            int tileIndex = context.Map.Grid.Index(command.Tile);
            for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
            {
                if (command.Order == OrderKind.Dig) // the dig stage asks for the move to the standing tile on its first tick
                    AddPending(data, moversByClass[sizeClass], new PendingOrder { Slot = -1, Stance = Stances.Idle, WorkTile = tileIndex, WorkKind = kind });
                else IssueWork(data, context, moversByClass[sizeClass], sizeClass, standGoal, tick, command.OwnerId, tileIndex, kind);
            }
        }

        private static byte WorkKindOf(OrderKind order) =>
            order == OrderKind.Build ? WorkKinds.Build : order == OrderKind.Repair ? WorkKinds.Repair : WorkKinds.Dig;

        private static OrderKind OrderOf(byte work) =>
            work == WorkKinds.Build ? OrderKind.Build : work == WorkKinds.Repair ? OrderKind.Repair : OrderKind.Dig;

        private readonly List<int> oneScratch = new List<int>(1);

        /// <summary>A one-unit slot list, reused (each use is done before the next call).</summary>
        private List<int> One(int slot)
        {
            oneScratch.Clear();
            oneScratch.Add(slot);
            return oneScratch;
        }

        /// <summary>Starts a queued job for one unit whose last one finished.</summary>
        private void StartQueuedWork(SimData data, SimContext context, int slot, int2 tile, byte work, int tick)
        {
            int owner = data.OwnerId[slot];
            if (!context.Map.Grid.Contains(tile)) return;
            List<int> one = One(slot);
            if (work == WorkKinds.Dig)
            {
                if (context.Map.Grid.TileAt(tile) == TileType.Border) return;
                AddPending(data, one, new PendingOrder { Slot = -1, Stance = Stances.Idle, WorkTile = context.Map.Grid.Index(tile), WorkKind = work });
                return;
            }
            if (!TryWorkTarget(data, context, owner, OrderOf(work), tile, out int2 standGoal)) return;
            IssueWork(data, context, one, data.SizeClass[slot] & 1, standGoal, tick, owner, context.Map.Grid.Index(tile), work);
        }

        /// <summary>
        /// Checks the order's target and finds the floor tile its workers head for. Build needs an own
        /// blueprint; Repair an own or allied building (an ally's bomb only when the ally shares vision).
        /// Wall and dig targets are resolved by their stages.
        /// </summary>
        private static bool TryWorkTarget(SimData data, SimContext context, int owner, OrderKind order, int2 tile, out int2 standGoal)
        {
            standGoal = tile;
            WAR2D.World.MapGrid grid = context.Map.Grid;
            if (!context.BuildingAt.TryGetValue(grid.Index(tile), out int buildingId)) return TryWallTarget(data, context, owner, order, tile, out standGoal);
            int index = NetIdAllocator.IndexOf(buildingId);
            int b = index < data.BuildingSlotOfIndex.Length ? data.BuildingSlotOfIndex[index] : -1;
            if (b < 0 || b >= data.BuildingCount[0] || data.BuildingIds[b] != buildingId) return false;
            int ownerSlot = context.SlotOf(owner), targetSlot = data.BuildingOwnerSlot[b];
            bool own = data.BuildingOwnerId[b] == owner;
            bool allied = targetSlot != SimData.NoSlot && ownerSlot >= 0 && !SimData.Attacks(data.AttackMask, ownerSlot, targetSlot) && !SimData.Attacks(data.AttackMask, targetSlot, ownerSlot);
            if (order == OrderKind.Build && !own) return false;
            if (order == OrderKind.Repair && !own && !allied) return false;
            // A hidden bomb is a target only for players who may know it, or the Builder's walk would reveal it.
            if (!own && (data.BuildingFlag[b] & global::BuildingFlags.Hidden) != 0 && !context.Diplomacy.SharesVisionWith(targetSlot, ownerSlot)) return false;
            return TileSearch.FindNearest(data.BuildingAnchor[b], context.Map.IsWalkable, t => TileRules.IsFloor(grid.TileAt(t)) || context.Map.IsUsed(t), 1024, out standGoal);
        }

        /// <summary>A wall tile as a work target: Build needs an own one, Repair an own or allied one; workers stand on a free tile beside it.</summary>
        private static bool TryWallTarget(SimData data, SimContext context, int owner, OrderKind order, int2 tile, out int2 standGoal)
        {
            standGoal = tile;
            WAR2D.World.MapGrid grid = context.Map.Grid;
            int index = grid.Index(tile);
            if (data.WallState[index] == WallStates.None) return false;
            int ownerSlot = context.SlotOf(owner), wallSlot = data.WallOwnerSlot[index];
            bool own = wallSlot == ownerSlot;
            bool allied = wallSlot != SimData.NoSlot && ownerSlot >= 0 && !SimData.Attacks(data.AttackMask, ownerSlot, wallSlot) && !SimData.Attacks(data.AttackMask, wallSlot, ownerSlot);
            if (order == OrderKind.Build && !own) return false;
            if (order == OrderKind.Repair && !own && !allied) return false;
            return TileSearch.FindNearest(tile, t => !t.Equals(tile) && context.Map.IsWalkable(t), t => TileRules.IsFloor(grid.TileAt(t)) || t.Equals(tile) || context.Map.IsUsed(t), 1024, out standGoal);
        }

        /// <summary>Like <see cref="Issue"/>, with the work target the units start on arrival.</summary>
        private static void IssueWork(SimData data, SimContext context, List<int> slots, int sizeClass, int2 goal, int tick, int owner, int tileIndex, byte kind)
        {
            if (slots.Count == 0) return;
            var starts = new NativeArray<int>(slots.Count, Allocator.Temp);
            for (int i = 0; i < slots.Count; i++)
            {
                int2 tile = math.clamp((int2)math.floor(data.Positions[slots[i]]), 0, new int2(data.Width - 1, data.Height - 1));
                starts[i] = tile.y * data.Width + tile.x;
            }
            int handle = context.Orders.Issue(goal, sizeClass, starts, tick, owner);
            starts.Dispose();
            if (handle < 0) return;
            AddPending(data, slots, new PendingOrder { Slot = handle, Stance = Stances.Move, WorkTile = tileIndex, WorkKind = kind });
        }

        /// <summary>
        /// Stops the workers whose target is done or gone, then sends each on to its next queued job (or
        /// waypoint) when it has one.
        /// </summary>
        private void ClearFinishedWork(SimData data, SimContext context, int unitCount, int tick)
        {
            while (data.WorkFinished.TryDequeue(out int id))
            {
                int slot = data.SlotOf(id, unitCount);
                if (slot < 0 || data.Health[slot] <= 0f) { context.Waypoints.Forget(id); continue; }
                AddPending(data, One(slot), new PendingOrder { Slot = -1, Stance = Stances.Idle, WorkKind = WorkKinds.None });
                if (!context.Waypoints.TryPop(id, out int2 goal, out byte stance, out byte work)) continue;
                if (work != WorkKinds.None) StartQueuedWork(data, context, slot, goal, work, tick);
                else
                {
                    Issue(data, context, One(slot), data.SizeClass[slot] & 1, goal, stance, tick, data.OwnerId[slot]);
                }
            }
        }

        private readonly Dictionary<(int2 stand, int tile, int sizeClass, int owner), List<int>> digLegs = new Dictionary<(int2, int, int, int), List<int>>();

        /// <summary>Sends each Digger that asked to a floor tile beside its next rock, keeping its dig target.</summary>
        private void ApplyDigMoves(SimData data, SimContext context, int unitCount, int tick)
        {
            while (data.DigMoves.TryDequeue(out int3 move))
            {
                int slot = data.SlotOf(move.x, unitCount);
                if (slot < 0 || data.Health[slot] <= 0f || data.WorkKind[slot] != WorkKinds.Dig) continue;
                var key = (new int2(move.y, move.z), data.WorkTile[slot], data.SizeClass[slot] & 1, data.OwnerId[slot]);
                if (!digLegs.TryGetValue(key, out List<int> list)) digLegs[key] = list = new List<int>();
                list.Add(slot);
            }
            foreach (var leg in digLegs)
                IssueWork(data, context, leg.Value, leg.Key.sizeClass, leg.Key.stand, tick, leg.Key.owner, leg.Key.tile, WorkKinds.Dig);
            digLegs.Clear();
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
