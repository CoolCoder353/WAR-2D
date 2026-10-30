using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Client
{
    /// <summary>The range rings for one selected unit type (Figma v0.15 QoL redesign, item 14).</summary>
    public readonly struct RangeRing
    {
        public readonly UnitType Type;
        /// <summary>Attack range in tiles.</summary>
        public readonly float Max;
        /// <summary>Targets closer than this are ignored (0 = none).</summary>
        public readonly float Min;
        /// <summary>Sight radius in tiles: a unit that outranges its sight needs a spotter.</summary>
        public readonly float Sight;

        public RangeRing(UnitType type, float max, float min, float sight)
        {
            Type = type;
            Max = max;
            Min = min;
            Sight = sight;
        }

        /// <summary>True when the unit can shoot further than it sees (Artillery, Sniper).</summary>
        public bool NeedsSpotter => Max > Sight;
    }

    /// <summary>
    /// Which range rings to draw for the selection, and which known enemies sit inside one. Enemies are only ever
    /// the units the client already holds, which are exactly the ones its side sees now, so a bracket never marks
    /// a unit in the fog.
    /// </summary>
    public static class RangePreview
    {
        /// <summary>Types whose rings are worth drawing: ranges of 8 tiles or more (the Tank's 5 is on screen anyway).</summary>
        public const float MinRingRange = 8f;

        /// <summary>One ring per selected unit type that has a long range, the widest first.</summary>
        public static void RingsFor(IEnumerable<UnitType> selectedTypes, Config.GameConfigData config, List<RangeRing> into)
        {
            into.Clear();
            var seen = new HashSet<UnitType>();
            foreach (UnitType type in selectedTypes)
            {
                if (!seen.Add(type)) continue;
                Config.UnitConfig unit = config.GetUnit(type);
                if (unit == null || unit.Range < MinRingRange) continue;
                into.Add(new RangeRing(type, unit.Range, unit.MinRange, unit.Sight));
            }
            into.Sort((a, b) => b.Max.CompareTo(a.Max));
        }

        /// <summary>True when <paramref name="target"/> is inside the ring drawn around <paramref name="centre"/> (past the minimum range, within the maximum).</summary>
        public static bool InRing(float2 centre, float2 target, RangeRing ring)
        {
            float d = math.distance(centre, target);
            return d <= ring.Max && d >= ring.Min;
        }
    }
}
