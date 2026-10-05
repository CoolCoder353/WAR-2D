using Mirror;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// Moves units along their PathPoint buffer. A unit always holds a claim on the tile it is
/// heading to, so two units never target the same tile. Server only.
/// </summary>
public partial struct MovementSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementComponent>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || WorldStateManager.Instance == null) return;
        TileOccupancy occupancy = WorldStateManager.Instance.Occupancy;
        float dt = SystemAPI.Time.DeltaTime;

        foreach (var (move, transform, unit, entity) in SystemAPI.Query<RefRW<MovementComponent>, RefRW<LocalTransform>, RefRO<ClientUnit>>().WithEntityAccess())
        {
            DynamicBuffer<PathPoint> path = SystemAPI.GetBuffer<PathPoint>(entity);
            if (path.IsEmpty)
            {
                move.ValueRW.currentSpeed = 0f;
                continue;
            }

            int id = unit.ValueRO.id;
            int2 waypoint = path[0].position;

            if (!occupancy.TryClaim(waypoint, id))
            {
                if (Blocked(ref move.ValueRW, dt)) path.Clear();
                continue;
            }

            float3 target = new float3(waypoint.x, waypoint.y, 0f);
            if (math.distance(transform.ValueRO.Position, target) > MovementMath.ArriveDistance)
            {
                move.ValueRW.blockedSeconds = 0f;
                float speed = move.ValueRO.currentSpeed;
                transform.ValueRW.Position = MovementMath.Step(transform.ValueRO.Position, target, ref speed, move.ValueRO.speed, move.ValueRO.acceleration, dt);
                move.ValueRW.currentSpeed = speed;
                continue;
            }

            // Arrived at waypoint. Keep the claim on the final tile; otherwise claim the next before releasing this one.
            if (path.Length == 1)
            {
                path.Clear();
                move.ValueRW.currentSpeed = 0f;
                continue;
            }

            int2 next = path[1].position;
            if (occupancy.TryClaim(next, id))
            {
                path.RemoveAt(0);
                occupancy.Release(waypoint, id);
                move.ValueRW.blockedSeconds = 0f;
            }
            else if (Blocked(ref move.ValueRW, dt))
            {
                path.Clear();
            }
        }
    }

    /// <summary>Accumulates blocked time; returns true when the unit should give up its path.</summary>
    private static bool Blocked(ref MovementComponent move, float dt)
    {
        move.currentSpeed = 0f;
        move.blockedSeconds += dt;
        if (move.blockedSeconds < MovementMath.BlockedGiveUpSeconds) return false;
        move.blockedSeconds = 0f;
        return true;
    }
}
