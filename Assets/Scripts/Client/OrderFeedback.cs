using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client.Render;

namespace WAR2D.Client
{
    /// <summary>
    /// The world feedback for orders (Figma <c>v0.12 Fixes</c>): a goal the server will move off rock (an X where
    /// the player clicked, a dashed line and a ring at the open ground, for <see cref="MovedSeconds"/>), the
    /// selection's numbered goals while Shift is held, the Dig drag's box and the tiles it would dig, and the marks
    /// of dig areas already given. Everything is the local player's own data. The HUD draws the text parts
    /// (<see cref="WorldTag"/>, <see cref="CursorTag"/>, <see cref="Numbers"/>).
    /// </summary>
    public sealed class OrderFeedback : MonoBehaviour
    {
        /// <summary>How long a moved goal shows.</summary>
        public const float MovedSeconds = 1.5f;
        /// <summary>Dig areas marked at once; the oldest goes first.</summary>
        public const int MaxMarks = 8;
        private const float FadeSeconds = 0.4f;

        /// <summary>The goals sent to each unit.</summary>
        public OrderQueueTracker Queue { get; private set; }

        /// <summary>Text pinned to a world point (the moved goal's tag), or null.</summary>
        public (float2 at, string text)? WorldTag { get; private set; }

        /// <summary>Text beside the pointer (the Dig drag's count, or "Queue full"), or null.</summary>
        public string CursorTag { get; private set; }

        /// <summary>The numbers of the queued goals shown while Shift is held.</summary>
        public List<(float2 at, string text)> Numbers { get; } = new List<(float2, string)>();

        private sealed class Mark
        {
            public int2 Min, Max;
            public readonly HashSet<int> Units = new HashSet<int>();
        }

        private readonly List<Mark> marks = new List<Mark>();
        private readonly List<int2> previewTiles = new List<int2>();
        private Func<int2, bool> diggable;
        private Func<int, float2?> positionOf;
        private OverlayMesh overlay, tilesOverlay; // tile marks go under the fog, so they never light up unexplored rock
        private int2 movedFrom, movedTo;
        private float movedUntil = -1f;
        private bool preview, previewClamped;
        private int2 previewMin, previewMax;
        private bool showQueue;
        private int queueUnit;

        /// <summary>Sets the rules: the waypoint limit, which tiles a dig area digs, and where a unit is.</summary>
        public void Init(int maxQueued, Func<int2, bool> isDiggable, Func<int, float2?> unitPosition)
        {
            Queue = new OrderQueueTracker(maxQueued);
            diggable = isDiggable;
            positionOf = unitPosition;
        }

        /// <summary>Shows that an order clicked at <paramref name="clicked"/> goes to <paramref name="goal"/> instead.</summary>
        public void GoalMoved(int2 clicked, int2 goal)
        {
            movedFrom = clicked;
            movedTo = goal;
            movedUntil = Time.unscaledTime + MovedSeconds;
        }

        /// <summary>Shows the Dig drag's box; <paramref name="clamped"/> when it hit the size cap. Returns the tiles it would dig.</summary>
        public int ShowDigPreview(int2 min, int2 max, bool clamped)
        {
            preview = true;
            previewMin = min;
            previewMax = max;
            previewClamped = clamped;
            previewTiles.Clear();
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                if (diggable != null && diggable(new int2(x, y))) previewTiles.Add(new int2(x, y));
            return previewTiles.Count;
        }

        /// <summary>Hides the Dig drag's box.</summary>
        public void HideDigPreview() => preview = false;

        /// <summary>Marks a dig area given to the units (a queued one keeps their other marks).</summary>
        public void AddDigMarks(int2 min, int2 max, IEnumerable<int> units, bool queue)
        {
            var mark = new Mark { Min = min, Max = max };
            foreach (int id in units) mark.Units.Add(id);
            if (!queue) Unmark(mark.Units);
            marks.Add(mark);
            while (marks.Count > MaxMarks) marks.RemoveAt(0);
        }

        /// <summary>The units got an order that replaces their dig area.</summary>
        public void Unmark(IEnumerable<int> ids)
        {
            foreach (int id in ids)
                foreach (Mark m in marks) m.Units.Remove(id);
            marks.RemoveAll(m => m.Units.Count == 0);
        }

        /// <summary>Shows the unit's goals (Shift held) or hides them (unit 0).</summary>
        public void ShowQueueOf(int unit)
        {
            showQueue = unit != 0;
            queueUnit = unit;
        }

