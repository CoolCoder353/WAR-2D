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
    }

    /// <summary>
    /// Nearest-enemy search over the counting-sort hash. Each unit searches on one tick in
    /// <see cref="Slice"/> (staggered by slot) and keeps its resolved target in between. It looks for
    /// the nearest living enemy unit within its type's range; with none in range it falls back to the
    /// nearest enemy building. "Enemy" means a different owner (teams arrive in v0.5).
    /// </summary>
    [BurstCompile]
    public struct NearestEnemyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<int> OwnerId;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<float> RangeSqByType;
        [ReadOnly] public NativeArray<int> CellStart, Sorted;
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<int> BuildingOwnerId;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        [ReadOnly] public NativeArray<int> BuildingCellStart, BuildingSorted;
        public float InvCellSize;
        public int CellsX, CellsY, SearchCells; // SearchCells = ceil(max range / cell size)
        public int Tick, Slice;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            if (Slice > 1 && (i + Tick) % Slice != 0) return;
            if (Health[i] <= 0f) { Target[i] = -1; return; }

            float2 p = Positions[i];
            int owner = OwnerId[i];
            int type = Type[i];
            float range = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;
            int2 c = (int2)math.floor(p * InvCellSize);
            int2 lo = math.max(c - SearchCells, 0);
            int2 hi = math.min(c + SearchCells, new int2(CellsX - 1, CellsY - 1));

            float best = range;
            int bestIndex = -1;
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = CellStart[cell]; k < CellStart[cell + 1]; k++)
                {
                    int j = Sorted[k];
                    if (OwnerId[j] == owner || Health[j] <= 0f) continue;
                    float d = math.distancesq(p, Positions[j]);
                    if (d <= best) { best = d; bestIndex = j; }
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
                    if (BuildingOwnerId[j] == owner || BuildingHealth[j] <= 0f) continue;
                    float d = math.distancesq(p, BuildingPositions[j]);
                    if (d <= best) { best = d; bestIndex = j; }
                }
            }
            Target[i] = bestIndex;
            TargetKind[i] = bestIndex >= 0 ? TargetKinds.Building : TargetKinds.Unit;
        }
    }
}
