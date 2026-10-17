using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>What a combat target slot refers to.</summary>
    public static class TargetKinds
    {
        public const byte Unit = 0;
        public const byte Building = 1;
        /// <summary>A player wall tile; the target is the tile index.</summary>
        public const byte Wall = 2;

        /// <summary>
        /// True for a unit on a <see cref="Stances.Move"/> order that is still live: it neither searches
        /// for nor keeps a target until it arrives.
        /// </summary>
        public static bool IgnoresEnemies(byte stance, int orderSlot, NativeArray<byte> orderLive) =>
            stance == Stances.Move && (uint)orderSlot < (uint)orderLive.Length && orderLive[orderSlot] != 0;
    }

    /// <summary>
    /// Nearest-enemy search over the counting-sort hash. Each unit searches on one tick in
    /// <see cref="Slice"/> (staggered by slot) and keeps its resolved target in between. It looks for
    /// the nearest living enemy unit within its type's range; with none in range it falls back to the
    /// nearest enemy building. "Enemy" means an owner on the unit's owner's attack mask.
    /// </summary>
    [BurstCompile]
    public struct NearestEnemyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<byte> Stance;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<byte> OrderLive;
        [ReadOnly] public NativeArray<float> RangeSqByType, MinRangeSqByType;
        [ReadOnly] public NativeArray<int> CellStart, Sorted;
        /// <summary>Fog grids (see <see cref="SimData.Visible"/>): a unit fires only at what its owner's grid sees now.</summary>
        [ReadOnly] public NativeArray<byte> Visible;
        public int FogCellSize, FogW, FogH, FogCells;
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<byte> BuildingOwnerSlot;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        [ReadOnly] public NativeArray<int> BuildingCellStart, BuildingSorted;
        public float InvCellSize;
        public int CellsX, CellsY, SearchCells; // SearchCells = ceil(max range / cell size): the cap on any type's reach
        public int Tick, Slice;
        [ReadOnly] public NativeArray<byte> OpaqueTile;
        [ReadOnly] public NativeArray<float> DamageByType;
        [ReadOnly] public NativeArray<byte> WallState, WallOwnerSlot;
        [ReadOnly] public NativeArray<byte> BuildingFlag;
        public int Width, Height;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            if (Slice > 1 && (i + Tick) % Slice != 0) return;
            if (Health[i] <= 0f || TargetKinds.IgnoresEnemies(Stance[i], OrderSlot[i], OrderLive) || DamageByType[Type[i]] <= 0f) { Target[i] = -1; return; }

            float2 p = Positions[i];
            int slot = OwnerSlot[i];
            int type = Type[i];
            float range = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;
            float minSq = type < MinRangeSqByType.Length ? MinRangeSqByType[type] : 0f;
            int grid = slot * FogCells;
            int2 c = (int2)math.floor(p * InvCellSize);
            // Each unit scans only the cells its own range reaches: the longest range (a Sniper's) would
            // otherwise make every Tank scan nine times the cells.
            int reach = math.clamp((int)math.ceil(math.sqrt(range) * InvCellSize), 1, SearchCells);
            int2 lo = math.max(c - reach, 0);
            int2 hi = math.min(c + reach, new int2(CellsX - 1, CellsY - 1));

            float best = range;
            int bestIndex = -1;
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = CellStart[cell]; k < CellStart[cell + 1]; k++)
                {
                    int j = Sorted[k];
                    if (Health[j] <= 0f || !SimData.Attacks(AttackMask, slot, OwnerSlot[j])) continue;
                    float d = math.distancesq(p, Positions[j]);
                    if (d <= best && d >= minSq && Sees(grid, Positions[j]) && LineOfSight.ClearTiles(OpaqueTile, Width, Height, p, Positions[j])) { best = d; bestIndex = j; }
                }
            }
            if (bestIndex >= 0)
            {
                Target[i] = bestIndex;
                TargetKind[i] = TargetKinds.Unit;
                return;
            }

            best = range;
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = BuildingCellStart[cell]; k < BuildingCellStart[cell + 1]; k++)
                {
                    int j = BuildingSorted[k];
                    if (BuildingHealth[j] <= 0f || (BuildingFlag[j] & BuildingFlags.Hidden) != 0 || !SimData.Attacks(AttackMask, slot, BuildingOwnerSlot[j])) continue;
                    float d = math.distancesq(p, BuildingPositions[j]);
                    if (d <= best && d >= minSq && Sees(grid, BuildingPositions[j]) && LineOfSight.ClearTiles(OpaqueTile, Width, Height, p, BuildingPositions[j])) { best = d; bestIndex = j; }
                }
            }
            Target[i] = bestIndex;
            TargetKind[i] = bestIndex >= 0 ? TargetKinds.Building : TargetKinds.Unit;
            if (bestIndex < 0 && Stance[i] == Stances.AttackMove) FindWall(i, p, slot, range);
        }

        private bool Sees(int grid, float2 position) => Sees(Visible, grid, position, FogCellSize, FogW, FogH);

        /// <summary>True when the fog grid starting at <paramref name="grid"/> (an owner slot times <c>FogCells</c>) sees the point now.</summary>
        public static bool Sees(NativeArray<byte> visible, int grid, float2 position, int fogCellSize, int fogW, int fogH)
        {
            int2 f = math.clamp((int2)math.floor(position / fogCellSize), 0, new int2(fogW - 1, fogH - 1));
            return visible[grid + f.y * fogW + f.x] != 0;
        }

        /// <summary>
        /// A unit on an Attack-move order that found no unit or building in range breaks the nearest enemy
        /// wall tile it can see within range. The wall tile ends the line, so it doesn't block its own line.
        /// </summary>
        private void FindWall(int i, float2 p, int slot, float rangeSq)
        {
            int r = (int)math.ceil(math.sqrt(rangeSq));
            int2 c = (int2)math.floor(p);
            int2 lo = math.max(c - r, 0), hi = math.min(c + r, new int2(Width - 1, Height - 1));
            float best = rangeSq;
            int bestTile = -1;
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int t = y * Width + x;
                if (WallState[t] == WallStates.None || !SimData.Attacks(AttackMask, slot, WallOwnerSlot[t])) continue;
                float2 centre = new float2(x + 0.5f, y + 0.5f);
                float d = math.distancesq(p, centre);
                if (d <= best && LineOfSight.ClearTiles(OpaqueTile, Width, Height, p, centre)) { best = d; bestTile = t; }
            }
            if (bestTile < 0) return;
            Target[i] = bestTile;
            TargetKind[i] = TargetKinds.Wall;
        }
    }
}
