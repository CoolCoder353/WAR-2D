using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Stage 3: combat. Validates the target each unit carried in (id to slot, alive, enemy, still in
    /// range), refreshes the target on the unit's search slice, queues attacks from every unit whose
    /// cooldown has run out, applies the queue in one pass so many attackers can hit one target, then
    /// writes health, cooldown and the target id back into the components.
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpikeTickGroup))]
    [UpdateAfter(typeof(SpikeHashSystem))]
    public partial struct SpikeCombatSystem : ISystem
    {
        private NativeQueue<int2> queue;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            queue = new NativeQueue<int2>(Allocator.Persistent);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            queue.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SpikeWorldData world = SystemAPI.GetSingleton<SpikeWorldData>();
            int tick = (int)math.round(SystemAPI.Time.ElapsedTime / world.Dt);

            state.Dependency = new ResolveTargetsJob
            {
                Capacity = world.Capacity,
                Positions = world.Positions,
                Team = world.Team,
                Health = world.Health,
                RangeSq = world.RangeSq,
                IdOf = world.IdOf,
                IndexOfId = world.IndexOfId,
                Target = world.Target,
            }.Schedule(world.Capacity, 256, state.Dependency);

            state.Dependency = new NearestEnemyJob
            {
                Positions = world.Positions,
                Team = world.Team,
                Health = world.Health,
                RangeSq = world.RangeSq,
                CellStart = world.CellStart,
                Sorted = world.Sorted,
                InvCellSize = 1f / world.CellSize,
                CellsX = world.CellsX,
                CellsY = world.CellsY,
                SearchCells = world.SearchCells,
                Tick = tick % world.Slice,
                Slice = world.Slice,
                Target = world.Target,
            }.Schedule(world.Capacity, 256, state.Dependency);

            state.Dependency = new AttackJob
            {
                Capacity = world.Capacity,
                Health = world.Health,
                Cooldown = world.Cooldown,
                Target = world.Target,
                Damage = queue.AsParallelWriter(),
                DamageFixed = (int)math.round(world.AttackDamage * 100f),
                CooldownSeconds = world.AttackCooldown,
                Dt = world.Dt,
            }.Schedule(world.Capacity, 256, state.Dependency);

            state.Dependency = new ApplyDamageJob
            {
                Damage = queue,
                Health = world.Health,
            }.Schedule(state.Dependency);

            state.Dependency = new WriteBackCombatJob
            {
                Health = world.Health,
                Cooldown = world.Cooldown,
                Target = world.Target,
                IdOf = world.IdOf,
            }.ScheduleParallel(state.Dependency);
        }
    }

    /// <summary>
    /// Turns the target id the gather carried over into this tick's slot, or -1 when the target is
    /// gone, dead, friendly, out of range or its slot has since been reused by another unit (the id
    /// at the slot has to match, which is what makes a stale <c>IndexOfId</c> entry harmless).
    /// </summary>
    [BurstCompile]
    public struct ResolveTargetsJob : IJobParallelFor
    {
        public int Capacity;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> RangeSq;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> IndexOfId;
        public NativeArray<int> Target;

        public void Execute(int i)
        {
            if (i >= Capacity) return;
            if (Health[i] <= 0f)
            {
                Target[i] = -1;
                return;
            }

            int id = Target[i];
            if ((uint)id >= (uint)IndexOfId.Length)
            {
                Target[i] = -1;
                return;
            }

            int j = IndexOfId[id];
            if ((uint)j >= (uint)Capacity || IdOf[j] != id || Health[j] <= 0f || Team[j] == Team[i] ||
                math.distancesq(Positions[i], Positions[j]) > RangeSq[i])
            {
                Target[i] = -1;
                return;
            }

            Target[i] = j;
        }
    }

    /// <summary>
    /// Every unit in range of a target attacks when its cooldown has run out, resetting the cooldown;
    /// everything else ticks its cooldown down. Attacks go to the queue because many units can attack
    /// the same target in the same tick.
    /// </summary>
    [BurstCompile]
    public struct AttackJob : IJobParallelFor
    {
        public int Capacity;
        [ReadOnly] public NativeArray<float> Health;
        public NativeArray<float> Cooldown;
        [ReadOnly] public NativeArray<int> Target;
        public NativeQueue<int2>.ParallelWriter Damage;
        public int DamageFixed;
        public float CooldownSeconds, Dt;

        public void Execute(int i)
        {
            if (i >= Capacity) return;
            float cooldown = Cooldown[i];
            if (Health[i] <= 0f || Target[i] < 0)
            {
                Cooldown[i] = math.max(0f, cooldown - Dt);
                return;
            }

            if (cooldown <= 0f)
            {
                Damage.Enqueue(new int2(Target[i], DamageFixed));
                Cooldown[i] = CooldownSeconds;
            }
            else
            {
                Cooldown[i] = math.max(0f, cooldown - Dt);
            }
        }
    }

    /// <summary>Applies every queued attack, clamping health at 0 so it never goes negative.</summary>
    [BurstCompile]
    public struct ApplyDamageJob : IJob
    {
        public NativeQueue<int2> Damage;
        public NativeArray<float> Health;

        public void Execute()
        {
            while (Damage.TryDequeue(out int2 hit))
            {
                int slot = hit.x;
                Health[slot] = math.max(0f, Health[slot] - hit.y * 0.01f);
            }
        }
    }

    /// <summary>Writes health, cooldown and the target's stable id back into the unit components.</summary>
    [BurstCompile]
    public partial struct WriteBackCombatJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Cooldown;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<int> IdOf;

        private void Execute(ref SpikeUnit unit, [EntityIndexInQuery] int index)
        {
            unit.Health = Health[index];
            unit.Cooldown = Cooldown[index];
            int target = Target[index];
            unit.TargetId = target >= 0 ? IdOf[target] : -1;
        }
    }
}
