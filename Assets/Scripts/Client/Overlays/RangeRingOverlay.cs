using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// Range rings for selected long-range units (Figma <c>v0.15 QoL – redesign</c>, item 14, frame 131:1534), from
    /// <see cref="RangePreview"/>: one pair of rings per selected type, its maximum range in the Radar's ring style
    /// (dashed, 2 px, 6 % fill, team colour) and its minimum range as a red ring with a hatch, labelled "range 14" and
    /// "min 4". The rings sit on the type's selected units, and follow the pointer while a Move or Attack-move is armed
    /// (what it would reach from there). Enemies inside a ring get a thin red bracket; the client holds only the
    /// units its side sees now, so nothing in the fog is ever marked. A reticle at the pointer says whether the
    /// pointer is in reach of the units where they stand (green) or in their dead zone or just out of reach (red).
    /// </summary>
    public sealed class RangeRingOverlay
    {
        /// <summary>Most enemy brackets drawn at once.</summary>
        public const int MaxBrackets = 48;
        /// <summary>The reticle shows within this many tiles beyond the maximum range.</summary>
        public const float HintMargin = 3f;

        private static readonly int TypeCount = System.Enum.GetValues(typeof(UnitType)).Length;
        private readonly float2[] sumOf = new float2[TypeCount];
        private readonly int[] countOf = new int[TypeCount];
        private readonly List<UnitType> types = new List<UnitType>(TypeCount);
        private readonly List<RangeRing> rings = new List<RangeRing>(TypeCount);
        private readonly List<int> nearby = new List<int>(256);
        private readonly Dictionary<int, string> rangeText = new Dictionary<int, string>();
        private readonly Dictionary<int, string> minText = new Dictionary<int, string>();
        private int lastMask = -1;
        private bool[] longRange;

        /// <summary>True for a type whose range earns a ring (<see cref="RangePreview.MinRingRange"/>), worked out once.</summary>
        private bool LongRange(Config.GameConfigData config, int t)
        {
            if (longRange == null)
            {
                longRange = new bool[TypeCount];
                for (int i = 0; i < TypeCount; i++)
                {
                    longRange[i] = config.Units.TryGetValue((UnitType)i, out Config.UnitConfig unit) && unit != null && unit.Range >= RangePreview.MinRingRange;
                }
            }
            return longRange[t];
        }

        public void Draw(OverlayFrame f)
        {
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            if (config == null) return;
            int mask = Gather(f, config);
            if (mask == 0) { lastMask = 0; return; }
            if (mask != lastMask)
            {
                types.Clear();
                for (int t = 0; t < TypeCount; t++) if ((mask & (1 << t)) != 0) types.Add((UnitType)t);
                RangePreview.RingsFor(types, config, rings); // only when the selected types change: it allocates a little
                lastMask = mask;
            }
            OrderKind? armed = f.Commander.ArmedOrder;
            bool follow = armed == OrderKind.Move || armed == OrderKind.AttackMove;
            int brackets = 0;
            bool hintIn = false, hintNear = false;
            for (int r = 0; r < rings.Count; r++)
            {
                RangeRing ring = rings[r];
                int t = (int)ring.Type;
                if (countOf[t] == 0) continue;
                float2 at = sumOf[t] / countOf[t];
                float2 centre = follow ? f.Pointer : at;
                DrawRing(f, ring, centre, rings.Count > 1);
                brackets = Brackets(f, ring, centre, brackets);
                float d = math.distance(at, f.Pointer);
                if (d <= ring.Max + HintMargin) hintNear = true;
                if (RangePreview.InRing(at, f.Pointer, ring)) hintIn = true;
            }
            if (hintNear && !f.PointerOverUI)
            {
                Color32 c = hintIn ? f.Success : f.Danger;
                float px = f.Px;
                if (hintIn) OverlayShapes.Brackets(f.Mesh, f.Pointer, 6f * px, 3f * px, 2f * px, c);
                else f.Mesh.Cross(f.Pointer, 4f * px, 2f * px, c);
            }
        }

        /// <summary>Sums the positions of the selected units per type with a long range; returns the mask of those types.</summary>
        private int Gather(OverlayFrame f, Config.GameConfigData config)
        {
            for (int t = 0; t < TypeCount; t++) { sumOf[t] = 0f; countOf[t] = 0; }
            int mask = 0;
            HashSet<int>.Enumerator e = f.Commander.Selection.SelectedEnumerator();
            while (e.MoveNext())
            {
                if (!f.World.TryGet(e.Current, out ClientUnitView view)) continue;
                int t = (int)view.Type;
                if ((uint)t >= (uint)TypeCount) continue;
                if (!LongRange(config, t)) continue;
                mask |= 1 << t;
                sumOf[t] += view.Position;
                countOf[t]++;
            }
            return mask;
        }

        private void DrawRing(OverlayFrame f, RangeRing ring, float2 centre, bool named)
        {
            float px = f.Px;
            OverlayShapes.Disc(f.Mesh, centre, ring.Max, OverlayColours.Alpha(f.Team, 0.06f), 64);
            OverlayShapes.DashedRing(f.Mesh, centre, ring.Max, 2f * px, f.Team, 8f * px);
            f.Labels.World(f.Cam, centre + new float2(0f, ring.Max), Text(rangeText, ring, named, true), f.Accent, -10f);
            if (ring.Min <= 0f) return;
            OverlayShapes.Disc(f.Mesh, centre, ring.Min, OverlayColours.Alpha(f.Danger, 0.12f), 32);
            OverlayShapes.Hatch(f.Mesh, centre, ring.Min, math.max(0.25f, 8f * px), px, OverlayColours.Alpha(f.Danger, 0.5f));
            f.Mesh.Ring(centre, ring.Min, 2f * px, f.Danger, 40);
            f.Labels.World(f.Cam, centre - new float2(0f, ring.Min), Text(minText, ring, false, false), f.Danger, 4f);
        }

        /// <summary>Brackets on the enemies the client holds (seen now) inside the ring; returns the running count.</summary>
        private int Brackets(OverlayFrame f, RangeRing ring, float2 centre, int drawn)
        {
            if (drawn >= MaxBrackets) return drawn;
            nearby.Clear();
            f.World.CollectInRadius(centre, ring.Max, f.Own, nearby);
            float px = f.Px;
            for (int i = 0; i < nearby.Count && drawn < MaxBrackets; i++)
            {
                if (!f.World.TryGet(nearby[i], out ClientUnitView view) || !f.Commander.IsEnemyOwner(view.OwnerId)) continue;
                if (!RangePreview.InRing(centre, view.Position, ring)) continue;
                OverlayShapes.Brackets(f.Mesh, view.Position, 0.5f, math.max(0.15f, 4f * px), px, f.Danger);
                drawn++;
            }
            return drawn;
        }

        private static string Text(Dictionary<int, string> cache, RangeRing ring, bool named, bool max)
        {
            float value = max ? ring.Max : ring.Min;
            int key = ((int)ring.Type << 16) | (named ? 1 << 15 : 0) | (math.clamp((int)math.round(value * 10f), 0, (1 << 15) - 1));
            if (cache.TryGetValue(key, out string s)) return s;
            string v = value.ToString(value == math.floor(value) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
            s = max ? (named ? $"{ring.Type} range {v}" : $"range {v}") : $"min {v}";
            cache[key] = s;
            return s;
        }
    }
}
