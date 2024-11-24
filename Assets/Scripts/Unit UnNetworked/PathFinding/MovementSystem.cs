using Org.BouncyCastle.Asn1.Cmp;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public partial struct MovementSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementComponent>();
    }

    public void OnDestroy(ref SystemState state)
    {
    }

    public void OnUpdate(ref SystemState state)
    {
        foreach (var (movementComp, localTransform, entity) in SystemAPI.Query<RefRW<MovementComponent>, RefRW<LocalTransform>>().WithEntityAccess())
        {


        }
    }



    //HELPER FUNCTIONS
    private void LockNode(int2 position, int entity)
    {
        WorldStateManager.Instance.LockNode(position, entity);
    }

    private void UnlockNode(int2 position)
    {
        WorldStateManager.Instance.UnlockNode(position);
    }

    private void MoveTowardsNode(ref MovementComponent movementComp, ref LocalTransform localTransform, int2 goal, ref SystemState state)
    {
        float3 direction = new float3(goal.x - localTransform.Position.x, 0, goal.y - localTransform.Position.z);
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

        localTransform.Position += direction * movementComp.currentSpeed * SystemAPI.Time.DeltaTime; ;
    }
}


//Notes for this system
//This system will in order:
//1. Check if we need to be anywhere, if we do
//2. Check if we have a path to follow, if we do
//3. Check if we are at the end of a path node, if so
//4. Change to move to the next path node