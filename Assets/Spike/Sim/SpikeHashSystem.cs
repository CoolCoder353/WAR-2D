using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Stage 2: the spatial hash. Indexes every unit into its cell (clamped to the grid) and counting
    /// sorts the units by cell, so the search and the separation pass walk neighbours through
    /// <c>CellStart</c>/<c>Sorted</c> (Task 5's two jobs, unchanged).
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpikeTickGroup))]
    [UpdateAfter(typeof(SpikeGatherSystem))]
    public partial struct SpikeHashSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SpikeWorldData world = SystemAPI.GetSingleton<SpikeWorldData>();

            // The hash's inputs and outputs are the SoA arrays, which live inside the SpikeWorldData
            // singleton: the dependency manager tracks components, not the arrays inside them, so this
            // system would otherwise have nothing in common with the gather and the stages after it.
            // Declaring it a writer of SpikeUnit orders it after every unit reader (the gather) and
            // before every later stage, which is exactly the chain this stage needs - without a sync
            // point. The handle itself is not used; the declaration is the point.
            _ = state.GetComponentTypeHandle<SpikeUnit>(isReadOnly: false);

            state.Dependency = new CellIndexJob
            {
                Positions = world.Positions,
                Cell = world.Cell,
                InvCellSize = 1f / world.CellSize,
                CellsX = world.CellsX,
                CellsY = world.CellsY,
            }.Schedule(world.Capacity, 256, state.Dependency);
            state.Dependency = new CountingSortJob
            {
                Cell = world.Cell,
                CellStart = world.CellStart,
                Sorted = world.Sorted,
            }.Schedule(state.Dependency);
        }
    }
}
