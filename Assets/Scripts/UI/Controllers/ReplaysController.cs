using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the Replays screen (<c>Assets/UI/Menus/Replays.uxml</c>): one row per saved replay with Watch and
    /// Delete (Delete asks first, in the row), Refresh and Back.
    /// </summary>
    public sealed class ReplaysController
    {
        private readonly ReplaysModel model;
        private readonly VisualElement list;
        private readonly Label empty, error;

        /// <summary>Watch on a row (the replay's path).</summary>
        public event Action<string> WatchClicked;
        /// <summary>A confirmed Delete (the replay's path).</summary>
        public event Action<string> DeleteConfirmed;
        public event Action BackClicked;

        public ReplaysController(VisualElement root, ReplaysModel model)
        {
            this.model = model;
            list = root.Q("replays-list");
            empty = root.Q<Label>("replays-empty");
            error = root.Q<Label>("replays-error");
            root.Q<Button>("replays-refresh").clicked += model.Refresh;
            root.Q<Button>("replays-back").clicked += () => BackClicked?.Invoke();
            model.Changed += Render;
        }

        /// <summary>Shows a problem (e.g. a replay that can't be played) under the list; null clears it.</summary>
        public void ShowError(string message)
        {
            error.text = message ?? "";
            error.EnableInClassList("play-panel__notice--visible", !string.IsNullOrEmpty(message));
        }

        private void Render()
        {
            list.Clear();
            for (int i = 0; i < model.Rows.Count; i++) list.Add(Row(model.Rows[i], i));
            empty.EnableInClassList("play-panel__notice--visible", model.Empty);
        }

        private VisualElement Row(ReplayRow r, int index)
        {
            var row = new VisualElement { name = "replay-row" };
            row.AddToClassList("row");
            row.AddToClassList("game-row");
            var swatch = new VisualElement();
            swatch.AddToClassList("lobby-row__swatch");
            if (r.Players.Length > 0) swatch.AddToClassList("player-" + (r.Players[0].Colour % 8 + 1));
            row.Add(swatch);
            var info = new VisualElement();
            info.AddToClassList("replay-row__info");
            info.Add(new Label(r.DateText) { name = "replay-date" });
            var summary = new Label(r.SummaryText) { name = "replay-summary" };
            summary.AddToClassList("replay-row__meta");
            info.Add(summary);
            row.Add(info);
            var size = new Label($"{r.DurationText} · {r.SizeText}") { name = "replay-length" };
            size.AddToClassList("replay-row__size");
            row.Add(size);
            Button watch = Widgets.TextButton("replay-watch", "Watch", primary: true, () => WatchClicked?.Invoke(r.Path));
            Button delete = Widgets.TextButton("replay-delete", "Delete", primary: false, null);
            delete.clicked += () =>
            {
                // Ask in the row: Delete becomes "Delete?" with Cancel beside it.
                if (delete.text == "Delete")
                {
                    delete.text = "Delete?";
                    var cancel = Widgets.TextButton("replay-delete-cancel", "Cancel", primary: false, null);
                    cancel.clicked += () => { delete.text = "Delete"; cancel.RemoveFromHierarchy(); };
                    row.Add(cancel);
                    return;
                }
                DeleteConfirmed?.Invoke(r.Path);
            };
            row.Add(watch);
            row.Add(delete);
            return row;
        }
    }
}
