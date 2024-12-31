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
    //     state.RequireForUpdate<SpawnerData>();
    // }
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            if (spawnerData.ValueRW.count > 0)
            {

                //Spawn 100 units each frame

                for (int i = 0; i < 3 || spawnerData.ValueRW.count == 0; i++)
                {
                    // Debug.Log("SpawnerSystem Spawning");
                    spawnerData.ValueRW.count--;
                    // Spawns a new entity and positions it at the spawner.
                    Entity newEntity = state.EntityManager.Instantiate(spawnerData.ValueRO.prefab);
                    // LocalPosition.FromPosition returns a Transform initialized with the given position.
                    state.EntityManager.SetComponentData(newEntity, LocalTransform.FromPosition(new float3(spawnerData.ValueRO.position.x, spawnerData.ValueRO.position.y, 0)));

                    // Set the ClientUnit component to the new entity.
                    FixedString64Bytes id = new FixedString64Bytes("" + UnityEngine.Random.Range(0, 1000000));

                    //TODO: This is a placeholder for the owner of the unit
                    int idOfOwner = 0;
                    state.EntityManager.SetComponentData(newEntity, new ClientUnit { id = id, spriteName = new FixedString64Bytes("tank"), ownerId = idOfOwner });

                    // Add the new entity to the list of entities.
                    WorldStateManager.Instance.AddUnit(newEntity, id);


                }
            }
        }
    }
}