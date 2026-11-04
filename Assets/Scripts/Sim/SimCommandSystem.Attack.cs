using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    public partial class SimCommandSystem
    {
        /// <summary>One owner's Attack order on one target: the shared goal, and what their side last saw of it.</summary>
        private sealed class Chase
        {
            public int Owner, Target;
            public byte Kind;
            public int2 Goal;
            public int LastMoveTick, CreatedTick;
            public bool SeenLastTick;
            public float2 LastSeen;
            public readonly List<int> Followers = new List<int>();
        }

        private readonly Dictionary<(int owner, int target), Chase> chases = new Dictionary<(int, int), Chase>();
        private readonly List<(int, int)> finishedChases = new List<(int, int)>();
        private readonly HashSet<int> attackEnded = new HashSet<int>();

        /// <summary>
        /// True when the id names a live unit or building on the owner's attack mask that their side sees now, never a ghost
        /// or a hidden bomb. This is the only way a target gets into an Attack order, so a client can't learn anything from
        /// naming an id: anything else quietly becomes an Attack-move to the clicked tile, which it could have sent anyway.
        /// </summary>
        private static bool SeesTarget(SimData data, int owner, int id, int unitCount, out byte kind, out float2 position)
        {
            kind = TargetKinds.Unit;
            position = default;
            if (id <= 0) return false;
            int slot = data.OwnerSlotOf(owner), grid = slot * data.FogCells;
            int unit = data.SlotOf(id, unitCount);
            if (unit >= 0)
            {
                position = data.Positions[unit];
                return data.Health[unit] > 0f && SimData.Attacks(data.AttackMask, slot, data.OwnerSlot[unit])
                    && NearestEnemyJob.Sees(data.Visible, grid, position, data.FogCellSize, data.FogW, data.FogH);
            }
            int index = NetIdAllocator.IndexOf(id);
            if (index >= data.BuildingSlotOfIndex.Length) return false;
            int building = data.BuildingSlotOfIndex[index];
            int count = math.min(data.BuildingCount[0], data.BuildingCapacity);
            if ((uint)building >= (uint)count || data.BuildingIds[building] != id) return false;
            kind = TargetKinds.Building;
            position = data.BuildingPositions[building];
            return data.BuildingHealth[building] > 0f && (data.BuildingFlag[building] & BuildingFlags.Hidden) == 0
                && SimData.Attacks(data.AttackMask, slot, data.BuildingOwnerSlot[building])
                && NearestEnemyJob.Sees(data.Visible, grid, position, data.FogCellSize, data.FogW, data.FogH);
        }

        /// <summary>Joins or starts the owner's chase of a target their side sees now; false when it doesn't.</summary>
        private bool TryStartChase(SimData data, int owner, int target, int unitCount, int tick, out Chase chase)
        {
            chase = null;
            if (!SeesTarget(data, owner, target, unitCount, out byte kind, out float2 position)) return false;
            if (!chases.TryGetValue((owner, target), out chase))
            {
                chase = new Chase { Owner = owner, Target = target, Kind = kind, Goal = (int2)math.floor(position), LastMoveTick = tick, CreatedTick = tick };
                chases[(owner, target)] = chase;
            }
            chase.SeenLastTick = true;
            chase.LastSeen = position;
            return true;
        }

        /// <summary>
        /// At each boundary (settled world): moves each Attack order's shared goal after its target while their side sees it,
        /// at most once per <c>AttackOrder/RepathSeconds</c>, so one flow field serves a whole group. When the target leaves
        /// sight the goal stays at the last tile seen, and each follower's order ends when it gets there. A target that dies
        /// in sight ends the order at once. One that dies out of sight tells the followers nothing: they still walk to the
        /// last tile, because stopping early would reveal the death.
        /// </summary>
        private void UpdateChases(SimData data, SimContext context, int unitCount, int tick)
        {
            attackEnded.Clear();
            if (chases.Count == 0) return;
            foreach (Chase c in chases.Values) c.Followers.Clear();
            for (int slot = 0; slot < unitCount; slot++)
            {
                int target = data.OrderTarget[slot];
                if (target == 0 || data.Health[slot] <= 0f) continue;
                if (chases.TryGetValue((data.OwnerId[slot], target), out Chase c)) c.Followers.Add(slot);
            }

            float repath = context.Config.AttackOrder.RepathTiles;
            int waitTicks = (int)math.ceil(context.Config.AttackOrder.RepathSeconds / data.Dt);
            finishedChases.Clear();
            foreach (KeyValuePair<(int owner, int target), Chase> pair in chases)
            {
                Chase c = pair.Value;
                if (c.Followers.Count == 0)
                {
                    if (tick > c.CreatedTick + 1) finishedChases.Add(pair.Key);
                    continue;
                }
                bool seen = SeesTarget(data, c.Owner, c.Target, unitCount, out _, out float2 position);
                bool diedInSight = !seen && c.SeenLastTick && !Exists(data, c.Target, unitCount);
                c.SeenLastTick = seen;
                if (diedInSight)
                {
                    EndChaseFor(data, c.Followers);
                    finishedChases.Add(pair.Key);
                    continue;
                }
                if (seen)
                {
                    c.LastSeen = position;
                    bool moved = math.distance((float2)c.Goal + 0.5f, position) > repath;
                    if (moved && tick - c.LastMoveTick >= waitTicks) MoveChaseGoal(data, context, c, (int2)math.floor(position), tick);
                    continue;
                }
                // Out of sight: the goal is the last tile seen; whoever reached it (or lost its route) is done.
                int2 last = (int2)math.floor(c.LastSeen);
                if (!last.Equals(c.Goal)) MoveChaseGoal(data, context, c, last, tick);
                else
                {
                    onlyDone.Clear();
                    foreach (int slot in c.Followers)
                    {
                        int order = data.OrderSlot[slot];
                        bool routed = (uint)order < (uint)data.OrderLive.Length && data.OrderLive[order] != 0;
                        if (data.Arrived[slot] != 0 || !routed) onlyDone.Add(slot);
                    }
                    EndChaseFor(data, onlyDone);
                }
            }
            foreach ((int, int) key in finishedChases) chases.Remove(key);
        }

        private readonly List<int> onlyDone = new List<int>();

        /// <summary>True for a target still alive. The settled arrays still hold what died this tick, at zero health.</summary>
        private static bool Exists(SimData data, int id, int unitCount)
        {
            int unit = data.SlotOf(id, unitCount);
            if (unit >= 0) return data.Health[unit] > 0f;
            int index = NetIdAllocator.IndexOf(id);
            if (index >= data.BuildingSlotOfIndex.Length) return false;
            int building = data.BuildingSlotOfIndex[index];
            return (uint)building < (uint)math.min(data.BuildingCount[0], data.BuildingCapacity) && data.BuildingIds[building] == id && data.BuildingHealth[building] > 0f;
        }

        private void EndChaseFor(SimData data, List<int> slots)
        {
            AddPending(data, slots, new PendingOrder { Slot = -1, Stance = Stances.Idle });
            foreach (int slot in slots)
            {
                attackEnded.Add(data.IdOf[slot]);
                data.Arrivals.Enqueue(data.IdOf[slot]); // its queued orders go on from here
            }
        }

        private void MoveChaseGoal(SimData data, SimContext context, Chase c, int2 goal, int tick)
        {
            foreach (List<int> list in moversByClass) list.Clear();
            foreach (int slot in c.Followers) moversByClass[data.SizeClass[slot]].Add(slot);
            for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
                Issue(data, context, moversByClass[sizeClass], sizeClass, goal, Stances.Attack, tick, c.Owner, c.Target, c.Kind);
            c.Goal = goal;
            c.LastMoveTick = tick;
        }

        /// <summary>The one size class's movers of an Attack order, after <see cref="TryStartChase"/>.</summary>
        private void IssueAttack(SimData data, SimContext context, Chase chase, int tick)
        {
            for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
                Issue(data, context, moversByClass[sizeClass], sizeClass, chase.Goal, Stances.Attack, tick, chase.Owner, chase.Target, chase.Kind);
        }
    }
}
