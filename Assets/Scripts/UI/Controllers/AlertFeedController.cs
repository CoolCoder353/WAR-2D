using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the alert feed (<c>Assets/UI/Hud/AlertFeed.uxml</c>): one row per shown alert with a colour
    /// stripe by kind and a × that dismisses it.
    /// </summary>
    public sealed class AlertFeedController : IDisposable
    {
        private readonly AlertFeedModel model;
        private readonly VisualElement feed;

        public AlertFeedController(VisualElement root, AlertFeedModel model)
        {
            this.model = model;
            feed = root.Q("alert-feed");
            model.Changed += Show;
            Show();
        }

        private void Show()
        {
            feed.Clear();
            foreach (AlertEntry entry in model.Entries)
            {
                int id = entry.Id;
                var row = new VisualElement { name = "alert-" + id };
                row.AddToClassList("alert");
                var stripe = new VisualElement();
                stripe.AddToClassList("alert__stripe");
                stripe.AddToClassList(StripeClass(entry.Kind));
                var text = new Label(entry.Text) { name = "alert-text" };
                text.AddToClassList("alert__text");
                var dismiss = new Button(() => model.Dismiss(id)) { text = "×", name = "alert-dismiss", tooltip = "Dismiss" };
                dismiss.AddToClassList("alert__dismiss");
                row.Add(stripe);
                row.Add(text);
                row.Add(dismiss);
                feed.Add(row);
            }
        }

        private static string StripeClass(AlertKind kind) => kind switch
        {
            AlertKind.UnderAttack => "alert__stripe--danger",
            AlertKind.UpkeepUnpaid => "alert__stripe--warning",
            AlertKind.GiftReceived => "alert__stripe--success",
            _ => "alert__stripe--accent",
        };

        public void Dispose() => model.Changed -= Show;
    }
}
