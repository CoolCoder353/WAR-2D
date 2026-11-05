using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    public partial class SimCommandSystem
    {
        /// <summary>
        /// Idle Builders work on their own (v0.16): each Builder with no order, no work, no queued waypoints and nothing
        /// pending looks, once every <see cref="SimData.AutoWorkSlice"/> ticks, for the closest job it can walk to within
        /// <see cref="SimData.AutoWorkRadius"/> tiles: its owner's blueprint or unfinished wall, or an own building or wall below
        /// <see cref="SimData.AutoWorkRepairBelow"/> of its health (not while the owner has no money). It takes that as a
        /// Build or Repair order, exactly as a click would (<see cref="ApplyWorkOrder"/>). Never a teammate's. Runs after the
        /// boundary's drained commands, so a player's order this boundary wins.
        /// </summary>
        private void AutoWork(SimData data, SimContext context, int unitCount, int tick)
        {
            float r = data.AutoWorkRadius, rSq = r * r;
            int slice = math.max(1, data.AutoWorkSlice);
            bool collected = false;
            // The Builders whose turn it is, idle and with no work, found in Burst: a managed pass over every unit each tick cost
            // a millisecond and more at 80,000 units (perf gate 2026-11-06).
            using var turn = new NativeList<int>(64, Allocator.TempJob);
            new IdleBuildersJob
            {
                Type = data.Type, Health = data.Health, IdOf = data.IdOf, WorkKind = data.WorkKind, Stance = data.Stance,
                Count = unitCount, Tick = tick, Slice = slice, Hits = turn,
            }.Run();
            foreach (int slot in turn)
            {
                int id = data.IdOf[slot];
                if (HasLiveOrder(data, slot, id)) continue;
                if (data.PendingOrders.ContainsKey(id) || context.Waypoints.CountOf(id) > 0) continue;
                int owner = data.OwnerId[slot];
                int ownerSlot = context.SlotOf(owner);
                if (ownerSlot < 0) continue;
                if (!collected) { CollectWorkBuildings(data); collected = true; }
                float2 at = data.Positions[slot];
                int sizeClass = data.SizeClass[slot];
                bool build = TryNearestBuildWork(data, context, owner, ownerSlot, at, sizeClass, rSq, out int2 buildTile, out float buildSq);
                OrderKind kind;
                int2 tile;
                if ((context.BudgetOf == null || context.BudgetOf(owner) > 0f) && TryNearestRepair(data, context, owner, ownerSlot, at, sizeClass, build ? buildSq : rSq, out tile)) kind = OrderKind.Repair;
                else if (build) { kind = OrderKind.Build; tile = buildTile; }
                else continue;
                ApplyWorkOrder(data, context, new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, OwnerId = owner, Tile = tile, Ids = new[] { id } }, unitCount, background: true);
            }
        }

        /// <summary>
        /// Idle units answer fire (playtest 2026-11-04): every <c>Defend/IntervalTicks</c>, the armed idle units near a shot unit or
        /// building of their own side attack-move at the shooter (<see cref="DefendAlarms"/>), so an army isn't picked apart from
        /// out of its range and a base under fire draws the units standing by it. Hold, work and any order keep their units.
        /// Runs after the boundary's drained commands, so a player's order this boundary wins.
        /// </summary>
        private void AnswerAlarms(SimData data, SimContext context, int unitCount, int tick)
        {
            if (tick % math.max(1, data.DefendInterval) != 0) return;
            bool any = false;
            for (int s = 0; s < data.AlarmCount.Length; s++) any |= data.AlarmCount[s] > 0;
            if (!any) return;

            using var responders = new NativeList<long>(256, Allocator.TempJob);
            new FindRespondersJob
            {
                Count = unitCount,
                RadiusSq = data.DefendRadius * data.DefendRadius,
                Alarms = data.Alarms,
                AlarmCount = data.AlarmCount,
                Positions = data.Positions,
                Health = data.Health,
                DamageByType = data.DamageByType,
                OwnerSlot = data.OwnerSlot,
                Type = data.Type,
                Stance = data.Stance,
                WorkKind = data.WorkKind,
                Target = data.Target,
                OrderSlot = data.OrderSlot,
                OrderLive = data.OrderLive,
                Results = responders,
            }.Run();
            responders.Sort();

            for (int next = 0; next < responders.Length;)
            {
                int alarm = (int)(responders[next] >> 32);
                int ownerSlot = alarm / DefendAlarms.PerOwner;
                DefendAlarm raised = data.Alarms[alarm];
                bool seen = NearestEnemyJob.Sees(data.Visible, ownerSlot * data.FogCells, raised.From, data.FogCellSize, data.FogW, data.FogH);
                int2 goal = DefendAlarms.Goal(raised, seen, data.DefendStepTiles);
                foreach (List<int> list in moversByClass) list.Clear();
                reachMemo.Clear();
                for (; next < responders.Length && (int)(responders[next] >> 32) == alarm; next++)
                {
                    int slot = (int)(responders[next] & 0xffffffffL);
                    int id = data.IdOf[slot];
                    if (!data.PendingOrders.ContainsKey(id) && context.Waypoints.CountOf(id) == 0 && CanWalkToMemo(data, context, slot, goal)) moversByClass[data.SizeClass[slot]].Add(slot);
                }
                for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
                    if (moversByClass[sizeClass].Count > 0) Issue(data, context, moversByClass[sizeClass], sizeClass, goal, Stances.AttackMove, tick, data.OwnerIdBySlot[ownerSlot], background: true);
            }
            foreach (List<int> list in moversByClass) list.Clear();
            for (int s = 0; s < data.AlarmCount.Length; s++) data.AlarmCount[s] = 0;
        }

        /// <summary>True when the unit can walk to the goal as <see cref="OrderBook.Issue"/> will snap it, so nobody answers a shooter behind a wall by waiting at the wall for good.</summary>
        /// <summary>
        /// <see cref="CanWalkTo"/> for one alarm's responders, remembered by size class, pocket and snapped goal: a crowd around a
        /// shot unit is mostly in one pocket, so the reach is looked up once, not once a unit (perf gate 2026-11-06).
        /// </summary>
        private bool CanWalkToMemo(SimData data, SimContext context, int slot, int2 goal)
        {
            int2 from = (int2)math.floor(data.Positions[slot]);
            goal = math.clamp(goal, 0, new int2(data.Width - 1, data.Height - 1));
            if (!OrderBook.SnapGoal(context.Map.Grid, goal, from, OrderBook.GoalSnapRadius, out int2 snapped)) return false;
            int sizeClass = data.SizeClass[slot];
            int pocket = context.Orders.PocketOf(sizeClass, from);
            if (pocket < 0) return false;
            long key = ((long)sizeClass << 60) | ((long)pocket << 30) | (long)(snapped.y * data.Width + snapped.x);
            if (reachMemo.TryGetValue(key, out bool reaches)) return reaches;
            reaches = context.Orders.Connects(sizeClass, pocket, context.Orders.PocketOf(sizeClass, snapped));
            reachMemo[key] = reaches;
            return reaches;
        }

        private readonly Dictionary<long, bool> reachMemo = new Dictionary<long, bool>();

        private static bool CanWalkTo(SimData data, SimContext context, int slot, int2 goal)
        {
            int2 from = (int2)math.floor(data.Positions[slot]);
            goal = math.clamp(goal, 0, new int2(data.Width - 1, data.Height - 1));
            return OrderBook.SnapGoal(context.Map.Grid, goal, from, OrderBook.GoalSnapRadius, out int2 snapped) && context.Orders.Reaches(data.SizeClass[slot], from, snapped);
        }

        private readonly List<int> blueprints = new List<int>();
        private readonly List<int> damaged = new List<int>();

        /// <summary>
        /// This boundary's living blueprints and finished buildings under the repair share (every owner's, in slot order), so
        /// each Builder that looks checks those few rather than every building.
        /// </summary>
        private void CollectWorkBuildings(SimData data)
        {
            blueprints.Clear();
            damaged.Clear();
            int count = data.BuildingCount[0];
            for (int b = 0; b < count; b++)
            {
                float h = data.BuildingHealth[b];
                if (h <= 0f) continue;
                if ((data.BuildingFlag[b] & global::BuildingFlags.UnderConstruction) != 0) blueprints.Add(b);
                else if (h < data.BuildingMaxHealth[b] * data.AutoWorkRepairBelow) damaged.Add(b);
            }
        }

        /// <summary>A job a Builder could take: how far it is (squared), its footprint's start and size.</summary>
        private struct WorkCandidate : System.IComparable<WorkCandidate>
        {
            public float DistanceSq;
            public int2 Start, Size;
            /// <summary>Its place in the scan: on equal distance the earlier one wins, as it did when the scan kept the first best.</summary>
            public int Order;
            public int CompareTo(WorkCandidate other) => DistanceSq != other.DistanceSq ? DistanceSq.CompareTo(other.DistanceSq) : Order.CompareTo(other.Order);
        }

        private readonly List<WorkCandidate> workCandidates = new List<WorkCandidate>();

        /// <summary>
        /// The nearest candidate the Builder can stand beside, checked nearest first so the walk check (the costly part) usually runs
        /// once, not once for every job that beat the last (perf gate 2026-11-06).
        /// </summary>
        private bool TryNearestReachable(SimContext context, float2 at, int sizeClass, out int2 tile, out float distanceSq)
        {
            tile = default;
            distanceSq = float.MaxValue;
            if (workCandidates.Count == 0) return false;
            int pocket = context.Orders.PocketOf(sizeClass, (int2)math.floor(at));
            if (pocket < 0) return false;
            workCandidates.Sort();
            foreach (WorkCandidate c in workCandidates)
            {
                if (!CanStandBeside(context, pocket, sizeClass, c.Start, c.Size)) continue;
                tile = c.Start;
                distanceSq = c.DistanceSq;
                return true;
            }
            return false;
        }

        /// <summary>The nearest own blueprint (a tile of its footprint) or unfinished own wall the Builder can walk to, closer than <paramref name="within"/> (squared).</summary>
        private bool TryNearestBuildWork(SimData data, SimContext context, int owner, int ownerSlot, float2 at, int sizeClass, float within, out int2 tile, out float distanceSq)
        {
            workCandidates.Clear();
            foreach (int b in blueprints)
            {
                if (data.BuildingOwnerId[b] != owner) continue;
                float d = DistanceSqToFootprint(at, data.BuildingAnchor[b], data.BuildingSize[b]);
                if (d < within) workCandidates.Add(new WorkCandidate { Order = workCandidates.Count, DistanceSq = d, Start = Footprint.Start(data.BuildingAnchor[b], data.BuildingSize[b]), Size = data.BuildingSize[b] });
            }
            foreach (int index in context.UnfinishedWalls)
            {
                if (data.WallOwnerSlot[index] != (byte)ownerSlot) continue;
                int2 wall = new int2(index % data.Width, index / data.Width);
                float d = math.distancesq(at, (float2)wall + 0.5f);
                if (d < within) workCandidates.Add(new WorkCandidate { Order = workCandidates.Count, DistanceSq = d, Start = wall, Size = new int2(1, 1) });
            }
            return TryNearestReachable(context, at, sizeClass, out tile, out distanceSq);
        }

        /// <summary>True when a unit in <paramref name="pocket"/> can walk to a free tile touching the footprint, so a walled-off blueprint doesn't hold a Builder from the next one.</summary>
        private static bool CanStandBeside(SimContext context, int pocket, int sizeClass, int2 start, int2 size)
        {
            for (int y = start.y - 1; y <= start.y + size.y; y++)
            for (int x = start.x - 1; x <= start.x + size.x; x++)
            {
                if (x >= start.x && x < start.x + size.x && y >= start.y && y < start.y + size.y) continue;
                var t = new int2(x, y);
                if (context.Map.IsWalkable(t) && context.Orders.Connects(sizeClass, pocket, context.Orders.PocketOf(sizeClass, t))) return true;
            }
            return false;
        }

        /// <summary>The nearest own finished building or built wall under the repair share that the Builder can walk to, closer than <paramref name="within"/> (squared).</summary>
        private bool TryNearestRepair(SimData data, SimContext context, int owner, int ownerSlot, float2 at, int sizeClass, float within, out int2 tile)
        {
            workCandidates.Clear();
            foreach (int b in damaged)
            {
                if (data.BuildingOwnerId[b] != owner) continue;
                float d = DistanceSqToFootprint(at, data.BuildingAnchor[b], data.BuildingSize[b]);
                if (d < within) workCandidates.Add(new WorkCandidate { Order = workCandidates.Count, DistanceSq = d, Start = Footprint.Start(data.BuildingAnchor[b], data.BuildingSize[b]), Size = data.BuildingSize[b] });
            }
            float below = data.WallMaxHealth * data.AutoWorkRepairBelow;
            foreach (int index in context.DamagedWalls)
            {
                if (data.WallOwnerSlot[index] != (byte)ownerSlot || data.WallHealth[index] >= below) continue;
                int2 wall = new int2(index % data.Width, index / data.Width);
                float d = math.distancesq(at, (float2)wall + 0.5f);
                if (d < within) workCandidates.Add(new WorkCandidate { Order = workCandidates.Count, DistanceSq = d, Start = wall, Size = new int2(1, 1) });
            }
            return TryNearestReachable(context, at, sizeClass, out tile, out _);
        }

        private static float DistanceSqToFootprint(float2 p, int2 anchor, int2 size)
        {
            float2 lo = Footprint.Start(anchor, size), hi = lo + size;
            float2 d = math.max(math.max(lo - p, p - hi), 0f);
            return math.lengthsq(d);
        }
    }

    /// <summary>Builders alive, idle and with no work whose auto-work turn this tick is (<c>(tick + index) % slice == 0</c>).</summary>
    [Unity.Burst.BurstCompile]
    public struct IdleBuildersJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Type, WorkKind, Stance;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<int> IdOf;
        public int Count, Tick, Slice;
        public NativeList<int> Hits;

        public void Execute()
        {
            for (int slot = 0; slot < Count; slot++)
            {
                if (Type[slot] != (byte)UnitType.Builder || Health[slot] <= 0f) continue;
                if ((Tick + NetIdAllocator.IndexOf(IdOf[slot])) % Slice != 0) continue;
                if (WorkKind[slot] != WorkKinds.None || Stance[slot] != Stances.Idle) continue;
                Hits.Add(slot);
            }
        }
    }
}
