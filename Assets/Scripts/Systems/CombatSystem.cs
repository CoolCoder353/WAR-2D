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
        // Query for all potential targets with health (units or buildings)
        var targetQuery = SystemAPI.QueryBuilder()
            .WithAll<HealthComponent, LocalTransform>()
            .Build();

        var targetEntities = targetQuery.ToEntityArray(state.WorldUpdateAllocator);
        var targetHealths = targetQuery.ToComponentDataArray<HealthComponent>(state.WorldUpdateAllocator);
        var targetTransforms = targetQuery.ToComponentDataArray<LocalTransform>(state.WorldUpdateAllocator);

        foreach (var (damageComp, localTransform, clientUnit, entity) in SystemAPI.Query<RefRO<DamageComponent>, RefRO<LocalTransform>, RefRW<ClientUnit>>().WithEntityAccess())
        {
            int bestTargetId = -1;
            float minDistance = damageComp.ValueRO.range;
            int currentTargetIndex = -1;

            Debug.Log($"Unit {entity} (Owner {clientUnit.ValueRO.ownerId}) is searching for targets...");
            for (int i = 0; i < targetEntities.Length; i++)
            {
                // Skip self    
                if (targetEntities[i] == entity) continue;
                // Check ownership - skip if we own this target
                int targetOwnerId = GetTargetOwnerId(targetEntities[i], ref state);

                Debug.Log($"Evaluating target entity {targetEntities[i]}, owned by player {targetOwnerId}");

                if (targetOwnerId == clientUnit.ValueRO.ownerId)
                {
                    Debug.Log($"Skipping own target with ID {targetOwnerId}");
                    continue;
                }


                float distance = math.distance(localTransform.ValueRO.Position, targetTransforms[i].Position);

                if (distance <= minDistance)
                {
                    // Found a valid target in range
                    minDistance = distance;
                    bestTargetId = i; // Store index instead of unit ID
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

    /// <summary>
    /// Gets the owner ID of a target entity by checking for ClientUnit or BuildingData components.
    /// Returns -1 if the entity has no owner.
    /// </summary>
    private int GetTargetOwnerId(Entity targetEntity, ref SystemState state)
    {
        var ecb = state.EntityManager;

        // Try to get owner from ClientUnit
        if (ecb.HasComponent<ClientUnit>(targetEntity))
        {
            return ecb.GetComponentData<ClientUnit>(targetEntity).ownerId;
        }

        // Try to get owner from BuildingData
        if (ecb.HasComponent<BuildingData>(targetEntity))
        {
            return ecb.GetComponentData<BuildingData>(targetEntity).ownerId;
        }

        // No owner found
        return -1;
    }
}
