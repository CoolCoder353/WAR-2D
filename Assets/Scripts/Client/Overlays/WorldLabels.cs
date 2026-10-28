using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// Small text tags pinned to world points for the v0.15 overlays ("12 units", "range 14", "min 4", the rally
    /// cursor tag). They are pooled <see cref="Label"/>s with the HUD's <c>order-tag</c> class, added at run time to
    /// the HUD's <c>order-tags</c> layer (no UXML change), and coloured per tag. Call <see cref="Begin"/>, then
    /// <see cref="World"/> / <see cref="Screen"/>, then <see cref="End"/> once a frame.
    /// </summary>
    public sealed class WorldLabels
    {
        private const float FindEverySeconds = 1f;
        private readonly List<Label> pool = new List<Label>();
        private readonly List<string> shownText = new List<string>();
        private VisualElement layer;
        private float nextFind;
        private int used;

        /// <summary>The HUD layer tags go into (null until the HUD is up).</summary>
        public VisualElement Layer => layer != null && layer.panel != null ? layer : null;

        /// <summary>Finds the HUD's <c>order-tags</c> layer (at most once a second while it is missing).</summary>
        public VisualElement FindLayer()
        {
            if (layer != null && layer.panel != null) return layer;
            if (Time.unscaledTime < nextFind) return null;
            nextFind = Time.unscaledTime + FindEverySeconds;
            var hud = Object.FindAnyObjectByType<WAR2D.UI.HudController>();
            VisualElement root = hud != null && hud.TryGetComponent(out UIDocument doc) ? doc.rootVisualElement : null;
            VisualElement found = root?.Q("order-tags");
            if (found != layer)
            {
                pool.Clear();
                shownText.Clear();
            }
            layer = found;
            return layer;
        }

        /// <summary>Starts a frame's tags.</summary>
        public void Begin()
        {
            FindLayer();
            used = 0;
        }

        /// <summary>A tag centred under a world point (<paramref name="below"/> panel pixels down).</summary>
        public void World(Camera cam, float2 at, string text, Color32 colour, float below = 0f)
        {
            VisualElement l = Layer;
            if (l == null || cam == null) return;
            Vector2 p = RuntimePanelUtils.CameraTransformWorldToPanel(l.panel, new Vector3(at.x, at.y, 0f), cam);
            Label label = Next(text, colour);
            float width = float.IsNaN(label.resolvedStyle.width) ? 0f : label.resolvedStyle.width;
            label.style.left = p.x - width * 0.5f;
            label.style.top = p.y + below;
        }

        /// <summary>A tag at a screen point (pixels, origin bottom left), offset right and down by panel pixels.</summary>
        public void Screen(Vector2 screen, string text, Color32 colour, float right, float down)
        {
            VisualElement l = Layer;
            if (l == null) return;
            Vector2 p = RuntimePanelUtils.ScreenToPanel(l.panel, new Vector2(screen.x, UnityEngine.Screen.height - screen.y));
            Label label = Next(text, colour);
            label.style.left = p.x + right;
            label.style.top = p.y + down;
        }

        /// <summary>Hides the tags not used this frame.</summary>
        public void End()
        {
            for (int i = used; i < pool.Count; i++)
                if (pool[i].style.display != DisplayStyle.None) pool[i].style.display = DisplayStyle.None;
        }

        /// <summary>Hides every tag.</summary>
        public void HideAll()
        {
            used = 0;
            End();
        }

        private Label Next(string text, Color32 colour)
        {
            if (used == pool.Count)
            {
                var label = new Label { name = "overlay-tag-" + pool.Count, pickingMode = PickingMode.Ignore };
                label.AddToClassList("order-tag");
                layer.Add(label);
                pool.Add(label);
                shownText.Add(null);
            }
            int i = used++;
            Label l = pool[i];
            if (!ReferenceEquals(shownText[i], text))
            {
                l.text = text;
                shownText[i] = text;
            }
            Color c = colour;
            l.style.color = c;
            l.style.borderTopColor = l.style.borderBottomColor = l.style.borderLeftColor = l.style.borderRightColor = c;
            l.style.display = DisplayStyle.Flex;
            return l;
        }
    }
}
