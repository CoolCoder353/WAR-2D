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
        /// pending looks, once every <see cref="SimData.AutoWorkSlice"/> ticks, for its owner's nearest blueprint or unfinished
        /// wall anywhere on the map, then for the nearest own building or wall within <see cref="SimData.AutoWorkRadius"/> tiles
        /// below <see cref="SimData.AutoWorkRepairBelow"/> of its health (not while the owner has no money), and takes it as a
        /// Build or Repair order, exactly as a click would (<see cref="ApplyWorkOrder"/>). Never a teammate's. Runs after the
        /// boundary's drained commands, so a player's order this boundary wins.
        /// </summary>
        private void AutoWork(SimData data, SimContext context, int unitCount, int tick)
        {
            float r = data.AutoWorkRadius, rSq = r * r;
            int slice = math.max(1, data.AutoWorkSlice);
            bool collected = false;
            for (int slot = 0; slot < unitCount; slot++)
            {
                if (data.Type[slot] != (byte)UnitType.Builder || data.Health[slot] <= 0f) continue;
                int id = data.IdOf[slot];
                if ((tick + NetIdAllocator.IndexOf(id)) % slice != 0) continue;
                if (data.WorkKind[slot] != WorkKinds.None || data.Stance[slot] != Stances.Idle || HasLiveOrder(data, slot, id)) continue;
                if (data.PendingOrders.ContainsKey(id) || context.Waypoints.CountOf(id) > 0) continue;
                int owner = data.OwnerId[slot];
                int ownerSlot = context.SlotOf(owner);
                if (ownerSlot < 0) continue;
                if (!collected) { CollectWorkBuildings(data); collected = true; }
                float2 at = data.Positions[slot];
                OrderKind kind;
                int2 tile;
                if (TryNearestBuildWork(data, context, owner, ownerSlot, at, data.SizeClass[slot], out tile)) kind = OrderKind.Build;
                else if ((context.BudgetOf == null || context.BudgetOf(owner) > 0f) && TryNearestRepair(data, owner, ownerSlot, at, rSq, out tile)) kind = OrderKind.Repair;
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
                for (; next < responders.Length && (int)(responders[next] >> 32) == alarm; next++)
                {
                    int slot = (int)(responders[next] & 0xffffffffL);
                    int id = data.IdOf[slot];
                    if (!data.PendingOrders.ContainsKey(id) && context.Waypoints.CountOf(id) == 0 && CanWalkTo(data, context, slot, goal)) moversByClass[data.SizeClass[slot]].Add(slot);
                }
                for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
                    if (moversByClass[sizeClass].Count > 0) Issue(data, context, moversByClass[sizeClass], sizeClass, goal, Stances.AttackMove, tick, data.OwnerIdBySlot[ownerSlot], background: true);
            }
            foreach (List<int> list in moversByClass) list.Clear();
            for (int s = 0; s < data.AlarmCount.Length; s++) data.AlarmCount[s] = 0;
        }

        /// <summary>True when the unit can walk to the goal as <see cref="OrderBook.Issue"/> will snap it, so nobody answers a shooter behind a wall by waiting at the wall for good.</summary>
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

        /// <summary>The nearest own blueprint (a tile of its footprint) or unfinished own wall the Builder can walk to, however far.</summary>
        private bool TryNearestBuildWork(SimData data, SimContext context, int owner, int ownerSlot, float2 at, int sizeClass, out int2 tile)
        {
            tile = default;
            float best = float.MaxValue;
            foreach (int b in blueprints)
            {
                if (data.BuildingOwnerId[b] != owner) continue;
                float d = DistanceSqToFootprint(at, data.BuildingAnchor[b], data.BuildingSize[b]);
                if (d >= best) continue;
                int2 start = Footprint.Start(data.BuildingAnchor[b], data.BuildingSize[b]);
                if (!CanStandBeside(context, at, sizeClass, start, data.BuildingSize[b])) continue;
                best = d;
                tile = start;
            }
            foreach (int index in context.UnfinishedWalls)
            {
                if (data.WallOwnerSlot[index] != (byte)ownerSlot) continue;
                int2 wall = new int2(index % data.Width, index / data.Width);
                float d = math.distancesq(at, (float2)wall + 0.5f);
                if (d >= best || !CanStandBeside(context, at, sizeClass, wall, new int2(1, 1))) continue;
                best = d;
                tile = wall;
            }
            return best < float.MaxValue;
        }

        /// <summary>True when a unit at <paramref name="at"/> can walk to a free tile touching the footprint, so a walled-off blueprint doesn't hold a Builder from the next one.</summary>
        private static bool CanStandBeside(SimContext context, float2 at, int sizeClass, int2 start, int2 size)
        {
            int2 from = (int2)math.floor(at);
            for (int y = start.y - 1; y <= start.y + size.y; y++)
            for (int x = start.x - 1; x <= start.x + size.x; x++)
            {
                if (x >= start.x && x < start.x + size.x && y >= start.y && y < start.y + size.y) continue;
                var t = new int2(x, y);
                if (context.Map.IsWalkable(t) && context.Orders.Reaches(sizeClass, from, t)) return true;
            }
            return false;
        }

        /// <summary>The nearest own finished building or built wall under the repair share, within reach.</summary>
        private bool TryNearestRepair(SimData data, int owner, int ownerSlot, float2 at, float rSq, out int2 tile)
        {
            tile = default;
            float best = rSq;
            foreach (int b in damaged)
            {
                if (data.BuildingOwnerId[b] != owner) continue;
                float d = DistanceSqToFootprint(at, data.BuildingAnchor[b], data.BuildingSize[b]);
                if (d < best) { best = d; tile = Footprint.Start(data.BuildingAnchor[b], data.BuildingSize[b]); }
            }
            ScanWalls(data, at, ref best, ref tile, (byte)ownerSlot, data.WallMaxHealth * data.AutoWorkRepairBelow);
            return best < rSq;
        }

        /// <summary>The nearest built own wall tile under <paramref name="below"/> health, within <paramref name="best"/> (squared).</summary>
        private static void ScanWalls(SimData data, float2 at, ref float best, ref int2 tile, byte ownerSlot, float below)
        {
            int r = (int)math.ceil(math.sqrt(best));
            int2 c = (int2)math.floor(at);
            for (int y = math.max(0, c.y - r); y <= math.min(data.Height - 1, c.y + r); y++)
            for (int x = math.max(0, c.x - r); x <= math.min(data.Width - 1, c.x + r); x++)
            {
                int index = y * data.Width + x;
                if (data.WallState[index] != WallStates.Built || data.WallOwnerSlot[index] != ownerSlot || data.WallHealth[index] >= below) continue;
                float d = math.distancesq(at, new float2(x + 0.5f, y + 0.5f));
                if (d < best) { best = d; tile = new int2(x, y); }
            }
        }

        private static float DistanceSqToFootprint(float2 p, int2 anchor, int2 size)
        {
            float2 lo = Footprint.Start(anchor, size), hi = lo + size;
            float2 d = math.max(math.max(lo - p, p - hi), 0f);
            return math.lengthsq(d);
        }
    }
}
