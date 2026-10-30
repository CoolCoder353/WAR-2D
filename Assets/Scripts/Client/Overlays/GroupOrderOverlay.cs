using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// Group-wide order feedback (Figma <c>v0.15 QoL – redesign</c>, item 10, frame 130:1436). After an order, for
    /// <see cref="OverlayRules.OrderFadeSeconds"/>: a ring at the goal sized to the group's gather radius (Move in the
    /// team colour, Attack-move red with crossed swords), a dashed path from the selection's centre of mass, thin spokes
    /// from up to <see cref="OverlayRules.MaxSpokes"/> of its units, and a count tag ("12 units"). A Shift-queued order
    /// draws dashed and lighter. While a Move or Attack-move is armed, a thin hull outlines what will move. All from the
    /// client's own selection: nothing new is sent.
    /// </summary>
    public sealed class GroupOrderOverlay
    {
        /// <summary>Orders shown at once (a right-click that splits by unit kind sends several).</summary>
        public const int MaxShown = 6;
        /// <summary>Most units the armed hull is built from (sampled evenly beyond that).</summary>
        public const int MaxHullPoints = 512;

        /// <summary>One order being shown.</summary>
        public sealed class Shown
        {
            public OrderKind Kind;
            public bool Queued;
            public float2 Goal, From;
            public int Units;
            public float At = float.NegativeInfinity;
            public readonly int[] Sample = new int[OverlayRules.MaxSpokes];
            public int SampleCount;
            public string Tag;
        }

        private readonly Shown[] shown = new Shown[MaxShown];
        private int next;
        private readonly List<float2> hullPoints = new List<float2>(MaxHullPoints);
        private readonly List<float2> hull = new List<float2>(MaxHullPoints);
        private readonly Dictionary<int, string> tagOf = new Dictionary<int, string>();

        public GroupOrderOverlay()
        {
            for (int i = 0; i < MaxShown; i++) shown[i] = new Shown();
        }

        /// <summary>The newest order shown (tests), or null before any.</summary>
        public Shown Latest => shown[(next + MaxShown - 1) % MaxShown].At > float.NegativeInfinity ? shown[(next + MaxShown - 1) % MaxShown] : null;

        /// <summary><see cref="Selection.Ordered"/>: remembers the order's goal, centre of mass and a sample of its units.</summary>
        public void OnOrdered(OrderKind kind, bool queue, int2 goal, IReadOnlyList<int> ids)
        {
            if (kind == OrderKind.Stop || kind == OrderKind.Hold || kind == OrderKind.Detonate || ids == null || ids.Count == 0) return;
            ClientWorld world = ClientWorld.Instance;
            Shown s = shown[next];
            next = (next + 1) % MaxShown;
            s.Kind = kind;
            s.Queued = queue;
            s.Goal = (float2)goal + 0.5f;
            s.Units = ids.Count;
            s.At = Time.unscaledTime;
            float2 sum = 0f;
            int found = 0;
            for (int i = 0; i < ids.Count; i++)
                if (world != null && world.TryGet(ids[i], out ClientUnitView view)) { sum += view.Position; found++; }
            s.From = found > 0 ? sum / found : s.Goal;
            s.SampleCount = OverlayRules.SampleCount(ids.Count, OverlayRules.MaxSpokes);
            for (int k = 0; k < s.SampleCount; k++) s.Sample[k] = ids[OverlayRules.SampleIndex(k, ids.Count, OverlayRules.MaxSpokes)];
            if (!tagOf.TryGetValue(ids.Count, out s.Tag))
            {
                s.Tag = OverlayRules.UnitCount(ids.Count);
                if (tagOf.Count < 256) tagOf[ids.Count] = s.Tag;
            }
        }

        /// <summary>The colour of an order kind: Move the team colour, Attack-move danger, Build gold, Repair success, Dig warning.</summary>
        public static Color32 ColourOf(OrderKind kind, OverlayFrame f) => kind switch
        {
            OrderKind.AttackMove => f.Danger,
            OrderKind.Build => f.Readout,
            OrderKind.Repair => f.Success,
            OrderKind.Dig => f.Warning,
            _ => f.Team,
        };

        public void Draw(OverlayFrame f)
        {
            DrawHull(f);
            for (int i = 0; i < MaxShown; i++)
            {
                Shown s = shown[i];
                float a = OverlayRules.Fade(f.Now - s.At, OverlayRules.OrderFadeSeconds);
                if (a <= 0f) continue;
                Color32 c = ColourOf(s.Kind, f);
                float px = f.Px, dash = s.Queued ? 4f * px : 6f * px;
                float alpha = s.Queued ? 0.7f * a : a;

                // Spokes: thin lines from a sample of the units (where they are now) to the goal.
                for (int k = 0; k < s.SampleCount; k++)
                    if (f.World.TryGet(s.Sample[k], out ClientUnitView view))
                        f.Mesh.Segment(view.Position, s.Goal, px, OverlayColours.Alpha(c, 0.3f * alpha), s.Queued ? dash : 0f);

                // Path from the centre of mass, and the goal ring at the gather radius.
                float radius = math.max(OverlayRules.GatherRadius(s.Units), 10f * px);
                float2 dir = s.Goal - s.From;
                float len = math.length(dir);
                if (len > radius) f.Mesh.Segment(s.From, s.Goal - dir / len * radius, 2f * px, OverlayColours.Alpha(c, 0.85f * alpha), dash);
                if (!s.Queued) OverlayShapes.Disc(f.Mesh, s.Goal, radius, OverlayColours.Alpha(c, 0.14f * alpha), 32);
                if (s.Queued) OverlayShapes.DashedRing(f.Mesh, s.Goal, radius, 2f * px, OverlayColours.Alpha(c, alpha), 4f * px);
                else f.Mesh.Ring(s.Goal, radius, 2f * px, OverlayColours.Alpha(c, alpha), 40);
                if (s.Kind == OrderKind.AttackMove) OverlayShapes.Swords(f.Mesh, s.Goal, math.min(radius * 0.45f, 8f * px), 2f * px, OverlayColours.Alpha(f.Readout, alpha));
                else f.Mesh.Rect(s.Goal - 2f * px, s.Goal + 2f * px, OverlayColours.Alpha(c, alpha));

                if (s.Units > 1 && a > 0.15f) f.Labels.World(f.Cam, s.Goal - new float2(0f, radius), s.Tag, OverlayColours.Alpha(c, 1f), 6f);
            }
        }

        /// <summary>While a Move or Attack-move is armed: a thin dashed outline around the selected units (what will move).</summary>
        private void DrawHull(OverlayFrame f)
        {
            OrderKind? armed = f.Commander.ArmedOrder;
            if (armed != OrderKind.Move && armed != OrderKind.AttackMove) return;
            Selection selection = f.Commander.Selection;
            int count = selection.Selected.Count;
            if (count == 0) return;
            hullPoints.Clear();
            int stride = math.max(1, (count + MaxHullPoints - 1) / MaxHullPoints), i = 0;
            HashSet<int>.Enumerator e = selection.SelectedEnumerator();
            while (e.MoveNext())
            {
                if (i++ % stride != 0) continue;
                if (f.World.TryGet(e.Current, out ClientUnitView view)) hullPoints.Add(view.Position);
            }
            if (hullPoints.Count == 0) return;
            OverlayRules.ConvexHull(hullPoints, hull);
            Color32 c = OverlayColours.Alpha(armed == OrderKind.AttackMove ? f.Danger : f.Accent, 0.8f);
            float pad = 0.7f, w = f.Px;
            if (hull.Count < 3)
            {
                float2 lo = hull[0], hi = hull[hull.Count - 1];
                f.Mesh.RectOutline(math.min(lo, hi) - pad, math.max(lo, hi) + pad, w, c, 4f * w);
                return;
            }
            // Push each hull point out from the centroid so the outline clears the units.
            float2 centroid = 0f;
            for (int k = 0; k < hull.Count; k++) centroid += hull[k];
            centroid /= hull.Count;
            for (int k = 0; k < hull.Count; k++)
            {
                float2 a = hull[k], b = hull[(k + 1) % hull.Count];
                f.Mesh.Segment(Out(a, centroid, pad), Out(b, centroid, pad), w, c, 4f * w);
            }
        }

        private static float2 Out(float2 p, float2 centre, float pad)
        {
            float2 d = p - centre;
            float l = math.length(d);
            return l < 1e-4f ? p : p + d / l * pad;
        }
    }
}
