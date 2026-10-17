using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>One start option's badge: where its ring is in the world and what the badge says.</summary>
    public struct StartMarker
    {
        public Vector2 Centre;
        public float Radius;
        public Color32 Colour;
        /// <summary>The badge on the ring's rim ("1", "Claude · option", "Claude's start").</summary>
        public string OnScreen;
        /// <summary>The badge pinned to the screen edge when the ring is off screen ("2", "Claude"), followed by an arrow.</summary>
        public string OffScreen;
    }

    /// <summary>
    /// The start options' badges during HQ placement (Figma: HUD / HQ placement – timed pick, teammate's options): a badge
    /// on each ring on screen, and one pinned to the screen edge pointing at each ring off screen. Clicking a badge moves
    /// the camera to that option. Shows only what <see cref="HudController"/> passes: the local player's own and
    /// teammates' options.
    /// </summary>
    public sealed class StartMarkersController
    {
        private const float EdgeMargin = 16f;
        private readonly VisualElement layer;
        private readonly List<Badge> badges = new List<Badge>();

        /// <summary>A badge was clicked: centre the camera on this world point.</summary>
        public event Action<Vector2> Clicked;

        public StartMarkersController(VisualElement root) => layer = root.Q("start-markers");

        /// <summary>Places a badge per marker for <paramref name="camera"/>'s view; an empty list hides them all.</summary>
        public void Show(IReadOnlyList<StartMarker> markers, Camera camera)
        {
            Rect area = new Rect(Vector2.zero, layer.layout.size); // the layer's own space (it starts below the top bar)
            Vector2 origin = layer.worldBound.position;
            bool ready = camera != null && layer.panel != null && !float.IsNaN(area.width) && area.width > 0f;
            int count = ready ? markers.Count : 0;
            while (badges.Count < count) badges.Add(new Badge(layer, this));
            for (int i = 0; i < badges.Count; i++)
            {
                if (i >= count) { badges[i].Hide(); continue; }
                StartMarker m = markers[i];
                Vector2 centre = RuntimePanelUtils.CameraTransformWorldToPanel(layer.panel, m.Centre, camera) - origin;
                Vector2 rim = RuntimePanelUtils.CameraTransformWorldToPanel(layer.panel, m.Centre + new Vector2(0f, m.Radius), camera) - origin;
                if (area.Contains(centre)) badges[i].Show(m, Inside(area, rim), -1, area); // zoomed in, the rim can be off screen: keep its badge in view
                else
                {
                    var (point, direction) = EdgePoint(area, centre);
                    badges[i].Show(m, point, direction, area);
                }
            }
        }

        /// <summary>The point moved inside <paramref name="area"/>, inset from its edges.</summary>
        public static Vector2 Inside(Rect area, Vector2 point) =>
            new Vector2(Mathf.Clamp(point.x, area.xMin + EdgeMargin, area.xMax - EdgeMargin), Mathf.Clamp(point.y, area.yMin + EdgeMargin, area.yMax - EdgeMargin));

        /// <summary>
        /// Where a badge for an off-screen point sits: on the line from the screen centre towards it, inset from the
        /// edge, with the arrow's direction (0 right, 1 down, 2 left, 3 up: the axis it leaves the screen by).
        /// </summary>
        public static (Vector2 point, int direction) EdgePoint(Rect area, Vector2 target)
        {
            Vector2 c = area.center;
            Vector2 d = target - c;
            float halfW = Mathf.Max(1f, area.width * 0.5f - EdgeMargin), halfH = Mathf.Max(1f, area.height * 0.5f - EdgeMargin);
            float tx = Mathf.Abs(d.x) > 1e-4f ? halfW / Mathf.Abs(d.x) : float.PositiveInfinity;
            float ty = Mathf.Abs(d.y) > 1e-4f ? halfH / Mathf.Abs(d.y) : float.PositiveInfinity;
            float t = Mathf.Min(tx, ty, 1f);
            int direction = tx <= ty ? (d.x >= 0f ? 0 : 2) : (d.y >= 0f ? 1 : 3);
            return (c + d * t, direction);
        }

        private sealed class Badge
        {
            private readonly Button button;
            private readonly Label text;
            private readonly VisualElement arrow;
            private Vector2 target;
            private int arrowDirection = -2;

            public Badge(VisualElement layer, StartMarkersController owner)
            {
                button = new Button(() => owner.Clicked?.Invoke(target)) { name = "start-badge" };
                button.AddToClassList("start-badge");
                button.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
                text = new Label();
                text.AddToClassList("text");
                text.AddToClassList("start-badge__text");
                text.pickingMode = PickingMode.Ignore;
                arrow = new VisualElement { pickingMode = PickingMode.Ignore };
                arrow.style.width = 8;
                arrow.style.height = 8;
                arrow.style.marginLeft = 4;
                arrow.generateVisualContent += DrawArrow;
                button.Add(text);
                button.Add(arrow);
                layer.Add(button);
            }

            public void Show(StartMarker m, Vector2 at, int direction, Rect area)
            {
                target = m.Centre;
                button.style.display = DisplayStyle.Flex;
                // Centred on the point, but whole badges stay on screen (their size is last frame's layout).
                float halfW = float.IsNaN(button.layout.width) ? 0f : button.layout.width * 0.5f, halfH = float.IsNaN(button.layout.height) ? 0f : button.layout.height * 0.5f;
                button.style.left = Mathf.Clamp(at.x, area.xMin + halfW, Mathf.Max(area.xMin + halfW, area.xMax - halfW));
                button.style.top = Mathf.Clamp(at.y, area.yMin + halfH, Mathf.Max(area.yMin + halfH, area.yMax - halfH));
                button.style.backgroundColor = (Color)m.Colour;
                string label = direction < 0 ? m.OnScreen : m.OffScreen;
                if (text.text != label) text.text = label;
                arrow.style.display = direction < 0 ? DisplayStyle.None : DisplayStyle.Flex;
                if (direction != arrowDirection) { arrowDirection = direction; arrow.MarkDirtyRepaint(); }
            }

            public void Hide() => button.style.display = DisplayStyle.None;

            /// <summary>A small filled triangle pointing the way the option lies (the font has no arrow glyphs).</summary>
            private void DrawArrow(MeshGenerationContext context)
            {
                Rect r = arrow.contentRect;
                Vector2 c = r.center;
                float h = Mathf.Min(r.width, r.height) * 0.5f;
                Vector2 tip, left, right;
                switch (arrowDirection)
                {
                    case 1: tip = c + new Vector2(0, h); left = c + new Vector2(-h, -h); right = c + new Vector2(h, -h); break;
                    case 2: tip = c + new Vector2(-h, 0); left = c + new Vector2(h, -h); right = c + new Vector2(h, h); break;
                    case 3: tip = c + new Vector2(0, -h); left = c + new Vector2(-h, h); right = c + new Vector2(h, h); break;
                    default: tip = c + new Vector2(h, 0); left = c + new Vector2(-h, -h); right = c + new Vector2(-h, h); break;
                }
                Painter2D painter = context.painter2D;
                painter.fillColor = text.resolvedStyle.color;
                painter.BeginPath();
                painter.MoveTo(tip);
                painter.LineTo(left);
                painter.LineTo(right);
                painter.ClosePath();
                painter.Fill();
            }
        }
    }
}
