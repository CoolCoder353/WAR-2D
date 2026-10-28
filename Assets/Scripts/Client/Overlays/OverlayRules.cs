using System.Collections.Generic;
using Unity.Mathematics;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// The pure rules behind the v0.15 world overlays (Figma <c>v0.15 QoL – redesign</c>, items 8, 10, 12, 14, 15):
    /// which health bars and tracers the Gameplay settings keep, how long a hover waits, the group order feedback's
    /// sizes and sampling, and the selection hull. No Unity objects, so EditMode tests cover them.
    /// </summary>
    public static class OverlayRules
    {
        /// <summary>Seconds the group order feedback takes to fade after the click (Figma item 10: 0.6 s).</summary>
        public const float OrderFadeSeconds = 0.6f;

        /// <summary>Most spokes (unit to goal lines) one order draws; a bigger selection is sampled evenly.</summary>
        public const int MaxSpokes = 24;

        /// <summary>
        /// True when a unit's or building's health bar is drawn under <see cref="HealthBarMode"/> <paramref name="mode"/>:
        /// Always draws every bar, DamagedOnly (the default, as before) the damaged ones, SelectedOnly the selected ones.
        /// Burst-safe (used in <c>PackUnitsJob</c>).
        /// </summary>
        public static bool ShowHealthBar(int mode, float health01, bool selected)
        {
            if (mode == (int)HealthBarMode.Always) return true;
            if (mode == (int)HealthBarMode.SelectedOnly) return selected;
            return health01 < 0.999f;
        }

        /// <summary>
        /// True when the <paramref name="serial"/>-th tracer is drawn under <see cref="TracerMode"/> <paramref name="mode"/>:
        /// Full draws all, Reduced every second one, Off none. Only what is drawn changes; the server sends the same events.
        /// </summary>
        public static bool KeepTracer(int mode, int serial)
        {
            if (mode == (int)TracerMode.Off) return false;
            if (mode == (int)TracerMode.Reduced) return (serial & 1) == 0;
            return true;
        }

        /// <summary>Seconds the pointer rests before a hover card opens under <see cref="TooltipDelayMode"/>; negative for never (Off).</summary>
        public static float TooltipDelaySeconds(int mode) => mode switch
        {
            (int)TooltipDelayMode.Fast => 0.25f,
            (int)TooltipDelayMode.Off => -1f,
            _ => 0.6f,
        };

        /// <summary>True once a hover of <paramref name="restedSeconds"/> opens the card under <paramref name="mode"/>.</summary>
        public static bool TooltipOpen(int mode, float restedSeconds)
        {
            float delay = TooltipDelaySeconds(mode);
            return delay >= 0f && restedSeconds >= delay;
        }

        /// <summary>
        /// The radius a group of <paramref name="units"/> gathers in at its goal: the movement stage's arrival radius
        /// (<c>SimMovementSystem</c>: 0.5 + 0.4 √n tiles), so the goal ring shows where the group will stand.
        /// </summary>
        public static float GatherRadius(int units) => 0.5f + 0.4f * math.sqrt(math.max(1, units));

        /// <summary>
        /// The index of the <paramref name="k"/>-th sampled unit when at most <paramref name="max"/> of
        /// <paramref name="count"/> are drawn: every one when they fit, else evenly spread (first and last kept).
        /// </summary>
        public static int SampleIndex(int k, int count, int max)
        {
            if (count <= max || max <= 1) return k;
            return (int)((long)k * (count - 1) / (max - 1));
        }

        /// <summary>How many units are drawn of <paramref name="count"/> (at most <paramref name="max"/>).</summary>
        public static int SampleCount(int count, int max) => math.min(count, max);

        /// <summary>The opacity of feedback <paramref name="age"/> seconds old that fades over <paramref name="seconds"/> (1 → 0).</summary>
        public static float Fade(float age, float seconds) => seconds <= 0f ? 0f : math.saturate(1f - age / seconds);

        /// <summary>"12 units" / "1 unit" for the count tag.</summary>
        public static string UnitCount(int units) => units == 1 ? "1 unit" : $"{units} units";

        private static readonly System.Comparison<float2> ByXThenY = (a, b) =>
        {
            int c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.y.CompareTo(b.y);
        };

        /// <summary>
        /// The convex hull of <paramref name="points"/> (sorted in place) into <paramref name="hull"/>, counter-clockwise
        /// (Andrew's monotone chain). Fewer than three distinct points give them as they are. No allocation after warm-up.
        /// </summary>
        public static void ConvexHull(List<float2> points, List<float2> hull)
        {
            hull.Clear();
            int n = points.Count;
            if (n == 0) return;
            points.Sort(ByXThenY);
            if (n < 3)
            {
                for (int i = 0; i < n; i++) hull.Add(points[i]);
                return;
            }
            for (int i = 0; i < n; i++)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }
            int lower = hull.Count + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }
            hull.RemoveAt(hull.Count - 1); // the first point closes the loop
        }

        private static float Cross(float2 o, float2 a, float2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>
        /// The chord of a circle at signed distance <paramref name="offset"/> from its centre along <paramref name="normal"/>
        /// (unit length): false when the line misses the circle. The minimum-range ring's hatch is a set of these.
        /// </summary>
        public static bool Chord(float2 centre, float radius, float2 normal, float offset, out float2 a, out float2 b)
        {
            a = b = centre;
            if (math.abs(offset) >= radius) return false;
            float half = math.sqrt(radius * radius - offset * offset);
            float2 along = new float2(-normal.y, normal.x);
            float2 mid = centre + normal * offset;
            a = mid - along * half;
            b = mid + along * half;
            return true;
        }
    }
}
