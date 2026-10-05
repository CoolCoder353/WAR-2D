using Unity.Mathematics;

/// <summary>Pure kinematics for moving a unit toward a waypoint.</summary>
public static class MovementMath
{
    /// <summary>Distance at which a unit counts as having reached a waypoint.</summary>
    public const float ArriveDistance = 0.1f;

    /// <summary>A unit blocked this long by another unit gives up its remaining path.</summary>
    public const float BlockedGiveUpSeconds = 2f;

    /// <summary>Returns the new position. Never overshoots the target; updates currentSpeed.</summary>
    public static float3 Step(float3 position, float3 target, ref float currentSpeed, float maxSpeed, float acceleration, float deltaTime)
    {
        float3 delta = target - position;
        float distance = math.length(delta);
        if (distance < 1e-5f)
        {
            currentSpeed = 0f;
            return target;
        }

        currentSpeed = acceleration <= 0f ? maxSpeed : math.min(maxSpeed, currentSpeed + acceleration * deltaTime);
        float stepLength = math.min(currentSpeed * deltaTime, distance);
        return position + delta / distance * stepLength;
    }
}
