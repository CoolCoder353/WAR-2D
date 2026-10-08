using Config;
using Unity.Collections;

namespace WAR2D.Rules
{
    /// <summary>
    /// The damage table in a Burst-friendly form: one multiplier per (attacker unit type, target class),
    /// at <c>type * 3 + (int)target</c>. Missing pairs and out-of-range types read as 1.
    /// </summary>
    public static class DamageTable
    {
        /// <summary>Target classes per attacker row.</summary>
        public const int Classes = 3;

        /// <summary>Builds the flat table from config.</summary>
        public static NativeArray<float> Build(DamageTableConfig config, int typeCount, Allocator allocator)
        {
            var table = new NativeArray<float>(typeCount * Classes, allocator);
            for (int t = 0; t < typeCount; t++)
            for (int c = 0; c < Classes; c++)
                table[t * Classes + c] = config.Multiplier((UnitType)t, (TargetClass)c);
            return table;
        }

        /// <summary>The multiplier for an attacker type hitting a target class; 1 when out of range.</summary>
        public static float Get(NativeArray<float> table, int attackerType, TargetClass target)
        {
            int i = attackerType * Classes + (int)target;
            return attackerType >= 0 && (int)target < Classes && i < table.Length ? table[i] : 1f;
        }
    }
}
