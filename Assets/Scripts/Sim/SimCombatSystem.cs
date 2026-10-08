using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Rules;

namespace WAR2D.Sim
{
    /// <summary>One hit, queued by the attack job and applied by a single writer.</summary>
    public struct DamageHit
    {
        public byte Kind;
        public int Slot;
        public float Amount;
    }

    /// <summary>
    /// Combat: resolve last tick's targets to this tick's slots, run the sliced nearest-enemy search, then
    /// attack on cooldown. Damage is per unit type times the damage table's multiplier for the target
    /// class. Unit damage lands in the SoA; building damage accumulates in
    /// <see cref="SimData.BuildingDamage"/> for the next boundary to apply.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimHashSystem))]
    public partial struct SimCombatSystem : ISystem
    {
        private NativeQueue<DamageHit> hits;
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            hits = new NativeQueue<DamageHit>(Allocator.Persistent);
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            hits.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            int count = clock.UnitCount;
            int buildings = math.min(data.BuildingCount[0], data.BuildingCapacity);
            if (data.AttackEvents.Capacity < count) data.AttackEvents.Capacity = count;

            state.Dependency = new ResolveTargetsJob
            {
                Positions = data.Positions,
                OwnerId = data.OwnerId,
                Health = data.Health,
                Type = data.Type,
                RangeSqByType = data.RangeSqByType,
                IdOf = data.IdOf,
                IndexOfId = data.IndexOfId,
                UnitCount = count,
                BuildingPositions = data.BuildingPositions,
                BuildingOwnerId = data.BuildingOwnerId,
                BuildingHealth = data.BuildingHealth,
                BuildingIds = data.BuildingIds,
                BuildingSlotOfIndex = data.BuildingSlotOfIndex,
                BuildingCount = buildings,
                Target = data.Target,
                TargetKind = data.TargetKind,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new NearestEnemyJob
            {
                Positions = data.Positions,
                OwnerId = data.OwnerId,
                Health = data.Health,
                Type = data.Type,
                RangeSqByType = data.RangeSqByType,
                CellStart = data.CellStart,
                Sorted = data.Sorted,
                BuildingPositions = data.BuildingPositions,
                BuildingOwnerId = data.BuildingOwnerId,
                BuildingHealth = data.BuildingHealth,
                BuildingCellStart = data.BuildingCellStart,
                BuildingSorted = data.BuildingSorted,
                InvCellSize = 1f / data.CellSize,
                CellsX = data.CellsX,
                CellsY = data.CellsY,
                SearchCells = data.SearchCells,
                Tick = clock.Tick % data.Slice,
                Slice = data.Slice,
                Target = data.Target,
                TargetKind = data.TargetKind,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new AttackJob
            {
                Health = data.Health,
                Type = data.Type,
                Cooldown = data.Cooldown,
                Target = data.Target,
                TargetKind = data.TargetKind,
                IdOf = data.IdOf,
                BuildingIds = data.BuildingIds,
                DamageByType = data.DamageByType,
                CooldownByType = data.CooldownByType,
                DamageTable = data.DamageTable,
                Hits = hits.AsParallelWriter(),
                Events = data.AttackEvents.AsParallelWriter(),
                Dt = data.Dt,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new ApplyDamageJob
            {
                Hits = hits,
                Health = data.Health,
                BuildingHealth = data.BuildingHealth,
                BuildingDamage = data.BuildingDamage,
            }.Schedule(state.Dependency);

            state.Dependency = new WriteBackCombatJob
            {
                Health = data.Health,
                Cooldown = data.Cooldown,
                Target = data.Target,
                TargetKind = data.TargetKind,
                IdOf = data.IdOf,
                BuildingIds = data.BuildingIds,
                Count = count,
            }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>
    /// Turns each unit's stored target id into this tick's slot, dropping targets that died, left range,
    /// changed hands or no longer exist.
    /// </summary>
    [BurstCompile]
    public struct ResolveTargetsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<int> OwnerId;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<float> RangeSqByType;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> IndexOfId;
        public int UnitCount;
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<int> BuildingOwnerId;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        [ReadOnly] public NativeArray<int> BuildingIds;
        [ReadOnly] public NativeArray<int> BuildingSlotOfIndex;
        public int BuildingCount;
        public NativeArray<int> Target;
        public NativeArray<byte> TargetKind;

        public void Execute(int i)
        {
            int id = Target[i];
            Target[i] = -1;
            if (Health[i] <= 0f || id <= 0) return;
            int index = NetIdAllocator.IndexOf(id);
            if (index >= IndexOfId.Length) return;
            int type = Type[i];
            float rangeSq = type < RangeSqByType.Length ? RangeSqByType[type] : 0f;

            if (TargetKind[i] == TargetKinds.Unit)
            {
                int j = IndexOfId[index];
                if ((uint)j >= (uint)UnitCount || IdOf[j] != id || Health[j] <= 0f || OwnerId[j] == OwnerId[i] ||
                    math.distancesq(Positions[i], Positions[j]) > rangeSq) return;
                Target[i] = j;
            }
            else
            {
                int j = BuildingSlotOfIndex[index];
                if ((uint)j >= (uint)BuildingCount || BuildingIds[j] != id || BuildingHealth[j] <= 0f ||
                    BuildingOwnerId[j] == OwnerId[i] || math.distancesq(Positions[i], BuildingPositions[j]) > rangeSq) return;
                Target[i] = j;
            }
        }
    }

    /// <summary>Attacks the resolved target when the cooldown allows; queues the hit and a tracer event.</summary>
    [BurstCompile]
    public struct AttackJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Type;
        public NativeArray<float> Cooldown;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<byte> TargetKind;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> BuildingIds;
        [ReadOnly] public NativeArray<float> DamageByType;
        [ReadOnly] public NativeArray<float> CooldownByType;
        [ReadOnly] public NativeArray<float> DamageTable;
        public NativeQueue<DamageHit>.ParallelWriter Hits;
        public NativeList<int2>.ParallelWriter Events;
        public float Dt;

        public void Execute(int i)
        {
            float cooldown = math.max(0f, Cooldown[i] - Dt);
            int target = Target[i];
            if (Health[i] <= 0f || target < 0 || cooldown > 0f)
            {
                Cooldown[i] = cooldown;
                return;
            }
            int type = Type[i];
            byte kind = TargetKind[i];
            float multiplier = WAR2D.Rules.DamageTable.Get(DamageTable, type, kind == TargetKinds.Unit ? TargetClass.Unit : TargetClass.Building);
            float damage = (type < DamageByType.Length ? DamageByType[type] : 0f) * multiplier;
            Hits.Enqueue(new DamageHit { Kind = kind, Slot = target, Amount = damage });
            Events.AddNoResize(new int2(IdOf[i], kind == TargetKinds.Unit ? IdOf[target] : BuildingIds[target]));
            Cooldown[i] = type < CooldownByType.Length ? CooldownByType[type] : 1f;
        }
    }

    /// <summary>The single writer of damage: units lose health in the SoA, buildings accumulate it.</summary>
    [BurstCompile]
    public struct ApplyDamageJob : IJob
    {
        public NativeQueue<DamageHit> Hits;
        public NativeArray<float> Health;
        public NativeArray<float> BuildingHealth;
        public NativeArray<float> BuildingDamage;

        public void Execute()
        {
            while (Hits.TryDequeue(out DamageHit hit))
            {
                if (hit.Kind == TargetKinds.Unit)
                {
                    Health[hit.Slot] = math.max(0f, Health[hit.Slot] - hit.Amount);
                }
                else
                {
                    BuildingHealth[hit.Slot] = math.max(0f, BuildingHealth[hit.Slot] - hit.Amount);
                    BuildingDamage[hit.Slot] += hit.Amount;
                }
            }
        }
    }

    /// <summary>Writes health, cooldown and the target (as a stable id) back to each unit.</summary>
    [BurstCompile]
    public partial struct WriteBackCombatJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Cooldown;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<byte> TargetKind;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> BuildingIds;
        public int Count;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Count) return;
            unit.Health = Health[index];
            unit.Cooldown = Cooldown[index];
            int target = Target[index];
            byte kind = TargetKind[index];
            unit.TargetKind = kind;
            unit.TargetId = target < 0 ? -1 : kind == TargetKinds.Unit ? IdOf[target] : BuildingIds[target];
        }
    }
}
