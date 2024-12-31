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
    }

    [BurstCompile, ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (movementComp, localTransform, entity) in SystemAPI.Query<RefRW<MovementComponent>, RefRW<LocalTransform>>().WithEntityAccess())
        {
            DynamicBuffer<PathPoint> pathBuffer = SystemAPI.GetBuffer<PathPoint>(entity);

            if (!pathBuffer.IsEmpty)
            {
                int2 goal = pathBuffer[0].position;



                if (math.distance(localTransform.ValueRO.Position, new float3(goal.x, goal.y, 0)) < 0.1f)
                {
                    pathBuffer.RemoveAt(0);
                }
                else
                {
                    MoveTowardsNode(ref movementComp.ValueRW, ref localTransform.ValueRW, goal.x, goal.y, ref state);
                }
            }
        }


    }



    //HELPER FUNCTIONS
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

        localTransform.Position += direction * movementComp.currentSpeed * SystemAPI.Time.DeltaTime; ;
    }
}

/*
public partial struct MovementJob : IJobEntity
{

    public float deltaTime;
    BufferLookup<PathPoint> pathBufferLookup;
    public void Execute(ref MovementComponent movementComp, ref LocalTransform localTransform)
    {
        DynamicBuffer<PathPoint> pathBuffer = pathBufferLookup;

        if (!pathBuffer.IsEmpty)
        {
            int2 goal = pathBuffer[0].position;



            if (math.distance(localTransform.Position, new float3(goal.x, goal.y, 0)) < 0.1f)
            {
                pathBuffer.RemoveAt(0);
            }
            else
            {
                MoveTowardsNode(ref movementComp, ref localTransform, goal.x, goal.y);
            }
        }
    }

    private void MoveTowardsNode(ref MovementComponent movementComp, ref LocalTransform localTransform, int goalx, int goaly)
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
            movementComp.currentSpeed += movementComp.acceleration * deltaTime;
        }

        if (movementComp.currentSpeed > movementComp.speed)
        {
            movementComp.currentSpeed = movementComp.speed;
        }

        if (movementComp.acceleration == 0)
        {
            movementComp.currentSpeed = movementComp.speed;
        }

        localTransform.Position += direction * movementComp.currentSpeed * deltaTime; ;
    }
}
*/