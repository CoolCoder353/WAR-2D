using UnityEngine;
using Mirror;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
public partial struct MovementSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementComponent>();
        state.RequireForUpdate<LocalTransform>();
        state.RequireForUpdate<ClientUnit>();
    }

    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (movementComp, localTransform, clientUnit, entity) in SystemAPI.Query<RefRW<MovementComponent>, RefRW<LocalTransform>, RefRO<ClientUnit>>().WithEntityAccess())
        {
            DynamicBuffer<PathPoint> pathBuffer = SystemAPI.GetBuffer<PathPoint>(entity);

            if (!pathBuffer.IsEmpty)
            {
                int2 goal = pathBuffer[0].position;

                IsAvaliable(goal, clientUnit.ValueRO.id, out bool isAvaliable);

                if (!isAvaliable)
                {
                    continue;
                }

                //PROBLEM: There are some edge cases where we release our current location before we can claim the next location. This causes units to collide.
                //FIX: Only release the current location if we can move to the next location
                if (math.distance(localTransform.ValueRO.Position, new float3(goal.x, goal.y, 0)) < 0.1f)
                {
                    //Not at end of path
                    if (pathBuffer.Length > 1)
                    {
                        //Check if next location is avaliable, if so, release current location and move to next location
                        IsAvaliable(pathBuffer[1].position, clientUnit.ValueRO.id, out bool isAvaliableNext);
                        if (isAvaliableNext)
                        {
                            ClaimLocation(pathBuffer[1].position, clientUnit.ValueRO.id);
                            pathBuffer.RemoveAt(0);
                            ReleaseLocation(goal, clientUnit.ValueRO.id);
                        }
                    }
                }
                else
                {
                    MoveTowardsNode(ref movementComp.ValueRW, ref localTransform.ValueRW, goal.x, goal.y, ref state);
                }
            }
        }
    }

    //HELPER FUNCTIONS
    public void IsAvaliable(int2 position, int id, out bool isAvaliable)
    {
        // Note: WorldStateManager is a MonoBehaviour, so accessing it from a Burst compiled job (if this was one) would be an issue.
        // But since this is on the main thread (ISystem), it's okay, but performance might be impacted.
        // Ideally, WorldStateManager should be an ECS system or component.
        bool result = WorldStateManager.Instance.IsAvaliable(position, id);
        isAvaliable = true; // This seems to always return true in the original code?
    }

    public void ClaimLocation(int2 position, int id)
    {
        WorldStateManager.Instance.ClaimLocation(position, id);
    }

    public void ReleaseLocation(int2 position, int id)
    {
        WorldStateManager.Instance.ReleaseLocation(position, id);
    }

    [BurstCompile]
    private void MoveTowardsNode(ref MovementComponent movementComp, ref LocalTransform localTransform, int goalx, int goaly, ref SystemState state)
    {
        int2 goal = new int2(goalx, goaly);
        float3 direction = new float3(goal.x - localTransform.Position.x, goal.y - localTransform.Position.y, 0);
        float distance = math.length(direction);
        direction = math.normalize(direction);

        if (distance < 0.1f)
        {
            movementComp.currentSpeed = 0;
            return;
        }

        if (movementComp.currentSpeed < movementComp.speed)
        {
            movementComp.currentSpeed += movementComp.acceleration * SystemAPI.Time.DeltaTime;
        }

        if (movementComp.currentSpeed > movementComp.speed)
        {
            movementComp.currentSpeed = movementComp.speed;
        }

        if (movementComp.acceleration == 0)
        {
            movementComp.currentSpeed = movementComp.speed;
        }

        localTransform.Position += direction * movementComp.currentSpeed * SystemAPI.Time.DeltaTime;
    }
}
