using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Mirror;




[BurstCompile]
public partial struct SpawnerSystem : ISystem
{



    // public void OnCreate(ref SystemState state)
    // {
    //     Debug.Log("SpawnerSystem Created");
    // }
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        // Create a single command buffer for all spawning operations
        EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        NativeList<int> spawnedUnitIds = new NativeList<int>(Allocator.Temp);

        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            if (spawnerData.ValueRW.count > 0 && WorldStateManager.Instance != null)
            {
                Debug.Log("SpawnerSystem: Spawning unit");
                spawnerData.ValueRW.count--;

                // Set the ClientUnit component to the new entity.
                int id = UnityEngine.Random.Range(0, int.MaxValue);

                //TODO: This is a placeholder for the owner of the unit
                int idOfOwner = spawnerData.ValueRO.ownerId;

                Entity createdEntity = CreateUnit(commandBuffer, id, idOfOwner, spawnerData.ValueRO.position, spawnerData.ValueRO.unitType);

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



    [Server]
    public Entity CreateUnit(EntityCommandBuffer commandBuffer, int id, int idOfOwner, float2 position, UnitType unitType)
    {
        // Check if owner has sufficient resources
        if (GameCore.Instance != null)
        {
            ServerPlayer owner = GameCore.Instance.GetServerPlayerById(idOfOwner);
            if (owner != null)
            {
                ResourceCost cost = ResourceConfigLoader.GetUnitCost(unitType);
                
                if (owner.data.resources < cost.upfrontCost)
                {
                    return Entity.Null;
                }
                
                // Deduct upfront cost
                owner.RemoveResources(cost.upfrontCost);
                
                // Update client display
                if (owner.connection != null && owner.connection.identity != null)
                {
                    ClientPlayer clientPlayer = owner.connection.identity.GetComponent<ClientPlayer>();
                    if (clientPlayer != null)
                    {
                        clientPlayer.TargetUpdateResources(owner.connection, owner.data.resources);
                    }
                }
            }
        }

        Entity newEntity = commandBuffer.CreateEntity();

        commandBuffer.AddComponent(newEntity, new LocalTransform { Position = new float3(position.x, position.y, 0) });
        commandBuffer.AddComponent(newEntity, new HealthComponent { currentHealth = 100, maxHealth = 100 });
        commandBuffer.AddComponent(newEntity, new DamageComponent { damageAmount = 10, range = 5, attackSpeed = 1 });
        commandBuffer.AddBuffer<PathPoint>(newEntity);
        
        float speed = 5;
        float acceleration = 5;
        float rotationSpeed = 5;
        float rotationAcceleration = 5;

        switch (unitType)
        {
            case UnitType.Tank:
                //commandBuffer.AddComponent(newEntity, new Tank { });
                speed = 2;
                acceleration = 0;
                rotationSpeed = 5;
                rotationAcceleration = 0;
                break;
            default:
                Debug.LogError("UnitType not found in SpawnerSystem");
                break;
        }
        
        commandBuffer.AddComponent(newEntity, new ClientUnit { id = id, ownerId = idOfOwner, spriteName = unitType });
        commandBuffer.AddComponent(newEntity, new MovementComponent { speed = speed, acceleration = acceleration, rotationSpeed = rotationSpeed, rotationAcceleration = rotationAcceleration });
        
        // Add resource cost component
        ResourceCost unitCost = ResourceConfigLoader.GetUnitCost(unitType);
        commandBuffer.AddComponent(newEntity, new ResourceCostComponent 
        { 
            upfrontCost = unitCost.upfrontCost,
            runningCostPerSecond = unitCost.runningCost,
            timeSinceLastCost = 0f
        });

        return newEntity;
    }


}