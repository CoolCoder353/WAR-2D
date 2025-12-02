using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Mirror;

[BurstCompile]
public partial struct CombatSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<DamageComponent>();
        state.RequireForUpdate<ClientUnit>();
        state.RequireForUpdate<LocalTransform>();
    }

    [Server]
    public void OnUpdate(ref SystemState state)
    {
        // Query for potential targets
        var targetQuery = SystemAPI.QueryBuilder()
            .WithAll<HealthComponent, ClientUnit, LocalTransform>()
            .Build();

        var targetEntities = targetQuery.ToEntityArray(state.WorldUpdateAllocator);
        var targetHealths = targetQuery.ToComponentDataArray<HealthComponent>(state.WorldUpdateAllocator);
        var targetClientUnits = targetQuery.ToComponentDataArray<ClientUnit>(state.WorldUpdateAllocator);
        var targetTransforms = targetQuery.ToComponentDataArray<LocalTransform>(state.WorldUpdateAllocator);

        foreach (var (damageComp, localTransform, clientUnit, entity) in SystemAPI.Query<RefRO<DamageComponent>, RefRO<LocalTransform>, RefRW<ClientUnit>>().WithEntityAccess())
        {
            int bestTargetId = -1;
            float minDistance = damageComp.ValueRO.range;
            int currentTargetIndex = -1;

            for (int i = 0; i < targetEntities.Length; i++)
            {
                // Skip self
                if (targetEntities[i] == entity) continue;

                // Skip friendly units
                if (targetClientUnits[i].ownerId == clientUnit.ValueRO.ownerId) continue;

                float distance = math.distance(localTransform.ValueRO.Position, targetTransforms[i].Position);

                if (distance <= minDistance)
                {
                    // Found a valid target in range
                    minDistance = distance;
                    bestTargetId = targetClientUnits[i].id;
                    currentTargetIndex = i;
                }
            }

            // Update target ID
            clientUnit.ValueRW.targetId = bestTargetId;

            // Attack logic
            if (bestTargetId != -1 && currentTargetIndex != -1)
            {
                if (SystemAPI.Time.ElapsedTime > clientUnit.ValueRO.lastAttackTime + damageComp.ValueRO.attackSpeed)
                {
                    // Perform attack
                    clientUnit.ValueRW.lastAttackTime = SystemAPI.Time.ElapsedTime;

                    // Apply damage
                    var targetHealth = targetHealths[currentTargetIndex];
                    targetHealth.currentHealth -= damageComp.ValueRO.damageAmount;
                    
                    SystemAPI.SetComponent(targetEntities[currentTargetIndex], targetHealth);

                    // Handle death
                    if (targetHealth.currentHealth <= 0)
                    {
                        var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
                        ecb.DestroyEntity(targetEntities[currentTargetIndex]);
                    }
                }
            }
        }
    }
}