        private void Update()
        {
            if (Queue == null) return;
            Queue.Prune(positionOf, 1.5f);
            foreach (Mark m in marks)
                m.Units.RemoveWhere(id => positionOf(id) == null);
            marks.RemoveAll(m => m.Units.Count == 0 || !AnyDiggable(m));
            CursorTag = preview ? $"Dig {previewTiles.Count:N0} tiles{(previewClamped ? " (max)" : "")}"
                : showQueue && Queue.IsFull(queueUnit) ? $"Queue full ({Queue.Limit} orders)" : null;
            WorldTag = Time.unscaledTime < movedUntil ? ((float2)movedTo + new float2(0.5f, 0f), "Moved to open ground") : ((float2, string)?)null;
        }

        private bool AnyDiggable(Mark m)
        {
            for (int y = m.Min.y; y <= m.Max.y; y++)
            for (int x = m.Min.x; x <= m.Max.x; x++)
                if (diggable(new int2(x, y))) return true;
            return false;
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (Queue == null || cam == null) return;
            overlay ??= new OverlayMesh("OrderFeedback", SortingOrders.DebugOverlay - 1);
            tilesOverlay ??= new OverlayMesh("OrderFeedbackTiles", SortingOrders.Fog - 1);
            overlay.Clear();
            tilesOverlay.Clear();
            Numbers.Clear();
            float px = 2f * cam.orthographicSize / Mathf.Max(1, Screen.height); // one screen pixel in tiles
            string palette = Palettes.Current;
            Color32 accent = Palettes.Accent(palette), warning = Palettes.Warning(palette);

            foreach (Mark m in marks) DrawTiles(m.Min, m.Max, null, Alpha(warning, 0.08f), Alpha(warning, 0.45f), px);
            if (preview)
            {
                DrawTiles(previewMin, previewMax, previewTiles, Alpha(warning, 0.18f), Alpha(warning, 0.9f), px);
                overlay.RectOutline(previewMin, previewMax + 1, 2f * px, accent, 6f * px);
            }

            float left = movedUntil - Time.unscaledTime;
            if (left > 0f)
            {
                float a = math.saturate(left / FadeSeconds);
                float2 from = (float2)movedFrom + 0.5f, to = (float2)movedTo + 0.5f;
                overlay.Segment(from, to, 2f * px, Alpha(warning, 0.85f * a), 6f * px);
                overlay.Cross(from, 5f * px, 2.5f * px, Alpha(warning, a));
                overlay.Ring(to, 10f * px, 2f * px, Alpha(accent, a));
                overlay.Rect(to - 2f * px, to + 2f * px, Alpha(accent, a));
            }

            if (showQueue && positionOf(queueUnit) is float2 unitAt)
            {
                IReadOnlyList<float2> goals = Queue.GoalsOf(queueUnit);
                float2 previous = unitAt;
                for (int i = 0; i < goals.Count; i++)
                {
                    overlay.Segment(previous, goals[i], 2f * px, accent);
                    overlay.Ring(goals[i], 10f * px, 2f * px, accent);
                    overlay.Rect(goals[i] - 2f * px, goals[i] + 2f * px, accent);
                    Numbers.Add((goals[i] + new float2(10f, 12f) * px, (i + 1).ToString()));
                    previous = goals[i];
                }
            }
            overlay.Apply();
            tilesOverlay.Apply();
        }

        /// <summary>Fills and outlines the diggable tiles of a box (all of <paramref name="tiles"/> when given).</summary>
        private void DrawTiles(int2 min, int2 max, List<int2> tiles, Color32 fill, Color32 edge, float px)
        {
            if (tiles != null)
            {
                foreach (int2 t in tiles) DrawTile(t, fill, edge, px);
                return;
            }
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                if (diggable(new int2(x, y))) DrawTile(new int2(x, y), fill, edge, px);
        }

        private void DrawTile(int2 t, Color32 fill, Color32 edge, float px)
        {
            float2 lo = (float2)t + 0.04f, hi = (float2)t + 0.96f;
            tilesOverlay.Rect(lo, hi, fill);
            tilesOverlay.RectOutline(lo, hi, math.min(px, 0.06f), edge);
        }

        private static Color32 Alpha(Color32 c, float a)
        {
            c.a = (byte)math.round(255f * math.saturate(a));
            return c;
        }

        private void OnDestroy()
        {
            overlay?.Dispose();
            tilesOverlay?.Dispose();
            overlay = tilesOverlay = null;
        }
    }
}
