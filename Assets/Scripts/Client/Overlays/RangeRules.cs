using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Client.Overlays
{
    /// <summary>One thing's rings: attack range (solid), minimum range (dashed) and sight (dotted), 0 for none.</summary>
    public struct RingSet
    {
        public float2 At;
        public float Range, Min, Sight;
        /// <summary>What may share a ring: the same kind of thing, for the same owner.</summary>
        public int Group;
        public int Owner;
    }

    /// <summary>
    /// The pure rules behind the held-key rings (v0.18, Figma G1): what a type's rings are, from the config alone, so a hovered
    /// enemy's rings say nothing a player couldn't read in the manual; and which rings to draw, nearest the view's centre first,
    /// up to a cap, with close neighbours of one type sharing a ring.
    /// </summary>
    public static class RangeRules
    {
        /// <summary>A unit type's rings. A bomber's reach is its blast; a Builder has only its sight.</summary>
        public static RingSet OfUnit(Config.UnitConfig unit, float2 at, int owner)
        {
            float range = unit.BlastRadius > 0f ? unit.BlastRadius : unit.Damage > 0 ? unit.Range : 0f;
            return new RingSet { At = at, Range = range, Min = unit.MinRange, Sight = unit.Sight, Owner = owner };
        }

        /// <summary>A building's rings: the Gun turret's range, the Radar's range, the Jammer's radius, and its sight unless that is what the ring already shows.</summary>
        public static RingSet OfBuilding(BuildingType type, Config.BuildingConfig building, float jamRadius, float2 at, int owner)
        {
            var set = new RingSet { At = at, Sight = building.Sight, Owner = owner };
            if (type == BuildingType.GunTurret) set.Range = building.Range;
            else if (SensorRules.IsRadar(type)) { set.Range = building.Sight; set.Sight = 0f; }
            else if (SensorRules.IsJammer(type)) set.Range = jamRadius;
            return set;
        }

        /// <summary>
        /// Share two equal circles a centre distance apart have in common, along the line between them: 1 at the same point,
        /// 0 when they only touch.
        /// </summary>
        public static float Overlap(float distance, float radius) => radius <= 0f ? 0f : 1f - math.min(1f, distance / (2f * radius));

        /// <summary>
        /// Keeps the sets to draw in <paramref name="kept"/>: nearest <paramref name="centre"/> first, skipping any that shares
        /// more than <paramref name="mergeOverlap"/> with a kept one of its group, up to <paramref name="max"/>. Sorts
        /// <paramref name="sets"/>.
        /// </summary>
        public static void Choose(List<RingSet> sets, float2 centre, int max, float mergeOverlap, List<RingSet> kept)
        {
            kept.Clear();
            sets.Sort((a, b) => math.distancesq(a.At, centre).CompareTo(math.distancesq(b.At, centre)));
            foreach (RingSet set in sets)
            {
                if (kept.Count >= max) return;
                float radius = math.max(set.Range, set.Sight);
                bool merged = false;
                foreach (RingSet other in kept)
                {
                    if (other.Group != set.Group || other.Owner != set.Owner) continue;
                    if (Overlap(math.distance(other.At, set.At), radius) > mergeOverlap) { merged = true; break; }
                }
                if (!merged) kept.Add(set);
            }
        }
    }
}
