using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace WAR2D.Sim
{
    /// <summary>
    /// Copies every <see cref="Unit"/> into <see cref="SimData"/>'s SoA arrays (one slot per unit, in
    /// query order), applying the orders the command system queued. On the main thread it
    /// counts the units and gathers the few hundred building targets.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimCommandSystem))]
    public partial struct SimGatherSystem : ISystem
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
            RefRW<SimClock> clock = SystemAPI.GetSingletonRW<SimClock>();
            if (!clock.ValueRO.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();

            int count = math.min(units.CalculateEntityCount(), data.Capacity);
            clock.ValueRW.UnitCount = count;

            // Buildings: no tick job touches building components, so the main thread reads them here.
            int b = 0;
            foreach (var (building, health, transform, entity) in
                     SystemAPI.Query<RefRO<BuildingData>, RefRO<HealthComponent>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                if (b >= data.BuildingCapacity) break;
                int id = building.ValueRO.id;
                data.BuildingPositions[b] = transform.ValueRO.Position.xy;
                data.BuildingOwnerId[b] = building.ValueRO.ownerId;
                data.BuildingOwnerSlot[b] = data.OwnerSlotOf(building.ValueRO.ownerId);
                int buildingType = (int)building.ValueRO.buildingType;
                data.BuildingSight[b] = (uint)buildingType < (uint)data.BuildingSightByType.Length ? data.BuildingSightByType[buildingType] : 0f;
                data.BuildingHealth[b] = health.ValueRO.currentHealth;
                data.BuildingDamage[b] = 0f;
                data.BuildingIds[b] = id;
                data.BuildingEntities[b] = entity;
                int index = NetIdAllocator.IndexOf(id);
                if (index < data.BuildingSlotOfIndex.Length) data.BuildingSlotOfIndex[index] = b;
                b++;
            }
            data.BuildingCount[0] = b;

            state.Dependency = new GatherJob
            {
                Capacity = data.Capacity,
                Positions = data.Positions,
                Velocity = data.Velocity,
                OwnerId = data.OwnerId,
                OwnerSlot = data.OwnerSlot,
                Type = data.Type,
                SizeClass = data.SizeClass,
                Health = data.Health,
                MaxHealth = data.MaxHealth,
                Radius = data.Radius,
                Cooldown = data.Cooldown,
                LastHitBy = data.LastHitBy,
                OrderSlot = data.OrderSlot,
                Unpaid = data.Unpaid,
                Stance = data.Stance,
                IdOf = data.IdOf,
                IndexOfId = data.IndexOfId,
                Target = data.Target,
                TargetKind = data.TargetKind,
                PendingOrders = data.PendingOrders,
                PendingMoves = data.PendingMoves,
            }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>The gather: one unit into its slot of every SoA array.</summary>
    [BurstCompile]
    public partial struct GatherJob : IJobEntity
    {
        public int Capacity;
        [NativeDisableParallelForRestriction] public NativeArray<float2> Positions;
        [NativeDisableParallelForRestriction] public NativeArray<float2> Velocity;
        [NativeDisableParallelForRestriction] public NativeArray<int> OwnerId;
        [NativeDisableParallelForRestriction] public NativeArray<byte> OwnerSlot;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Type;
        [NativeDisableParallelForRestriction] public NativeArray<byte> SizeClass;
        [NativeDisableParallelForRestriction] public NativeArray<float> Health;
        [NativeDisableParallelForRestriction] public NativeArray<float> MaxHealth;
        [NativeDisableParallelForRestriction] public NativeArray<float> Radius;
        [NativeDisableParallelForRestriction] public NativeArray<float> Cooldown;
        [NativeDisableParallelForRestriction] public NativeArray<int> LastHitBy;
        [NativeDisableParallelForRestriction] public NativeArray<int> OrderSlot;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Unpaid;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Stance;
        [NativeDisableParallelForRestriction] public NativeArray<int> IdOf;
        [NativeDisableParallelForRestriction] public NativeArray<int> IndexOfId;
        [NativeDisableParallelForRestriction] public NativeArray<int> Target;
        [NativeDisableParallelForRestriction] public NativeArray<byte> TargetKind;
        [ReadOnly] public NativeParallelHashMap<int, PendingOrder> PendingOrders;
        [ReadOnly] public NativeParallelHashMap<int, float2> PendingMoves;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Capacity) return;
            if (PendingOrders.TryGetValue(unit.Id, out PendingOrder order))
            {
                unit.OrderSlot = order.Slot;
                unit.Stance = order.Stance;
            }
            if (PendingMoves.TryGetValue(unit.Id, out float2 moved)) unit.Position = moved;

            Positions[index] = unit.Position;
            Velocity[index] = unit.Velocity;
            OwnerId[index] = unit.OwnerId;
            OwnerSlot[index] = unit.OwnerSlot;
            Type[index] = unit.Type;
            SizeClass[index] = unit.SizeClass;
            Health[index] = unit.Health;
            MaxHealth[index] = unit.MaxHealth;
            Radius[index] = unit.Radius;
            Cooldown[index] = unit.Cooldown;
            LastHitBy[index] = unit.LastHitBy;
            OrderSlot[index] = unit.OrderSlot;
            Unpaid[index] = unit.Unpaid;
            Stance[index] = unit.Stance;
            IdOf[index] = unit.Id;
            int idIndex = NetIdAllocator.IndexOf(unit.Id);
            if (idIndex < IndexOfId.Length) IndexOfId[idIndex] = index;
            Target[index] = unit.TargetId; // a stable id until combat resolves it to this tick's slot
            TargetKind[index] = unit.TargetKind;
        }
    }
}
