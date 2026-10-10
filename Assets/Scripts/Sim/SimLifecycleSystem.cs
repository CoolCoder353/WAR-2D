using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace WAR2D.Sim
{
    /// <summary>
    /// Finds this tick's dead units and queues them in <see cref="SimData.Deaths"/>; the next boundary
    /// destroys them, frees their ids and records their explosions. Spawns arrive as commands.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimEconomySystem))]
    public partial struct SimLifecycleSystem : ISystem
    {
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            state.Dependency = new RecordDeathsJob { Deaths = data.Deaths.AsParallelWriter() }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>Queues every unit at 0 health.</summary>
    [BurstCompile]
    public partial struct RecordDeathsJob : IJobEntity
    {
        public NativeQueue<DeathRecord>.ParallelWriter Deaths;

        private void Execute(Entity entity, in Unit unit)
        {
            if (unit.Health <= 0f) Deaths.Enqueue(new DeathRecord { Entity = entity, Id = unit.Id, Position = unit.Position, OwnerId = unit.OwnerId, Killer = unit.LastHitBy });
        }
    }
}
