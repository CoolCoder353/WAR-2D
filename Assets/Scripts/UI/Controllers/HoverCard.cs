using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The one shared hover card of the v0.15 HUD (<c>command-tooltip</c>, Figma v0.15 QoL redesign, frame 3): title and key chip,
    /// role, label/value pairs, secondary lines, an amber warning and a red reason. It opens after the Settings → Gameplay
    /// tooltip delay (never when that is Off) on the side of its target that keeps it clear of the panel it belongs to: left
    /// of the command card, below the top bar, above the squad bar. Runtime UI Toolkit draws no <c>tooltip</c> attribute,
    /// so every v0.15 card goes through here.
    /// </summary>
    public sealed class HoverCard
    {
        /// <summary>Where the card opens relative to its target (or the anchor panel given with it).</summary>
        public enum Side { Left, Above, Below }

        private const float Gap = 8f;

        private readonly VisualElement card, keyChip, pairs;
        private readonly Label title, key, role, warning, reason;
        private readonly VisualElement lines;
        private IVisualElementScheduledItem pending;
        private VisualElement shownFor;
        private Func<HoverCardContent> shownContent;
        private readonly System.Collections.Generic.List<(VisualElement target, Func<HoverCardContent> content, Side side, VisualElement anchor)> attached =
            new System.Collections.Generic.List<(VisualElement, Func<HoverCardContent>, Side, VisualElement)>();
        private VisualElement disabledHover;

        /// <summary>The card element (<c>command-tooltip</c>).</summary>
        public VisualElement Element => card;

        /// <summary>True while the card is showing.</summary>
        public bool IsOpen => card.ClassListContains("hover-card--open");

        public HoverCard(VisualElement root)
        {
            card = new VisualElement { name = "command-tooltip", pickingMode = PickingMode.Ignore };
            card.AddToClassList("hover-card");
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList("hover-card__head");
            title = new Label { name = "command-tooltip-title", pickingMode = PickingMode.Ignore };
            title.AddToClassList("hover-card__title");
            keyChip = new VisualElement { pickingMode = PickingMode.Ignore };
            keyChip.AddToClassList("key-chip");
            key = new Label { name = "command-tooltip-key", pickingMode = PickingMode.Ignore };
            key.AddToClassList("key-chip__text");
            keyChip.Add(key);
            head.Add(title);
            head.Add(keyChip);
            role = new Label { name = "command-tooltip-role", pickingMode = PickingMode.Ignore };
            role.AddToClassList("hover-card__role");
            pairs = new VisualElement { name = "command-tooltip-detail", pickingMode = PickingMode.Ignore };
            pairs.AddToClassList("hover-card__pairs");
            lines = new VisualElement { pickingMode = PickingMode.Ignore };
            warning = new Label { name = "command-tooltip-warning", pickingMode = PickingMode.Ignore };
            warning.AddToClassList("hover-card__warning");
            reason = new Label { name = "command-tooltip-reason", pickingMode = PickingMode.Ignore };
            reason.AddToClassList("hover-card__reason");
            card.Add(head);
            card.Add(role);
            card.Add(pairs);
            card.Add(lines);
            card.Add(warning);
            card.Add(reason);
            root.Add(card);
            // A greyed-out button gets no pointer events of its own, yet its card says why it is greyed out: the root
            // watches the pointer over disabled targets.
            root.RegisterCallback<PointerMoveEvent>(OnRootPointerMove, TrickleDown.TrickleDown);
        }

        private void OnRootPointerMove(PointerMoveEvent e)
        {
            VisualElement over = null;
            foreach (var a in attached)
            {
                if (a.target.enabledInHierarchy || a.target.panel == null || a.target.resolvedStyle.display == DisplayStyle.None) continue;
                if (!a.target.worldBound.Contains((Vector2)e.position)) continue;
                over = a.target;
                if (disabledHover != over) { disabledHover = over; Schedule(a.target, a.content, a.side, a.anchor); }
                break;
            }
            if (over == null && disabledHover != null)
            {
                if (shownFor == disabledHover) Hide();
                disabledHover = null;
            }
        }

        /// <summary>
        /// Shows <paramref name="content"/> (read when the card opens, so it is current) while the pointer rests on
        /// <paramref name="target"/>. <paramref name="anchor"/> is the panel the card must stay clear of (the command card for
        /// its buttons); null uses the target itself.
        /// </summary>
        public void Attach(VisualElement target, Func<HoverCardContent> content, Side side, VisualElement anchor = null)
        {
            attached.RemoveAll(a => a.target == target);
            attached.Add((target, content, side, anchor));
            target.RegisterCallback<DetachFromPanelEvent>(_ => attached.RemoveAll(a => a.target == target));
            target.RegisterCallback<PointerEnterEvent>(_ => Schedule(target, content, side, anchor));
            target.RegisterCallback<PointerLeaveEvent>(_ => { if (shownFor == target || pending != null) Hide(); });
            target.RegisterCallback<DetachFromPanelEvent>(_ => { if (shownFor == target) Hide(); });
        }

        private void Schedule(VisualElement target, Func<HoverCardContent> content, Side side, VisualElement anchor)
        {
            Hide();
            float? delay = HoverText.DelaySeconds((TooltipDelayMode)SettingsStore.Current.TooltipDelay);
            if (!delay.HasValue) return;
            shownFor = target;
            shownContent = content;
            pending = card.schedule.Execute(() =>
            {
                pending = null;
                if (target.panel == null) return;
                HoverCardContent c = content();
                if (c == null) return;
                Show(c, target, side, anchor);
            }).StartingIn((long)(delay.Value * 1000f));
        }

        /// <summary>Re-reads the open card's content (a cost became affordable while it shows).</summary>
        public void Refresh()
        {
            if (!IsOpen || shownContent == null) return;
            HoverCardContent c = shownContent();
            if (c != null) Fill(c);
        }

        /// <summary>Closes the card and drops a pending open.</summary>
        public void Hide()
        {
            pending?.Pause();
            pending = null;
            shownFor = null;
            shownContent = null;
            card.EnableInClassList("hover-card--open", false);
        }

        /// <summary>Opens the card at once beside <paramref name="target"/> (tests, and the delayed open).</summary>
        public void Show(HoverCardContent c, VisualElement target, Side side, VisualElement anchor = null)
        {
            Fill(c);
            VisualElement parent = card.parent;
            if (parent == null) return;
            Rect at = target.worldBound, edge = (anchor ?? target).worldBound;
            Vector2 parentSize = parent.layout.size;
            // Clear the inline sides first: each placement sets only the two it needs.
            card.style.left = StyleKeyword.Auto;
            card.style.right = StyleKeyword.Auto;
            card.style.top = StyleKeyword.Auto;
            card.style.bottom = StyleKeyword.Auto;
            switch (side)
            {
                case Side.Left:
                {
                    // Right edge just left of the anchor panel; bottom level with the button, growing upward.
                    Vector2 rightEdge = parent.WorldToLocal(new Vector2(edge.xMin - Gap, at.yMax));
                    card.style.right = parentSize.x - rightEdge.x;
                    card.style.bottom = Mathf.Max(Gap, parentSize.y - rightEdge.y);
                    break;
                }
                case Side.Above:
                {
                    Vector2 topLeft = parent.WorldToLocal(new Vector2(at.xMin, edge.yMin - Gap));
                    card.style.bottom = parentSize.y - topLeft.y;
                    PlaceAcross(topLeft.x, parent.WorldToLocal(new Vector2(at.xMax, 0f)).x, parentSize.x);
                    break;
                }
                default:
                {
                    Vector2 below = parent.WorldToLocal(new Vector2(at.xMin, edge.yMax + Gap));
                    card.style.top = below.y;
                    PlaceAcross(below.x, parent.WorldToLocal(new Vector2(at.xMax, 0f)).x, parentSize.x);
                    break;
                }
            }
            card.EnableInClassList("hover-card--open", true);
            card.BringToFront();
        }

        /// <summary>Lines the card up with the target's left edge, or its right edge on the right half of the screen.</summary>
        private void PlaceAcross(float left, float right, float width)
        {
            if (left + right > width) card.style.right = Mathf.Max(Gap, width - right);
            else card.style.left = Mathf.Max(Gap, left);
        }

        private void Fill(HoverCardContent c)
        {
            title.text = c.Title;
            key.text = c.Key ?? "";
            Display(keyChip, !string.IsNullOrEmpty(c.Key));
            role.text = c.Role ?? "";
            Display(role, !string.IsNullOrEmpty(c.Role));
            pairs.Clear();
            foreach ((string label, string value, HoverTone tone) in c.Pairs)
            {
                var pair = new VisualElement { pickingMode = PickingMode.Ignore };
                pair.AddToClassList("hover-card__pair");
                var l = new Label(label) { pickingMode = PickingMode.Ignore };
                l.AddToClassList("hover-card__label");
                var v = new Label(value) { pickingMode = PickingMode.Ignore };
                v.AddToClassList("hover-card__value");
                if (tone == HoverTone.Cost) v.AddToClassList("hover-card__value--cost");
                if (tone == HoverTone.Danger) v.AddToClassList("hover-card__value--danger");
                pair.Add(l);
                pair.Add(v);
                pairs.Add(pair);
            }
            Display(pairs, c.Pairs.Count > 0);
            lines.Clear();
            foreach (string line in c.Lines)
            {
                var l = new Label(line) { pickingMode = PickingMode.Ignore };
                l.AddToClassList("hover-card__line");
                lines.Add(l);
            }
            Display(lines, c.Lines.Count > 0);
            warning.text = c.Warning ?? "";
            Display(warning, !string.IsNullOrEmpty(c.Warning));
            reason.text = c.Reason ?? "";
            Display(reason, !string.IsNullOrEmpty(c.Reason));
        }

        private static void Display(VisualElement element, bool shown) => element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>An action's current binding as a key chip shows it ("A", "RMB", "Del"): rebinding-aware.</summary>
        public static string KeyOf(UnityEngine.InputSystem.InputAction action) => HoverText.KeyChip(Rebinding.Display(action));
    }
}
