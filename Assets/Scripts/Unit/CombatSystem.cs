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
            // Find a target if we don't have one or if the current one is invalid/out of range
            // For simplicity, we'll re-evaluate targets every frame or check if current target is valid
            // In a more optimized system, we might cache the target entity.
            // However, since ClientUnit stores targetId (int), we need to map that back to an entity or search again.
            // Given the requirement to update ClientUnit.targetId, let's search for the best target.

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
                    // Simple logic: pick the first one or closest. Let's pick closest.
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
                    
                    // Update the component on the target entity
                    // We need to use SystemAPI.SetComponent or similar. 
                    // Since we have arrays, we can't directly modify the array to update the entity.
                    // We need to look up the entity again or use a component lookup.
                    
                    // Optimization: Create a ComponentLookup for HealthComponent
                    // But for now, let's just use SetComponentData since we have the entity.
                    SystemAPI.SetComponent(targetEntities[currentTargetIndex], targetHealth);

                    Debug.Log($"HEHEHEH Unit {clientUnit.ValueRO.id} attacked Unit {bestTargetId}. Target Health: {targetHealth.currentHealth}");

                    // Handle death
                    if (targetHealth.currentHealth <= 0)
                    {
                        // Destroy entity or mark as dead.
                        // For now, let's just destroy it.
                        // Using a command buffer is safer for structural changes.
                        var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
                        ecb.DestroyEntity(targetEntities[currentTargetIndex]);
                        
                        Debug.Log($"Unit {bestTargetId} died.");
                    }
                }
            }
        }
    }
}
