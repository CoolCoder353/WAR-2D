using System.Collections.Generic;
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
        if (!NetworkServer.active || WorldStateManager.Instance == null || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return;

        // Create a single command buffer for all spawning operations
        EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        List<(int id, int2 tile)> spawnedUnits = new List<(int id, int2 tile)>();
        GameConfigData config = ConfigLoader.LoadConfig();
        float dt = SystemAPI.Time.DeltaTime;

        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            float spawnRate = spawnerData.ValueRO.spawnRate;
            // Clamp the timer to one unit's worth so a spawner that sat idle does not burst out its whole queue.
            float elapsed = spawnerData.ValueRO.timeSinceLastSpawn + dt;
            spawnerData.ValueRW.timeSinceLastSpawn = math.min(elapsed, 1f / spawnRate);

            if (spawnerData.ValueRW.count > 0 && SpawnerRules.CanSpawn(spawnerData.ValueRO.timeSinceLastSpawn, spawnRate))
            {
                int2 anchor = (int2)math.round(spawnerData.ValueRO.position);
                if (!WorldStateManager.Instance.TryFindFreeTileNear(anchor, -1, out int2 spawnTile))
                {
                    continue; // No room this frame; keep the queue.
                }

                Debug.Log("SpawnerSystem: Spawning unit");
                spawnerData.ValueRW.count--;
                spawnerData.ValueRW.timeSinceLastSpawn = 0f;

                // Set the ClientUnit component to the new entity.
                int id = WorldStateManager.Instance.Ids.Allocate();
                int idOfOwner = spawnerData.ValueRO.ownerId;

                // Claim the spawn tile straight away so two spawns in the same frame can't pick it.
                WorldStateManager.Instance.Occupancy.TryClaim(spawnTile, id);

                Entity createdEntity = CreateUnit(commandBuffer, id, idOfOwner, new float2(spawnTile.x, spawnTile.y), spawnerData.ValueRO.unitType, config);

                // Only add to list if entity was successfully created (had sufficient resources)
                if (createdEntity != Entity.Null)
                {
                    // Store the unit ID and tile to find the entity after playback
                    spawnedUnits.Add((id, spawnTile));
                }
                else
                {
                    // Restore count if spawn failed due to insufficient resources
                    spawnerData.ValueRW.count++;
                    WorldStateManager.Instance.Occupancy.Release(spawnTile, id);
                }
            }
        }

        // Play back all commands after iteration is complete
        commandBuffer.Playback(state.EntityManager);
        commandBuffer.Dispose();

        // Now find and add all spawned units to WorldStateManager
        if (WorldStateManager.Instance != null)
        {
            foreach (var (unitId, spawnTile) in spawnedUnits)
            {
                // Find the entity by its ClientUnit.id component
                foreach (var (clientUnit, entity) in SystemAPI.Query<RefRO<ClientUnit>>().WithEntityAccess())
                {
                    if (clientUnit.ValueRO.id == unitId)
                    {
                        WorldStateManager.Instance.AddUnit(entity, unitId);
                        WorldStateManager.Instance.Occupancy.TryClaim(spawnTile, unitId);
                        break;
                    }
                }
            }
        }
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
            UnitConfig unitConfig = config.GetUnit(unitType);

            if (owner == null || !owner.TrySpend(unitConfig.UpfrontCost)) return Entity.Null;

            Entity newEntity = commandBuffer.CreateEntity();

            commandBuffer.AddComponent(newEntity, new LocalTransform { Position = new float3(position.x, position.y, 0) });
            commandBuffer.AddComponent(newEntity, new HealthComponent { entityId = id, currentHealth = unitConfig.Health, maxHealth = unitConfig.Health });
            commandBuffer.AddComponent(newEntity, new DamageComponent { damageAmount = unitConfig.Damage, range = unitConfig.Range, attackSpeed = unitConfig.AttackInterval });
            commandBuffer.AddBuffer<PathPoint>(newEntity);

            commandBuffer.AddComponent(newEntity, new ClientUnit { id = id, ownerId = idOfOwner, spriteName = unitType });
            commandBuffer.AddComponent(newEntity, new MovementComponent { speed = unitConfig.MoveSpeed, acceleration = unitConfig.Acceleration });

            // Every unit pays its running cost; unpaid units decay.
            commandBuffer.AddComponent(newEntity, new UpkeepComponent
            {
                ownerId = idOfOwner,
                runningCostPerSecond = unitConfig.RunningCost,
            });

            return newEntity;
        }

        return Entity.Null;
    }
}
