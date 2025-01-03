using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Mirror;
using System;

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
        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            if (spawnerData.ValueRW.count > 0)
            {

                //Spawn 3 units each frame

                for (int i = 0; i < 3 || spawnerData.ValueRW.count == 0; i++)
                {
                    if (spawnerData.ValueRO.count == 0)
                    {
                        break;
                    }
                    // Debug.Log("SpawnerSystem Spawning");
                    spawnerData.ValueRW.count--;
                    // Spawns a new entity and positions it at the spawner.
                    Entity newEntity = state.EntityManager.Instantiate(spawnerData.ValueRO.prefab);


                    CheckEntitySuitability(ref state, newEntity);
                    // LocalPosition.FromPosition returns a Transform initialized with the given position.
                    state.EntityManager.SetComponentData(newEntity, LocalTransform.FromPosition(new float3(spawnerData.ValueRO.position.x, spawnerData.ValueRO.position.y, 0)));

                    // Set the ClientUnit component to the new entity.
                    int id = UnityEngine.Random.Range(0, int.MaxValue);

                    //TODO: This is a placeholder for the owner of the unit
                    int idOfOwner = 0;
                    state.EntityManager.SetComponentData(newEntity, new ClientUnit { id = id, spriteName = UnitSprites.Tank, ownerId = idOfOwner });

                    // Add the new entity to the list of entities.
                    WorldStateManager.Instance.AddUnit(newEntity, id);


                }
            }
        }
    }

    //Checks if the entity has all the necessary components to be used as an unit.
    private void CheckEntitySuitability(ref SystemState state, Entity newEntity)
    {
        if (!state.EntityManager.HasComponent<ClientUnit>(newEntity))
        {
            Debug.LogError("Entity did not have ClientUnit component");
        }
        if (!state.EntityManager.HasComponent<LocalTransform>(newEntity))
        {
            Debug.LogError("Entity did not have LocalTransform component");
        }
        if (!state.EntityManager.HasComponent<MovementComponent>(newEntity))
        {
            Debug.LogError("Entity did not have MovementComponent component. Going to add it now");
            // state.EntityManager.SetComponentData(newEntity, new MovementComponent { speed = 1 });
        }
    }
}