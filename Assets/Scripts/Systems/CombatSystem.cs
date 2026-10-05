using Mirror;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// Units keep or acquire the nearest enemy (unit or building) in range and damage it every attack interval.
/// Destruction is left to DestructionSystem. Server only, Playing state only.
/// </summary>
public partial struct CombatSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<DamageComponent>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return;

        var targetQuery = SystemAPI.QueryBuilder().WithAll<HealthComponent, LocalTransform>().Build();
        NativeArray<Entity> targetEntities = targetQuery.ToEntityArray(Allocator.Temp);
        NativeArray<HealthComponent> targetHealths = targetQuery.ToComponentDataArray<HealthComponent>(Allocator.Temp);
        NativeArray<LocalTransform> targetTransforms = targetQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        int count = targetEntities.Length;
        var positions = new NativeArray<float3>(count, Allocator.Temp);
        var owners = new NativeArray<int>(count, Allocator.Temp);
        var healths = new NativeArray<float>(count, Allocator.Temp);
        var indexById = new NativeHashMap<int, int>(count, Allocator.Temp);
        for (int i = 0; i < count; i++)
        {
            positions[i] = targetTransforms[i].Position;
            healths[i] = targetHealths[i].currentHealth;
            owners[i] = OwnerOf(ref state, targetEntities[i]);
            indexById[targetHealths[i].entityId] = i;
        }

        double now = SystemAPI.Time.ElapsedTime;
        foreach (var (damage, transform, unit) in SystemAPI.Query<RefRO<DamageComponent>, RefRO<LocalTransform>, RefRW<ClientUnit>>())
        {
            float range = damage.ValueRO.range;
            int targetIndex = -1;

            if (unit.ValueRO.targetId != -1 && indexById.TryGetValue(unit.ValueRO.targetId, out int current)
                && healths[current] > 0f && math.distancesq(transform.ValueRO.Position, positions[current]) <= range * range)
            {
                targetIndex = current;
            }
            else
            {
                targetIndex = CombatRules.FindNearestEnemy(transform.ValueRO.Position, range, unit.ValueRO.ownerId, positions, owners, healths);
                unit.ValueRW.targetId = targetIndex == -1 ? -1 : targetHealths[targetIndex].entityId;
            }

            if (targetIndex == -1 || !CombatRules.IsAttackReady(now, unit.ValueRO.lastAttackTime, damage.ValueRO.attackSpeed)) continue;

            unit.ValueRW.lastAttackTime = now;
            healths[targetIndex] -= damage.ValueRO.damageAmount;
            HealthComponent hp = SystemAPI.GetComponent<HealthComponent>(targetEntities[targetIndex]);
            hp.currentHealth = healths[targetIndex];
            SystemAPI.SetComponent(targetEntities[targetIndex], hp);
        }

        targetEntities.Dispose(); targetHealths.Dispose(); targetTransforms.Dispose();
        positions.Dispose(); owners.Dispose(); healths.Dispose(); indexById.Dispose();
    }

    private static int OwnerOf(ref SystemState state, Entity entity)
    {
        EntityManager em = state.EntityManager;
        if (em.HasComponent<ClientUnit>(entity)) return em.GetComponentData<ClientUnit>(entity).ownerId;
        if (em.HasComponent<BuildingData>(entity)) return em.GetComponentData<BuildingData>(entity).ownerId;
        return -1;
    }
}
