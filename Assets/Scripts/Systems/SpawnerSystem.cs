using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Mirror;
using Config;

/// <summary>
/// System responsible for spawning units from spawners.
/// Handles resource checking, entity creation, and component initialization.
/// </summary>
[BurstCompile]
public partial struct SpawnerSystem : ISystem
{
    /// <summary>
    /// Called every frame on the server to handle spawning logic.
    /// </summary>
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        // Create a single command buffer for all spawning operations
        EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        NativeList<int> spawnedUnitIds = new NativeList<int>(Allocator.Temp);
        GameConfigData config = ConfigLoader.LoadConfig();

        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            if (spawnerData.ValueRW.count > 0 && WorldStateManager.Instance != null)
            {
                Debug.Log("SpawnerSystem: Spawning unit");
                spawnerData.ValueRW.count--;

                // Set the ClientUnit component to the new entity.
                int id = UnityEngine.Random.Range(0, int.MaxValue);
                int idOfOwner = spawnerData.ValueRO.ownerId;

                Entity createdEntity = CreateUnit(commandBuffer, id, idOfOwner, spawnerData.ValueRO.position, spawnerData.ValueRO.unitType, config);

                // Only add to list if entity was successfully created (had sufficient resources)
                if (createdEntity != Entity.Null)
                {
                    // Store the unit ID to find the entity after playback
                    spawnedUnitIds.Add(id);
                }
                else
                {
                    // Restore count if spawn failed due to insufficient resources
                    spawnerData.ValueRW.count++;
                }
            }
        }

        // Play back all commands after iteration is complete
        commandBuffer.Playback(state.EntityManager);
        commandBuffer.Dispose();

        // Now find and add all spawned units to WorldStateManager
        if (WorldStateManager.Instance != null)
        {
            foreach (int unitId in spawnedUnitIds)
            {
                // Find the entity by its ClientUnit.id component
                foreach (var (clientUnit, entity) in SystemAPI.Query<RefRO<ClientUnit>>().WithEntityAccess())
                {
                    if (clientUnit.ValueRO.id == unitId)
                    {
                        WorldStateManager.Instance.AddUnit(entity, unitId);
                        break;
                    }
                }
            }
        }

        spawnedUnitIds.Dispose();
    }

    /// <summary>
    /// Creates a unit entity with the specified parameters.
    /// Checks for sufficient resources before creation.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to record the creation commands.</param>
    /// <param name="id">The unique ID of the unit.</param>
    /// <param name="idOfOwner">The ID of the unit's owner.</param>
    /// <param name="position">The spawn position.</param>
    /// <param name="unitType">The type of unit to spawn.</param>
    /// <param name="config">The game configuration data.</param>
    /// <returns>The created Entity, or Entity.Null if creation failed (e.g., insufficient resources).</returns>
    [Server]
    public Entity CreateUnit(EntityCommandBuffer commandBuffer, int id, int idOfOwner, float2 position, UnitType unitType, GameConfigData config)
    {
        // Check if owner has sufficient resources
        if (GameCore.Instance != null)
        {
            ServerPlayer owner = GameCore.Instance.GetServerPlayerById(idOfOwner);
            if (owner != null)
            {
                string typeStr = unitType.ToString();
                if (!config.Units.TryGetValue(typeStr, out UnitConfig unitConfig))
                {
                    Debug.LogError($"Unit type {typeStr} not found in config.");
                    return Entity.Null;
                }
                
                if (owner.data.resources < unitConfig.UpfrontCost)
                {
                    return Entity.Null;
                }
                
                // Deduct upfront cost
                owner.RemoveResources(unitConfig.UpfrontCost);
                
                // Update client display
                if (owner.connection != null && owner.connection.identity != null)
                {
                    ClientPlayer clientPlayer = owner.connection.identity.GetComponent<ClientPlayer>();
                    if (clientPlayer != null)
                    {
                        clientPlayer.TargetUpdateResources(owner.connection, owner.data.resources);
                    }
                }

                Entity newEntity = commandBuffer.CreateEntity();

                commandBuffer.AddComponent(newEntity, new LocalTransform { Position = new float3(position.x, position.y, 0) });
                commandBuffer.AddComponent(newEntity, new HealthComponent { currentHealth = unitConfig.Health, maxHealth = unitConfig.Health });
                commandBuffer.AddComponent(newEntity, new DamageComponent { damageAmount = unitConfig.Damage, range = 5, attackSpeed = 1 }); // Range and AttackSpeed could also be in config
                commandBuffer.AddBuffer<PathPoint>(newEntity);
                
                float speed = unitConfig.MoveSpeed;
                float acceleration = 5; // Could be in config
                float rotationSpeed = 5; // Could be in config
                float rotationAcceleration = 5; // Could be in config

                commandBuffer.AddComponent(newEntity, new ClientUnit { id = id, ownerId = idOfOwner, spriteName = unitType });
                commandBuffer.AddComponent(newEntity, new MovementComponent { speed = speed, acceleration = acceleration, rotationSpeed = rotationSpeed, rotationAcceleration = rotationAcceleration });
                
                // Add resource cost component
                commandBuffer.AddComponent(newEntity, new ResourceCostComponent 
                { 
                    upfrontCost = unitConfig.UpfrontCost,
                    runningCostPerSecond = unitConfig.RunningCost,
                    timeSinceLastCost = 0f
                });

                return newEntity;
            }
        }

        return Entity.Null;
    }
}
