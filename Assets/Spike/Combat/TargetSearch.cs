using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Nearest living enemy within range, searched by 1 / Slice of the units each tick (staggered by index).
    /// Units not searched this tick keep their previous Target.
    /// </summary>
    [BurstCompile]
    public struct NearestEnemyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> RangeSq;
        [ReadOnly] public NativeArray<int> CellStart, Sorted;
        public float InvCellSize;
        public int CellsX, CellsY, SearchCells; // SearchCells = ceil(maxRange / CellSize)
        public int Tick, Slice;
        public NativeArray<int> Target;

        public void Execute(int i)
        {
            if (Slice > 1 && (i + Tick) % Slice != 0) return;
            if (Health[i] <= 0f) { Target[i] = -1; return; }

            float2 p = Positions[i];
            byte team = Team[i];
            float best = RangeSq[i];
            int bestIndex = -1;
            int2 c = (int2)math.floor(p * InvCellSize);
            int2 lo = math.max(c - SearchCells, 0);
            int2 hi = math.min(c + SearchCells, new int2(CellsX - 1, CellsY - 1));
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = CellStart[cell]; k < CellStart[cell + 1]; k++)
                {
                    int j = Sorted[k];
                    if (Team[j] == team || Health[j] <= 0f) continue;
                    float d = math.distancesq(p, Positions[j]);
                    if (d <= best) { best = d; bestIndex = j; }
                }
            }
            Target[i] = bestIndex;
        }
    }
}
