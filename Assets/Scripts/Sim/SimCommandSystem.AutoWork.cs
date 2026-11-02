using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    public partial class SimCommandSystem
    {
        /// <summary>
        /// Idle Builders work on their own (v0.16): each Builder with no order, no work, no queued waypoints and nothing
        /// pending looks, once every <see cref="SimData.AutoWorkSlice"/> ticks, for its owner's nearest blueprint or unfinished
        /// wall within <see cref="SimData.AutoWorkRadius"/> tiles, then for the nearest own building or wall below
        /// <see cref="SimData.AutoWorkRepairBelow"/> of its health (not while the owner has no money), and takes it as a
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
                if (TryNearestBuildWork(data, owner, ownerSlot, at, rSq, out tile)) kind = OrderKind.Build;
                else if ((context.BudgetOf == null || context.BudgetOf(owner) > 0f) && TryNearestRepair(data, owner, ownerSlot, at, rSq, out tile)) kind = OrderKind.Repair;
                else continue;
                ApplyWorkOrder(data, context, new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, OwnerId = owner, Tile = tile, Ids = new[] { id } }, unitCount);
            }
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

        /// <summary>The nearest own blueprint (a tile of its footprint) or unfinished own wall within reach.</summary>
        private bool TryNearestBuildWork(SimData data, int owner, int ownerSlot, float2 at, float rSq, out int2 tile)
        {
            tile = default;
            float best = rSq;
            foreach (int b in blueprints)
            {
                if (data.BuildingOwnerId[b] != owner) continue;
                float d = DistanceSqToFootprint(at, data.BuildingAnchor[b], data.BuildingSize[b]);
                if (d < best) { best = d; tile = Footprint.Start(data.BuildingAnchor[b], data.BuildingSize[b]); }
            }
            ScanWalls(data, at, ref best, ref tile, (byte)ownerSlot, false, 0f);
            return best < rSq;
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
            ScanWalls(data, at, ref best, ref tile, (byte)ownerSlot, true, data.WallMaxHealth * data.AutoWorkRepairBelow);
            return best < rSq;
        }

        /// <summary>
        /// The nearest own wall tile within <paramref name="best"/> (squared): unfinished ones (a blueprint or rising) for
        /// building, built ones under <paramref name="below"/> health for <paramref name="repair"/>.
        /// </summary>
        private static void ScanWalls(SimData data, float2 at, ref float best, ref int2 tile, byte ownerSlot, bool repair, float below)
        {
            int r = (int)math.ceil(math.sqrt(best));
            int2 c = (int2)math.floor(at);
            for (int y = math.max(0, c.y - r); y <= math.min(data.Height - 1, c.y + r); y++)
            for (int x = math.max(0, c.x - r); x <= math.min(data.Width - 1, c.x + r); x++)
            {
                int index = y * data.Width + x;
                byte state = data.WallState[index];
                if (state == WallStates.None || data.WallOwnerSlot[index] != ownerSlot) continue;
                if (repair ? state != WallStates.Built || data.WallHealth[index] >= below : state == WallStates.Built) continue;
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
