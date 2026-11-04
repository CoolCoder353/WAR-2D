using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// What a Suicide bomber is aiming at (v0.18), stored by its id's index because slots move between ticks. A record whose
    /// <see cref="Id"/> isn't the bomber's is stale (the index was reused) and counts as no aim.
    /// </summary>
    public struct BlastAim
    {
        public int Id;
        /// <summary>The blast point.</summary>
        public float2 Point;
        /// <summary>The blast's worth at the point.</summary>
        public float Worth;
        /// <summary>Closest it has come to the point, and ticks since it last came closer.</summary>
        public float Closest;
        public int StuckTicks;
    }

    /// <summary>
    /// Works out what a blast is worth: enemy worth in its radius less <see cref="FriendlyWeight"/> times the worth of
    /// everyone its side doesn't attack. It reads only what the bomber's side sees now, because a bomber's route reaches its
    /// owner's client, so an aim built on the server's full picture would draw hidden units' positions.
    /// </summary>
    public struct BlastWorth
    {
        [ReadOnly] public SearchInput Search;
        [ReadOnly] public NativeArray<byte> Type, BuildingKind;
        [ReadOnly] public NativeArray<float> CostByType, BuildingCostByKind;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        public float FriendlyWeight;

        /// <summary>The blast's worth at a point, not counting the bomber itself.</summary>
        public float At(float2 at, int slot, float radius, int self)
        {
            ref readonly SearchInput s = ref Search;
            int grid = slot * s.FogCells;
            float rSq = radius * radius, worth = 0f;
            int2 c = (int2)math.floor(at * s.InvCellSize);
            int reach = (int)math.ceil(radius * s.InvCellSize) + 1;
            int2 lo = math.max(c - reach, 0), hi = math.min(c + reach, new int2(s.CellsX - 1, s.CellsY - 1));
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * s.CellsX + cx;
                for (int k = s.CellStart[cell]; k < s.CellStart[cell + 1]; k++)
                {
                    int u = s.Sorted[k];
                    if (u == self || s.Health[u] <= 0f || math.distancesq(s.Positions[u], at) > rSq) continue;
                    int other = s.OwnerSlot[u];
                    if (other != slot && !TargetSearch.Sees(s, grid, s.Positions[u])) continue;
                    float cost = CostByType[Type[u]];
                    worth += SimData.Attacks(s.AttackMask, slot, other) ? cost : -FriendlyWeight * cost;
                }
                for (int k = s.BuildingCellStart[cell]; k < s.BuildingCellStart[cell + 1]; k++)
                {
                    int b = s.BuildingSorted[k];
                    if (s.BuildingHealth[b] <= 0f || (s.BuildingFlag[b] & BuildingFlags.Hidden) != 0) continue;
                    float2 from = Footprint.Start(BuildingAnchor[b], BuildingSize[b]), to = from + BuildingSize[b];
                    if (math.lengthsq(math.max(math.max(from - at, at - to), 0f)) > rSq) continue;
                    int other = s.BuildingOwnerSlot[b];
                    if (other != slot && !TargetSearch.Sees(s, grid, s.BuildingPositions[b])) continue;
                    float cost = BuildingCostByKind[BuildingKind[b]];
                    worth += SimData.Attacks(s.AttackMask, slot, other) ? cost : -FriendlyWeight * cost;
                }
            }
            return worth;
        }

        /// <summary>True when an enemy the side sees is within <paramref name="reach"/> tiles (bodies' edges) of the point.</summary>
        public bool TouchesEnemy(float2 at, int slot, float reach, float selfRadius, NativeArray<float> radius)
        {
            ref readonly SearchInput s = ref Search;
            int grid = slot * s.FogCells;
            int2 c = (int2)math.floor(at * s.InvCellSize);
            int cells = (int)math.ceil((reach + selfRadius + 1f) * s.InvCellSize);
            int2 lo = math.max(c - cells, 0), hi = math.min(c + cells, new int2(s.CellsX - 1, s.CellsY - 1));
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * s.CellsX + cx;
                for (int k = s.CellStart[cell]; k < s.CellStart[cell + 1]; k++)
                {
                    int u = s.Sorted[k];
                    if (s.Health[u] <= 0f || !SimData.Attacks(s.AttackMask, slot, s.OwnerSlot[u])) continue;
                    float within = reach + selfRadius + radius[u];
                    if (math.distancesq(s.Positions[u], at) <= within * within && TargetSearch.Sees(s, grid, s.Positions[u])) return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Chooses each bomber's blast point on its target-search slice. Against a unit: of the enemies its side sees within
    /// <c>Bomber/AimTiles</c> of the target, the position whose blast is worth most (ties to the nearest). Against a building:
    /// the best of the four points just outside the middle of each side, never a corner. Against a wall or nothing: no aim.
    /// </summary>
    [BurstCompile]
    public struct BlastAimJob : IJobParallelFor
    {
        /// <summary>Candidates tried per bomber; a denser crowd is sampled evenly.</summary>
        private const int MaxCandidates = 24;

        public BlastWorth Worth;
        [NativeDisableParallelForRestriction] public NativeArray<BlastAim> Aims;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<byte> TargetKind, Stance;
        [ReadOnly] public NativeArray<float> BlastRadiusByType;
        public float AimTiles;
        public int Tick, Slice;

        public void Execute(int i)
        {
            if (Slice > 1 && (i + Tick) % Slice != 0) return;
            int type = Worth.Type[i];
            if (type >= BlastRadiusByType.Length || BlastRadiusByType[type] <= 0f || Worth.Search.Health[i] <= 0f) return;
            int id = Worth.Search.IdOf[i];
            int key = NetIdAllocator.IndexOf(id);
            int target = Target[i];
            byte kind = TargetKind[i];
            if (target < 0 || Stance[i] == Stances.Move || kind == TargetKinds.Wall)
            {
                if (Aims[key].Id == id) Aims[key] = default;
                return;
            }
            float2 p = Worth.Search.Positions[i];
            int slot = Worth.Search.OwnerSlot[i];
            float radius = BlastRadiusByType[type];
            float bestWorth = float.MinValue, bestDistance = float.MaxValue;
            float2 best = p;
            if (kind == TargetKinds.Unit) BestAmongEnemies(Worth.Search.Positions[target], p, slot, radius, i, ref best, ref bestWorth, ref bestDistance);
            else BestBesideBuilding(target, p, slot, radius, i, ref best, ref bestWorth, ref bestDistance);
            if (bestWorth == float.MinValue) return;

            BlastAim aim = Aims[key];
            bool fresh = aim.Id != id;
            float distance = math.distance(p, best);
            if (fresh) aim = new BlastAim { Id = id, Closest = distance };
            else if (math.distancesq(aim.Point, best) > 1f) aim.Closest = distance; // a new point: it hasn't been nearer this one yet
            aim.Point = best;
            aim.Worth = bestWorth;
            Aims[key] = aim;
        }

        private void BestAmongEnemies(float2 centre, float2 p, int slot, float radius, int self, ref float2 best, ref float bestWorth, ref float bestDistance)
        {
            ref readonly SearchInput s = ref Worth.Search;
            int grid = slot * s.FogCells;
            float aimSq = AimTiles * AimTiles;
            int2 c = (int2)math.floor(centre * s.InvCellSize);
            int reach = (int)math.ceil(AimTiles * s.InvCellSize) + 1;
            int2 lo = math.max(c - reach, 0), hi = math.min(c + reach, new int2(s.CellsX - 1, s.CellsY - 1));
            var candidates = new NativeList<float2>(64, Allocator.Temp);
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * s.CellsX + cx;
                for (int k = s.CellStart[cell]; k < s.CellStart[cell + 1]; k++)
                {
                    int u = s.Sorted[k];
                    if (s.Health[u] <= 0f || !SimData.Attacks(s.AttackMask, slot, s.OwnerSlot[u])) continue;
                    if (math.distancesq(s.Positions[u], centre) <= aimSq && TargetSearch.Sees(s, grid, s.Positions[u])) candidates.Add(s.Positions[u]);
                }
            }
            int stride = math.max(1, candidates.Length / MaxCandidates);
            for (int n = 0; n < candidates.Length; n += stride) Consider(candidates[n], p, slot, radius, self, ref best, ref bestWorth, ref bestDistance);
            candidates.Dispose();
        }

        private void BestBesideBuilding(int building, float2 p, int slot, float radius, int self, ref float2 best, ref float bestWorth, ref float bestDistance)
        {
            int2 size = Worth.BuildingSize[building];
            float2 from = Footprint.Start(Worth.BuildingAnchor[building], size), to = from + size, mid = (from + to) * 0.5f;
            const float Outside = 0.5f;
            Consider(new float2(mid.x, from.y - Outside), p, slot, radius, self, ref best, ref bestWorth, ref bestDistance);
            Consider(new float2(mid.x, to.y + Outside), p, slot, radius, self, ref best, ref bestWorth, ref bestDistance);
            Consider(new float2(from.x - Outside, mid.y), p, slot, radius, self, ref best, ref bestWorth, ref bestDistance);
            Consider(new float2(to.x + Outside, mid.y), p, slot, radius, self, ref best, ref bestWorth, ref bestDistance);
        }

        private void Consider(float2 point, float2 p, int slot, float radius, int self, ref float2 best, ref float bestWorth, ref float bestDistance)
        {
            float worth = Worth.At(point, slot, radius, self);
            float distance = math.distancesq(point, p);
            if (worth < bestWorth || (worth == bestWorth && distance >= bestDistance)) return;
            best = point;
            bestWorth = worth;
            bestDistance = distance;
        }
    }
}
