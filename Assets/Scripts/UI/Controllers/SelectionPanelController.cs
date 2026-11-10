using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the selection panel (<c>Assets/UI/Hud/SelectionPanel.uxml</c>): the unit count (and squad),
    /// a button per unit type that narrows the selection to it, and the combined health; with only own buildings
    /// selected (v0.13, Figma <c>Selected building – v0.13</c>), a chip per building type, their health, their
    /// Upkeep and a status line (Gun turrets and Jammers). Hidden with nothing selected. v0.15 (Figma v0.15 QoL redesign,
    /// frame 5): a unit chip shows "selected / on the map" when there are more, a double-click on it selects every own unit of
    /// that type on the map, and a "Map-wide" tag sits in the title after one.
    /// </summary>
    public sealed class SelectionPanelController : IDisposable
    {
        private readonly SelectionModel model;
        private readonly VisualElement panel, types, healthFill, upkeepRow;
        private readonly Label title, health, upkeep, status, mapWide;
        private readonly HoverCard card;
        private string shownTypes;

        public SelectionPanelController(VisualElement root, SelectionModel model, HoverCard card = null)
        {
            this.model = model;
            this.card = card;
            mapWide = root.Q<Label>("selection-map-wide");
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
            if (mapWide != null && (buildings || model.Count == 0)) Display(mapWide, false);
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
            if (model.MapWide && model.ByType.Count == 1)
                units = $"{model.Count:N0} {Plural(AlertFeedModel.UnitName(model.ByType[0].type), model.Count)}";
            title.text = model.Squad >= 0 ? $"Selection: {units} (squad {(model.Squad + 1) % 10})" : $"Selection: {units}";
            if (model.Roaming > 0) title.text += $" · {model.Roaming:N0} roaming"; // Figma "Playtest fixes" item 6
            Display(mapWide, model.MapWide);

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
            // Playtest 2026-11-10: the HQ read "Upkeep −0/s"; no upkeep reads "None".
            string cost = model.BuildingUpkeep < 0.05f ? "None" : $"−{model.BuildingUpkeep:0.#}/s";
            upkeep.text = model.BuildingUnpaid ? $"{cost} unpaid" : cost;
            upkeep.EnableInClassList("selection-upkeep--unpaid", model.BuildingUnpaid);
            status.text = model.BuildingStatus;
            status.EnableInClassList("selection-status--danger", model.BuildingStatusTone == SelectionModel.StatusTone.Danger);
            status.EnableInClassList("selection-status--idle", model.BuildingStatusTone == SelectionModel.StatusTone.Idle);
        }

        private static string NameOf(BuildingType type) => AlertFeedModel.BuildingName(type) ?? AlertFeedModel.Spaced(type.ToString());

        /// <summary>A building type's icon: named as the type, but for the HQ, whose type is <c>Base</c>.</summary>
        public static bool TryIconOf(BuildingType type, out IconKind kind)
        {
            if (type == BuildingType.Base) { kind = IconKind.Hq; return true; }
            return Enum.TryParse(type.ToString(), out kind);
        }

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
                if (TryIconOf(type, out IconKind kind)) icon = new CommandIcon { Icon = kind };
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
            foreach ((UnitType type, int count) in model.ByType) key.Append((int)type).Append(':').Append(count).Append('/').Append(model.MapWide ? 0 : model.TotalOf(type)).Append(model.AnyBurning(type) ? "f" : "").Append(';');
            if (key.ToString() == shownTypes) return;
            shownTypes = key.ToString();
            types.Clear();
            foreach ((UnitType type, int count) in model.ByType)
            {
                string name = AlertFeedModel.UnitName(type);
                var chip = new Button(() => model.FilterTo(type)) { name = "type-" + type, tooltip = "Select only " + name };
                chip.AddToClassList("selection-type");
                // A double-click takes every own unit of the type on the map (the first click already kept only this type).
                chip.RegisterCallback<ClickEvent>(e => { if (e.clickCount == 2) model.SelectEverywhere(type); });
                card?.Attach(chip, () => new HoverCardContent(name, null, null).Line($"Click: keep only {Plural(name, 2)}")
                    .Line($"Double-click: all {model.TotalOf(type):N0} on the map")
                    .Line($"{HoverCard.KeyOf(GameInput.AssignModifier)} + double-click a unit: the same"), HoverCard.Side.Below);
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
                var number = new Label($"{count:N0}");
                number.AddToClassList("selection-type__count");
                chip.Add(icon);
                chip.Add(label);
                chip.Add(number);
                // v0.20 (Figma B1): a small flame when any of that type burns (its shape, not only a colour, says so).
                if (model.AnyBurning(type)) chip.Add(BurningMarker());
                int total = model.TotalOf(type);
                if (!model.MapWide && total > count)
                {
                    var of = new Label($"/ {total:N0}") { name = "type-total" };
                    of.AddToClassList("selection-type__total");
                    chip.Add(of);
                }
                types.Add(chip);
            }
        }

        private static Sprite flame;

        /// <summary>The burning marker: the approved world flame (the effects atlas's <c>Fire</c>, as on the health bars), 12 px.</summary>
        private static VisualElement BurningMarker()
        {
            if (flame == null)
            {
                WAR2D.Art.ArtAtlas atlas = WAR2D.Art.ArtAtlas.Load(WAR2D.Art.ArtAtlas.Effects);
                flame = atlas != null ? atlas.Sprite("Fire", 1) : null;
            }
            var marker = new VisualElement { name = "type-burning", pickingMode = PickingMode.Ignore, tooltip = "Burning" };
            marker.AddToClassList("selection-type__burning");
            marker.style.width = 12;
            marker.style.height = 12;
            marker.style.marginLeft = 2;
            if (flame != null) marker.style.backgroundImage = new StyleBackground(flame);
            return marker;
        }

        private static string Plural(string name, int n) => n == 1 || name.EndsWith("s") ? name : name + "s";

        public void Dispose() => model.Changed -= Show;
    }
}
