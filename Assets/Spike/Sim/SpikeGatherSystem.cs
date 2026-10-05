using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Stage 1: gather. Copies every unit's component into the SoA scratch arrays, builds the
    /// id-to-slot map and carries the target id. Slots are dense: the index the query gives a unit is
    /// the slot every later stage uses.
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpikeTickGroup))]
    public partial struct SpikeGatherSystem : ISystem
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
            state.Dependency = new GatherJob
            {
                Capacity = world.Capacity,
                Positions = world.Positions,
                Team = world.Team,
                SizeClass = world.SizeClass,
                Health = world.Health,
                Radius = world.Radius,
                Cooldown = world.Cooldown,
                FieldGoal = world.FieldGoal,
                IdOf = world.IdOf,
                IndexOfId = world.IndexOfId,
                Target = world.Target,
            }.ScheduleParallel(state.Dependency);
        }
    }

    /// <summary>
    /// The SoA copy. Every array is written at the unit's own slot, so the parallel-for restrictions
    /// are lifted on all of them.
    /// </summary>
    [BurstCompile]
    public partial struct GatherJob : IJobEntity
    {
        public int Capacity;

        [NativeDisableParallelForRestriction] public NativeArray<float2> Positions;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Team;
        [NativeDisableParallelForRestriction] public NativeArray<byte> SizeClass;
        [NativeDisableParallelForRestriction] public NativeArray<float> Health;
        [NativeDisableParallelForRestriction] public NativeArray<float> Radius;
        [NativeDisableParallelForRestriction] public NativeArray<float> Cooldown;
        [NativeDisableParallelForRestriction] public NativeArray<int> FieldGoal;
        [NativeDisableParallelForRestriction] public NativeArray<int> IdOf;
        [NativeDisableParallelForRestriction] public NativeArray<int> IndexOfId;
        [NativeDisableParallelForRestriction] public NativeArray<int> Target;

        private void Execute(Entity entity, in SpikeUnit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Capacity) return;

            Positions[index] = unit.Position;
            Team[index] = unit.Team;
            SizeClass[index] = unit.SizeClass;
            Health[index] = unit.Health;
            Radius[index] = unit.Radius;
            Cooldown[index] = unit.Cooldown;
            FieldGoal[index] = unit.FieldGoal;
            IdOf[index] = unit.Id;
            IndexOfId[unit.Id] = index;
            Target[index] = unit.TargetId; // a stable id until combat resolves it to this tick's slot
        }
    }
}
