using System;
using Config;
using Unity.Collections;

namespace WAR2D.Rules
{
    /// <summary>
    /// The damage table in a Burst-friendly form: one multiplier per (attacker, target class), at
    /// <c>attacker * 4 + (int)target</c>. The attacker index is a unit type's row (<see cref="UnitAttacker"/>) or,
    /// after every unit type, a building type's row (<see cref="BuildingAttacker"/>, v0.13). Callers always go
    /// through those helpers, so nothing computes offsets by hand. Missing pairs and out-of-range rows read as 1.
    /// </summary>
    public static class DamageTable
    {
        /// <summary>Target classes per attacker row.</summary>
        public const int Classes = 4;

        /// <summary><see cref="UnitType"/> values (rows before the building rows). <c>DamageTableTests</c> checks it against the enum.</summary>
        public const int UnitTypeCount = (int)UnitType.SuicideBomber + 1;

        /// <summary><see cref="BuildingType"/> values (rows after the unit rows). <c>DamageTableTests</c> checks it against the enum.</summary>
        public const int BuildingTypeCount = (int)BuildingType.Jammer + 1;

        /// <summary>Attacker rows in the table.</summary>
        public const int Rows = UnitTypeCount + BuildingTypeCount;

        /// <summary>The attacker index of a unit type.</summary>
        public static int UnitAttacker(int unitType) => unitType;

        /// <summary>The attacker index of a unit type.</summary>
        public static int UnitAttacker(UnitType unitType) => (int)unitType;

        /// <summary>The attacker index of a building type.</summary>
        public static int BuildingAttacker(int buildingType) => UnitTypeCount + buildingType;

        /// <summary>The attacker index of a building type.</summary>
        public static int BuildingAttacker(BuildingType buildingType) => UnitTypeCount + (int)buildingType;

        /// <summary>Builds the flat table from config: the unit rows, then the building rows.</summary>
        /// <param name="typeCount">Unit types, as <c>SimData</c> counts them; must equal <see cref="UnitTypeCount"/>.</param>
        public static NativeArray<float> Build(DamageTableConfig config, int typeCount, Allocator allocator)
        {
            if (typeCount != UnitTypeCount) throw new ArgumentException($"{typeCount} unit types, the table is laid out for {UnitTypeCount}", nameof(typeCount));
            var table = new NativeArray<float>(Rows * Classes, allocator);
            for (int t = 0; t < UnitTypeCount; t++)
            for (int c = 0; c < Classes; c++)
                table[UnitAttacker(t) * Classes + c] = config.Multiplier((UnitType)t, (TargetClass)c);
            for (int b = 0; b < BuildingTypeCount; b++)
            for (int c = 0; c < Classes; c++)
                table[BuildingAttacker(b) * Classes + c] = config.Multiplier((BuildingType)b, (TargetClass)c);
            return table;
        }

        /// <summary>The multiplier for an attacker index (see <see cref="UnitAttacker"/>, <see cref="BuildingAttacker"/>) hitting a target class; 1 when out of range.</summary>
        public static float Get(NativeArray<float> table, int attacker, TargetClass target)
        {
            int i = attacker * Classes + (int)target;
            return attacker >= 0 && (int)target < Classes && i < table.Length ? table[i] : 1f;
        }
    }
}
