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

    /// <summary>True when a Miner facing this tile kind earns (either gem).</summary>
    public static bool Produces(TileType faced) => TileRules.IsGem(faced);

    /// <summary>
    /// What a Miner facing <paramref name="faced"/> earns per second, as a multiple of <c>Resources/MiningRate</c>: 1 for a
    /// gem, <paramref name="richMultiplier"/> (<c>Resources/RichGemMultiplier</c>) for a rich gem (v0.17), 0 otherwise.
    /// </summary>
    public static float RateOf(TileType faced, float richMultiplier) =>
        faced == TileType.RichGem ? richMultiplier : faced == TileType.Gem ? 1f : 0f;

    /// <summary>Rotation about Z in degrees for a 2D (Z-only) rotation quaternion.</summary>
    public static float ZDegrees(quaternion rotation)
    {
        float4 q = rotation.value;
        float radians = math.atan2(2f * (q.w * q.z + q.x * q.y), 1f - 2f * (q.y * q.y + q.z * q.z));
        return math.degrees(radians);
    }
}
