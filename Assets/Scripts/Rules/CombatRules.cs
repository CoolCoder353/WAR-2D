using Unity.Collections;
using Unity.Mathematics;

/// <summary>Pure combat helpers shared by CombatSystem and tests.</summary>
public static class CombatRules
{
    public static bool IsAttackReady(double now, double lastAttackTime, float interval) => now >= lastAttackTime + interval;

    /// <summary>Index of the nearest living entity not owned by <paramref name="ownerId"/> within range, or -1.</summary>
    public static int FindNearestEnemy(float3 position, float range, int ownerId, NativeArray<float3> positions, NativeArray<int> owners, NativeArray<float> healths)
    {
        int best = -1;
        float bestDistSq = range * range;
        for (int i = 0; i < positions.Length; i++)
        {
            if (healths[i] <= 0f || owners[i] == ownerId) continue;
            float d = math.distancesq(position, positions[i]);
            if (d <= bestDistSq)
            {
                bestDistSq = d;
                best = i;
            }
        }
        return best;
    }
}
