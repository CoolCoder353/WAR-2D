using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Client;

namespace WAR2D.UI
{
    /// <summary>
    /// The text of the order feedback (Figma <c>v0.12 Fixes</c>): the "Moved to open ground" tag under a moved
    /// goal, the tag beside the pointer ("Dig 58 tiles", "Queue full (5 orders)") and the numbers of the queued
    /// goals while Shift is held. <see cref="OrderFeedback"/> draws the shapes and decides what shows.
    /// </summary>
    public sealed class OrderTagsController
    {
        private const float TagBelow = 14f, CursorRight = 18f, CursorBelow = 14f;
        private readonly VisualElement layer;
        private readonly Label worldTag, cursorTag;
        private readonly List<Label> numbers = new List<Label>();

        public OrderTagsController(VisualElement root)
        {
            layer = root.Q("order-tags");
            worldTag = root.Q<Label>("order-world-tag");
            cursorTag = root.Q<Label>("order-cursor-tag");
        }

        /// <summary>Places the tags for this frame (all hidden without feedback or a camera).</summary>
        public void Update(OrderFeedback feedback, Camera cam, Vector2 pointerScreen)
        {
            if (layer == null) return;
            bool live = feedback != null && cam != null && layer.panel != null;
            Show(worldTag, live ? feedback.WorldTag?.text : null);
            if (live && feedback.WorldTag is (float2 at, string _))
            {
                Vector2 p = RuntimePanelUtils.CameraTransformWorldToPanel(layer.panel, new Vector3(at.x, at.y, 0f), cam);
                float width = float.IsNaN(worldTag.resolvedStyle.width) ? 0f : worldTag.resolvedStyle.width;
                worldTag.style.left = p.x - width * 0.5f;
                worldTag.style.top = p.y + TagBelow;
            }
            Show(cursorTag, live ? feedback.CursorTag : null);
            if (live && feedback.CursorTag != null)
            {
                Vector2 p = RuntimePanelUtils.ScreenToPanel(layer.panel, new Vector2(pointerScreen.x, Screen.height - pointerScreen.y));
                cursorTag.style.left = p.x + CursorRight;
                cursorTag.style.top = p.y + CursorBelow;
            }
            int shown = live ? feedback.Numbers.Count : 0;
            while (numbers.Count < shown)
            {
                var label = new Label { name = "order-number-" + numbers.Count, pickingMode = PickingMode.Ignore };
                label.AddToClassList("order-number");
                layer.Add(label);
                numbers.Add(label);
            }
            for (int i = 0; i < numbers.Count; i++)
            {
                bool on = i < shown;
                numbers[i].style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                if (!on) continue;
                (float2 spot, string text) = feedback.Numbers[i];
                Vector2 p = RuntimePanelUtils.CameraTransformWorldToPanel(layer.panel, new Vector3(spot.x, spot.y, 0f), cam);
                numbers[i].text = text;
                numbers[i].style.left = p.x;
                numbers[i].style.top = p.y;
            }
        }

        private static void Show(Label label, string text)
        {
            if (label == null) return;
            label.style.display = text != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (text != null) label.text = text;
        }
    }
}
