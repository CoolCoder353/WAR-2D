using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the alert feed (<c>Assets/UI/Hud/AlertFeed.uxml</c>): one row per shown alert with a colour
    /// stripe by kind and a × that dismisses it, oldest on top. The feed sits on the right just above the
    /// command card and follows its top edge as the card changes height; a new alert has a border in its
    /// stripe colour for <see cref="AlertFeedModel.HighlightSeconds"/> (Figma <c>v0.12 Fixes</c>). v0.15 (Figma v0.15 QoL redesign,
    /// frame 11): the next-alert bar heads the feed while any alert with a place is unvisited (its key, "Next alert" and the
    /// count; a click does what the key does), and visited rows dim.
    /// </summary>
    public sealed class AlertFeedController : IDisposable
    {
        private readonly AlertFeedModel model;
        private readonly VisualElement feed, column, nextSlot;
        private readonly VisualElement card;
        private IVisualElementScheduledItem unhighlight, drain;
        private readonly List<(AlertEntry entry, VisualElement bar)> timers = new List<(AlertEntry, VisualElement)>();
        private readonly HoverCard hover;

        /// <summary>The gap between the feed and the command card's top edge.</summary>
        private const float CardGap = 8f;

        public AlertFeedController(VisualElement root, AlertFeedModel model, HoverCard hover = null)
        {
            this.model = model;
            this.hover = hover;
            feed = root.Q("alert-feed");
            column = root.Q("alert-column") ?? feed;
            nextSlot = root.Q("next-alert-slot");
            card = root.Q("command-card");
            card?.RegisterCallback<GeometryChangedEvent>(OnCardMoved);
            feed.RegisterCallback<PointerEnterEvent>(_ => Hovered = true);
            feed.RegisterCallback<PointerLeaveEvent>(_ => Hovered = false);
            model.Changed += Show;
            Show();
            // The timer bars drain a few times a second (they stop while the HUD holds the feed).
            drain = feed.schedule.Execute(Drain).Every(100);
        }

        /// <summary>Sets each row's timer bar to the share of its time left.</summary>
        private void Drain()
        {
            float now = Time.unscaledTime;
            foreach ((AlertEntry entry, VisualElement bar) in timers) bar.style.width = Length.Percent(model.Left(entry, now) * 100f);
        }

        private void OnCardMoved(GeometryChangedEvent e) => Follow();

        /// <summary>True while the pointer is over the feed (the HUD holds the alerts' timers then).</summary>
        public bool Hovered { get; private set; }

        /// <summary>Puts the feed's bottom edge just above the command card.</summary>
        private void Follow()
        {
            if (card == null || float.IsNaN(card.layout.height) || card.panel == null) return;
            float above = card.parent != null ? card.parent.layout.height - card.layout.yMin : card.layout.height;
            column.style.bottom = (float.IsNaN(above) ? card.layout.height : above) + CardGap;
        }

        private void Show()
        {
            feed.Clear();
            timers.Clear();
            float now = Time.unscaledTime, nextFade = float.MaxValue;
            int unvisited = model.UnvisitedCount;
            if (nextSlot != null)
            {
                nextSlot.Clear();
                if (unvisited > 0) nextSlot.Add(NextAlertBar(unvisited));
            }
            foreach (AlertEntry entry in model.Entries)
            {
                int id = entry.Id;
                var row = new VisualElement { name = "alert-" + id };
                row.AddToClassList("alert");
                row.AddToClassList(KindClass(entry.Kind));
                if (entry.Visited) row.AddToClassList("alert--visited");
                if (AlertFeedModel.HasPlace(entry.Kind))
                {
                    row.AddToClassList("alert--clickable");
                    row.tooltip = "Go there";
                    row.RegisterCallback<ClickEvent>(_ => model.Focus(id));
                }
                float fadesAt = entry.Added + AlertFeedModel.HighlightSeconds;
                if (fadesAt > now) { row.AddToClassList("alert--new"); nextFade = Mathf.Min(nextFade, fadesAt); }
                var stripe = new VisualElement();
                stripe.AddToClassList("alert__stripe");
                row.Add(stripe);
                if (entry.Subject != null)
                {
                    // "Cmdr. Vasquez [icon] AI · Hard gifted you 500": the callsign, its tag, then the rest.
                    var subject = new Label(entry.Subject) { name = "alert-subject" };
                    subject.AddToClassList("alert__text");
                    subject.AddToClassList("alert__subject");
                    row.Add(subject);
                    VisualElement tag = PlayerNameLabel.Tag(entry.SubjectTag);
                    tag.AddToClassList("alert__tag");
                    row.Add(tag);
                }
                string body = entry.Subject != null ? entry.Text.Substring(entry.Subject.Length).TrimStart() : entry.Text;
                var text = new Label(entry.Count > 1 ? $"{body} ×{entry.Count}" : body) { name = "alert-text" };
                text.AddToClassList("alert__text");
                var dismiss = new Button(() => model.Dismiss(id)) { text = "×", name = "alert-dismiss", tooltip = "Dismiss" };
                dismiss.AddToClassList("alert__dismiss");
                dismiss.RegisterCallback<ClickEvent>(e => e.StopPropagation());
                // A right click on the row dismisses it too (playtest 2026-10-30), and goes no further than the feed.
                row.RegisterCallback<PointerDownEvent>(e => { if (e.button == 1) { e.StopPropagation(); model.Dismiss(id); } });
                row.Add(text);
                row.Add(dismiss);
                var timer = new VisualElement { name = "alert-timer", pickingMode = PickingMode.Ignore };
                timer.AddToClassList("alert__timer");
                timer.style.width = Length.Percent(model.Left(entry, now) * 100f);
                row.Add(timer);
                timers.Add((entry, timer));
                feed.Add(row);
            }
            unhighlight?.Pause();
            unhighlight = nextFade < float.MaxValue ? feed.schedule.Execute(Show).StartingIn((long)((nextFade - now) * 1000f) + 1) : null;
            Follow();
        }

        /// <summary>The next-alert bar: the NextAlert key, "Next alert" and how many are left to visit.</summary>
        private Button NextAlertBar(int unvisited)
        {
            var bar = new Button(() => model.FocusNextUnvisited()) { name = "next-alert" };
            bar.AddToClassList("next-alert");
            var chip = new VisualElement();
            chip.AddToClassList("key-chip");
            var key = new Label(HoverCard.KeyOf(GameInput.NextAlert)) { name = "next-alert-key" };
            key.AddToClassList("key-chip__text");
            chip.Add(key);
            var text = new Label("Next alert");
            text.AddToClassList("next-alert__text");
            var count = new Label(unvisited.ToString()) { name = "next-alert-count" };
            count.AddToClassList("next-alert__count");
            bar.Add(chip);
            bar.Add(text);
            bar.Add(count);
            hover?.Attach(bar, () => HoverText.NextAlert(HoverCard.KeyOf(GameInput.NextAlert)), HoverCard.Side.Left, bar);
            return bar;
        }

        /// <summary>The row's kind class (<c>alert--danger</c> and so on), which colours its stripe and new-alert border.</summary>
        public static string KindClass(AlertKind kind) => kind switch
        {
            AlertKind.UnderAttack or AlertKind.EnemySpotted => "alert--danger",
            AlertKind.UpkeepUnpaid or AlertKind.BombTriggered or AlertKind.RepairsStopped or AlertKind.ResourcesLow or AlertKind.PlayerDisconnected => "alert--warning",
            AlertKind.GiftReceived or AlertKind.ConstructionFinished or AlertKind.PlayerReturned => "alert--success",
            _ => "alert--accent",
        };

        public void Dispose()
        {
            model.Changed -= Show;
            unhighlight?.Pause();
            drain?.Pause();
            card?.UnregisterCallback<GeometryChangedEvent>(OnCardMoved);
        }
    }
}
