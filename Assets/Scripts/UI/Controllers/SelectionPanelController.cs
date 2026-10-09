using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the selection panel (<c>Assets/UI/Hud/SelectionPanel.uxml</c>): the unit count (and squad),
    /// a button per unit type that narrows the selection to it, and the combined health. Hidden with no
    /// units selected.
    /// </summary>
    public sealed class SelectionPanelController : IDisposable
    {
        private readonly SelectionModel model;
        private readonly VisualElement panel, types, healthFill;
        private readonly Label title, health;
        private string shownTypes;

        public SelectionPanelController(VisualElement root, SelectionModel model)
        {
            this.model = model;
            panel = root.Q("selection-panel");
            types = root.Q("selection-types");
            healthFill = root.Q("selection-health-fill");
            title = root.Q<Label>("selection-title");
            health = root.Q<Label>("selection-health");
            model.Changed += Show;
            Show();
        }

        private void Show()
        {
            panel.EnableInClassList("selection-panel--visible", model.Count > 0);
            if (model.Count == 0) return;
            string units = model.Count == 1 ? "1 unit" : $"{model.Count:N0} units";
            title.text = model.Squad >= 0 ? $"Selection: {units} (squad {(model.Squad + 1) % 10})" : $"Selection: {units}";

            ShowTypes();
            float h = Math.Clamp(model.CombinedHealth01, 0f, 1f);
            healthFill.style.width = Length.Percent(h * 100f);
            health.text = $"{h * 100f:0}%";
        }

        /// <summary>Rebuilds the type buttons only when the counts changed, so a click is never lost to a rebuild.</summary>
        private void ShowTypes()
        {
            var key = new System.Text.StringBuilder();
            foreach ((UnitType type, int count) in model.ByType) key.Append((int)type).Append(':').Append(count).Append(';');
            if (key.ToString() == shownTypes) return;
            shownTypes = key.ToString();
            types.Clear();
            foreach ((UnitType type, int count) in model.ByType)
            {
                var chip = new Button(() => model.FilterTo(type)) { name = "type-" + type, tooltip = "Select only " + type };
                chip.AddToClassList("selection-type");
                var icon = new VisualElement();
                icon.AddToClassList("icon-slot");
                var label = new Label(type.ToString());
                label.AddToClassList("text");
                var number = new Label($"×{count:N0}");
                number.AddToClassList("selection-type__count");
                chip.Add(icon);
                chip.Add(label);
                chip.Add(number);
                types.Add(chip);
            }
        }

        public void Dispose() => model.Changed -= Show;
    }
}
