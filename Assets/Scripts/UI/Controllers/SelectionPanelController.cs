using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the selection panel (<c>Assets/UI/Hud/SelectionPanel.uxml</c>): the unit count (and squad),
    /// a button per unit type that narrows the selection to it, and the combined health; with only own buildings
    /// selected (v0.13, Figma <c>Selected building – v0.13</c>), a chip per building type, their health, their
    /// Upkeep and a status line (Gun turrets and Jammers). Hidden with nothing selected.
    /// </summary>
    public sealed class SelectionPanelController : IDisposable
    {
        private readonly SelectionModel model;
        private readonly VisualElement panel, types, healthFill, upkeepRow;
        private readonly Label title, health, upkeep, status;
        private string shownTypes;

        public SelectionPanelController(VisualElement root, SelectionModel model)
        {
            this.model = model;
            panel = root.Q("selection-panel");
            types = root.Q("selection-types");
            healthFill = root.Q("selection-health-fill");
            title = root.Q<Label>("selection-title");
            health = root.Q<Label>("selection-health");
            upkeepRow = root.Q("selection-upkeep-row");
            upkeep = root.Q<Label>("selection-upkeep");
            status = root.Q<Label>("selection-status");
            model.Changed += Show;
            Show();
        }

        private void Show()
        {
            bool buildings = model.Count == 0 && model.BuildingKinds.Count > 0;
            panel.EnableInClassList("selection-panel--visible", model.Count > 0 || buildings);
            Display(upkeepRow, buildings);
            Display(status, buildings && model.BuildingStatus.Length > 0);
            if (buildings)
            {
                ShowBuildings();
                return;
            }
            if (model.Count == 0) return;
            string units = model.Count == 1 ? "1 unit" : $"{model.Count:N0} units";
            title.text = model.Squad >= 0 ? $"Selection: {units} (squad {(model.Squad + 1) % 10})" : $"Selection: {units}";

            ShowTypes();
            ShowHealth(model.CombinedHealth01);
        }

        private void ShowHealth(float value)
        {
            float h = Math.Clamp(value, 0f, 1f);
            healthFill.style.width = Length.Percent(h * 100f);
            health.text = $"{h * 100f:0}%";
        }

        /// <summary>The selected own buildings: chips per type, health, upkeep (warning when unpaid) and the status line.</summary>
        private void ShowBuildings()
        {
            int count = 0;
            foreach ((BuildingType _, int n) in model.BuildingKinds) count += n;
            title.text = model.BuildingKinds.Count == 1 && count == 1 ? $"Selection: {NameOf(model.BuildingKinds[0].type)}" : $"Selection: {count} buildings";
            ShowBuildingTypes();
            ShowHealth(model.BuildingHealth01);
            upkeep.text = model.BuildingUnpaid ? $"−{model.BuildingUpkeep:0.#}/s unpaid" : $"−{model.BuildingUpkeep:0.#}/s";
            upkeep.EnableInClassList("selection-upkeep--unpaid", model.BuildingUnpaid);
            status.text = model.BuildingStatus;
            status.EnableInClassList("selection-status--danger", model.BuildingStatusTone == SelectionModel.StatusTone.Danger);
            status.EnableInClassList("selection-status--idle", model.BuildingStatusTone == SelectionModel.StatusTone.Idle);
        }

        private static string NameOf(BuildingType type) => AlertFeedModel.BuildingName(type) ?? AlertFeedModel.Spaced(type.ToString());

        /// <summary>One chip per selected building type (its icon, name and count), rebuilt only when the counts changed.</summary>
        private void ShowBuildingTypes()
        {
            var key = new System.Text.StringBuilder("b");
            foreach ((BuildingType type, int count) in model.BuildingKinds) key.Append((int)type).Append(':').Append(count).Append(';');
            if (key.ToString() == shownTypes) return;
            shownTypes = key.ToString();
            types.Clear();
            foreach ((BuildingType type, int count) in model.BuildingKinds)
            {
                var chip = new VisualElement { name = "type-" + type };
                chip.AddToClassList("selection-type");
                VisualElement icon;
                if (Enum.TryParse(type.ToString(), out IconKind kind)) icon = new CommandIcon { Icon = kind };
                else
                {
                    icon = new VisualElement();
                    icon.AddToClassList("icon-slot");
                }
                var label = new Label(NameOf(type));
                label.AddToClassList("text");
                var number = new Label($"×{count:N0}");
                number.AddToClassList("selection-type__count");
                chip.Add(icon);
                chip.Add(label);
                chip.Add(number);
                types.Add(chip);
            }
        }

        private static void Display(VisualElement element, bool shown) => element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

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
                string name = AlertFeedModel.UnitName(type);
                var chip = new Button(() => model.FilterTo(type)) { name = "type-" + type, tooltip = "Select only " + name };
                chip.AddToClassList("selection-type");
                // The unit's icon (Figma Selection panel: unit icons, v0.12); an empty slot for a type without one.
                VisualElement icon;
                if (Enum.TryParse(type.ToString(), out IconKind kind)) icon = new CommandIcon { Icon = kind };
                else
                {
                    icon = new VisualElement();
                    icon.AddToClassList("icon-slot");
                }
                var label = new Label(name);
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
