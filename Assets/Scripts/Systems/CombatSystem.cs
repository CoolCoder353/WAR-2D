using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Mirror;
using System;
using Unity.Collections;

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

            if (clientUnit.ValueRO.targetId != -1)
            {
                // If we have a target, we need to check if it's still valid
                int targetIndex = TryFindIndex(targetEntities, clientUnit.ValueRO.targetId);

                if (targetIndex != -1)
                {
                    var targetHealth = targetHealths[targetIndex];
                    var targetPosition = targetTransforms[targetIndex].Position;
                    float distanceSq = math.distancesq(localTransform.ValueRO.Position, targetPosition);

                    // Check if target is still alive and within range
                    if (targetHealth.currentHealth > 0 && distanceSq <= damageComp.ValueRO.range * damageComp.ValueRO.range)
                    {
                        // Attack the target
                        AttackTarget(targetIndex, ref state, clientUnit, damageComp, targetEntities, targetHealths);
                        continue; // Continue to next attacker
                    }
                }
            }
            // Either no target or target is invalid, find a new target
            int attackerOwnerId = clientUnit.ValueRO.ownerId;
            int newTargetIndex = FindNewTarget(entity, localTransform.ValueRO.Position, damageComp.ValueRO.range, attackerOwnerId, targetEntities, targetHealths, targetTransforms, ref state);

            if (newTargetIndex != -1) Debug.Log($"Entity {entity.Index} found new target index: {newTargetIndex}");

            clientUnit.ValueRW.targetId = newTargetIndex != -1 ? targetEntities[newTargetIndex].Index : -1;
            clientUnit.ValueRW.lastAttackTime = SystemAPI.Time.ElapsedTime;

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

    private static int TryFindIndex(NativeArray<Entity> entities, int targetEntityId)
    {
        for (int i = 0; i < entities.Length; i++)
        {
            if (entities[i].Index == targetEntityId)
            {
                return i;
            }
        }
        return -1;
    }

    private int FindNewTarget(Entity attackerEntity, float3 attackerPosition, float range, int attackerOwnerId, NativeArray<Entity> targetEntities, NativeArray<HealthComponent> targetHealths, NativeArray<LocalTransform> targetTransforms, ref SystemState state)
    {
        int closestTargetIndex = -1;
        float closestDistanceSq = float.MaxValue;

        for (int i = 0; i < targetEntities.Length; i++)
        {
            // Skip dead targets
            if (targetHealths[i].currentHealth <= 0)
                continue;

            // Skip targets owned by the same player
            int targetOwnerId = GetTargetOwnerId(targetEntities[i], ref state);
            if (targetOwnerId == attackerOwnerId)
                continue;

            float3 targetPosition = targetTransforms[i].Position;
            float distanceSq = math.distancesq(attackerPosition, targetPosition);

            if (distanceSq <= range * range && distanceSq < closestDistanceSq)
            {
                closestDistanceSq = distanceSq;
                closestTargetIndex = i;
            }
        }

        return closestTargetIndex;
    }


    private void AttackTarget(int targetIndex, ref SystemState state, RefRW<ClientUnit> clientUnit, RefRO<DamageComponent> damageComp, NativeArray<Entity> targetEntities, NativeArray<HealthComponent> targetHealths)
    {

        if (SystemAPI.Time.ElapsedTime > clientUnit.ValueRO.lastAttackTime + damageComp.ValueRO.attackSpeed)
        {
            // Perform attack
            clientUnit.ValueRW.lastAttackTime = SystemAPI.Time.ElapsedTime;

            // Apply damage
            var targetHealth = targetHealths[targetIndex];
            targetHealth.currentHealth -= damageComp.ValueRO.damageAmount;

            SystemAPI.SetComponent(targetEntities[targetIndex], targetHealth);

            // Handle death
            if (targetHealth.currentHealth <= 0)
            {
                var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
                ecb.DestroyEntity(targetEntities[targetIndex]);
            }
        }

    }
}
