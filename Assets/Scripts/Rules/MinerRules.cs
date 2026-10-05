using Unity.Mathematics;

/// <summary>Which tile a Miner faces for a given rotation.</summary>
public static class MinerRules
{
    /// <summary>Offset to the faced tile: 0° right, 90° up, 180° left, 270° down (nearest quarter turn).</summary>
    public static int2 FacingOffset(float rotationDegrees)
    {
        float normalised = (rotationDegrees % 360f + 360f) % 360f;
        int index = (int)math.round(normalised / 90f) % 4;
        switch (index)
        {
            case 0: return new int2(1, 0);
            case 1: return new int2(0, 1);
            case 2: return new int2(-1, 0);
            default: return new int2(0, -1);
        }
    }

    /// <summary>Rotation about Z in degrees for a 2D (Z-only) rotation quaternion.</summary>
    public static float ZDegrees(quaternion rotation)
    {
        float4 q = rotation.value;
        float radians = math.atan2(2f * (q.w * q.z + q.x * q.y), 1f - 2f * (q.y * q.y + q.z * q.z));
        return math.degrees(radians);
    }
}
