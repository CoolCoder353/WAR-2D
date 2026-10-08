using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>Writes each position's hash cell, clamped to the grid.</summary>
    [BurstCompile]
    public struct CellIndexJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        public NativeArray<int> Cell;
        public float InvCellSize;
        public int CellsX, CellsY;

        public void Execute(int i)
        {
            int2 c = math.clamp((int2)math.floor(Positions[i] * InvCellSize), 0, new int2(CellsX - 1, CellsY - 1));
            Cell[i] = c.y * CellsX + c.x;
        }
    }

    /// <summary>
    /// Counting sort of slots by cell: afterwards cell c's slots are
    /// <c>Sorted[CellStart[c] .. CellStart[c + 1])</c>. Single-threaded, O(slots + cells).
    /// </summary>
    [BurstCompile]
    public struct CountingSortJob : IJob
    {
        [ReadOnly] public NativeArray<int> Cell;
        public NativeArray<int> CellStart; // length cells + 1
        public NativeArray<int> Sorted;    // length slots

        public void Execute()
        {
            for (int c = 0; c < CellStart.Length; c++) CellStart[c] = 0;
            for (int i = 0; i < Cell.Length; i++) CellStart[Cell[i] + 1]++;
            for (int c = 1; c < CellStart.Length; c++) CellStart[c] += CellStart[c - 1];
            var cursor = new NativeArray<int>(CellStart.Length - 1, Allocator.Temp);
            NativeArray<int>.Copy(CellStart, cursor, cursor.Length);
            for (int i = 0; i < Cell.Length; i++) Sorted[cursor[Cell[i]]++] = i;
            cursor.Dispose();
        }
    }
}
