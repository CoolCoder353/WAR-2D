using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine;
using Unity.Burst;

[BurstCompile]
public partial struct SpawnerSystem : ISystem
{



    // public void OnCreate(ref SystemState state)
    // {
    //     state.RequireForUpdate<SpawnerData>();
    // }
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            if (spawnerData.ValueRW.count > 0)
            {

                //Spawn 100 units each frame

                for (int i = 0; i < 100 || spawnerData.ValueRW.count == 0; i++)
                {
                    // Debug.Log("SpawnerSystem Spawning");
                    spawnerData.ValueRW.count--;
                    // Spawns a new entity and positions it at the spawner.
                    Entity newEntity = state.EntityManager.Instantiate(spawnerData.ValueRO.prefab);
                    // LocalPosition.FromPosition returns a Transform initialized with the given position.
                    state.EntityManager.SetComponentData(newEntity, LocalTransform.FromPosition(new float3(spawnerData.ValueRO.position.x, spawnerData.ValueRO.position.y, 0)));
                }
            }
        }
    }
}