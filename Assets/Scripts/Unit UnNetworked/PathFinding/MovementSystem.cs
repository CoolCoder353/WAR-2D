using System;
using Org.BouncyCastle.Asn1.Cmp;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

public partial struct MovementSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementComponent>();
    }

    public void OnUpdate(ref SystemState state)
    {
        foreach (var (movementComp, localTransform, entity) in SystemAPI.Query<RefRW<MovementComponent>, RefRW<LocalTransform>>().WithEntityAccess())
        {
            DynamicBuffer<PathPoint> pathBuffer = SystemAPI.GetBuffer<PathPoint>(entity);

            if (!pathBuffer.IsEmpty)
            {
                int2 goal = pathBuffer[0].position;

                if (!TrySavePosition(ref movementComp.ValueRW, goal))
                {
                    continue;
                }

                if (pathBuffer.Length > 1)
                {
                    TrySavePosition(ref movementComp.ValueRW, pathBuffer[1].position, 2);
                }



                if (math.distance(localTransform.ValueRO.Position, new float3(goal.x, goal.y, 0)) < 0.1f)
                {
                    UnlockNode(goal);
                    pathBuffer.RemoveAt(0);
                    movementComp.ValueRW.hasSavedPositionOne = false;
                    movementComp.ValueRW.hasSavedPositionTwo = false;
                }
                else
                {
                    MoveTowardsNode(ref movementComp.ValueRW, ref localTransform.ValueRW, goal, ref state);
                }
            }
        }
    }

    private bool TrySavePosition(ref MovementComponent valueRW, int2 goal, int positionToSave = 1)
    {
        if (positionToSave == 1)
        {
            //Check if we already saved position
            if (valueRW.hasSavedPositionOne && valueRW.savedPositionOne.Equals(goal))
            {
                return true;
            }

            //Check if we can save a position
            if (!WorldStateManager.Instance.IsNodeLocked(goal))
            {
                LockNode(goal);
                valueRW.savedPositionOne = goal;
                valueRW.hasSavedPositionOne = true;
                return true;
            }
            else
            {
                return false;
            }
        }
        else
        {
            //Check if we already saved position
            if (valueRW.hasSavedPositionTwo && valueRW.savedPositionTwo.Equals(goal))
            {
                return true;
            }

            //Check if we can save a position
            if (!WorldStateManager.Instance.IsNodeLocked(goal))
            {
                LockNode(goal);
                valueRW.savedPositionTwo = goal;
                valueRW.hasSavedPositionTwo = true;
                return true;
            }
            else
            {
                return false;
            }
        }
    }



    //HELPER FUNCTIONS
    private void LockNode(int2 position, int entity = 1)
    {
        WorldStateManager.Instance.LockNode(position, entity);
    }

    private void UnlockNode(int2 position)
    {
        WorldStateManager.Instance.UnlockNode(position);
    }

    private void MoveTowardsNode(ref MovementComponent movementComp, ref LocalTransform localTransform, int2 goal, ref SystemState state)
    {
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

        localTransform.Position += direction * movementComp.currentSpeed * SystemAPI.Time.DeltaTime; ;
    }
}


//Notes for this system
//This system will in order:
//1. Check if we have a path to follow, if we do
//2. Check if we are at the end of a path node, if so
//3. Change to move to the next path node
//4. If we are not at the end of a path node, move towards the current path node