using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>Builds the counting-sort spatial hashes of units and building targets for this tick.</summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimGatherSystem))]
    public partial struct SimHashSystem : ISystem
    {
        public void OnCreate(ref SystemState state) => state.RequireForUpdate<SimData>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            // Declared, not used: being a writer of Unit chains this stage after the gather and before
            // the later stages (see SimData).
            _ = state.GetComponentTypeHandle<Unit>(isReadOnly: false);

            int count = clock.UnitCount;
            float inv = 1f / data.CellSize;
            JobHandle units = new CellIndexJob
            {
                Positions = data.Positions.GetSubArray(0, count),
                Cell = data.Cell.GetSubArray(0, count),
                InvCellSize = inv, CellsX = data.CellsX, CellsY = data.CellsY,
            }.Schedule(count, 256, state.Dependency);
            units = new CountingSortJob
            {
                Cell = data.Cell.GetSubArray(0, count),
                CellStart = data.CellStart,
                Sorted = data.Sorted.GetSubArray(0, count),
            }.Schedule(units);

            int buildings = math.min(data.BuildingCount[0], data.BuildingCapacity);
            JobHandle b = new CellIndexJob
            {
                Positions = data.BuildingPositions.GetSubArray(0, buildings),
                Cell = data.BuildingCell.GetSubArray(0, buildings),
                InvCellSize = inv, CellsX = data.CellsX, CellsY = data.CellsY,
            }.Schedule(buildings, 64, state.Dependency);
            b = new CountingSortJob
            {
                Cell = data.BuildingCell.GetSubArray(0, buildings),
                CellStart = data.BuildingCellStart,
                Sorted = data.BuildingSorted.GetSubArray(0, buildings),
            }.Schedule(b);

            state.Dependency = JobHandle.CombineDependencies(units, b);
        }
    }
}
