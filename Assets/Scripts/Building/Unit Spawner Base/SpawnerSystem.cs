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
        foreach (var spawnerData in SystemAPI.Query<RefRW<SpawnerData>>())
        {
            if (spawnerData.ValueRW.count > 0 && WorldStateManager.Instance != null)
            {
                spawnerData.ValueRW.count--;

                // Set the ClientUnit component to the new entity.
                int id = UnityEngine.Random.Range(0, int.MaxValue);

                //TODO: This is a placeholder for the owner of the unit
                int idOfOwner = spawnerData.ValueRO.ownerId;

                var (commandBuffer, newEntity) = CreateUnit(id, idOfOwner, spawnerData.ValueRO.position, spawnerData.ValueRO.unitType);

                commandBuffer.Playback(state.EntityManager);

                // Add the new entity to the list of entities.
                WorldStateManager.Instance.AddUnit(newEntity, id);



            }
        }
    }



    [Server]
    public (EntityCommandBuffer, Entity) CreateUnit(int id, int idOfOwner, float2 position, UnitType unitType)
    {
        EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        Entity newEntity = commandBuffer.CreateEntity();
        commandBuffer.AddComponent(newEntity, new ClientUnit { id = id, ownerId = idOfOwner });
        commandBuffer.AddComponent(newEntity, new LocalTransform { Position = new float3(position.x, position.y, 0) });
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

        commandBuffer.AddComponent(newEntity, new MovementComponent { speed = speed, acceleration = acceleration, rotationSpeed = rotationSpeed, rotationAcceleration = rotationAcceleration });


        return (commandBuffer, newEntity);
    }


}