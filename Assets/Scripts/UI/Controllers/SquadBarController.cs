using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the squad bar (<c>Assets/UI/Hud/SquadBar.uxml</c>): one button per squad (keys 1–9, 0) with
    /// its live unit count from the server, "–" when empty. Clicking selects the squad. Until a squad is
    /// saved this match a hint line above the bar says how squads work; every slot has a tooltip that says it too
    /// (Figma <c>v0.12 Fixes</c>, <c>Squad bar – hint states</c>). v0.15 (Figma v0.15 QoL redesign, frame 6): a slot may hold
    /// a group of producers instead, shown as the producer's icon and how many; every slot has the shared hover card.
    /// </summary>
    public sealed class SquadBarController : IDisposable
    {
        private readonly SelectionModel model;
        private readonly Label[] counts = new Label[Sim.Squads.Count];
        private readonly CommandIcon[] icons = new CommandIcon[Sim.Squads.Count];
        private readonly Button[] squads = new Button[Sim.Squads.Count];
        private readonly VisualElement hint;
        private bool squadSaved;

        /// <summary>Raised when a squad is clicked (index 0–9, as keys 1–9, 0).</summary>
        public event Action<int> SquadClicked;

        public SquadBarController(VisualElement root, SelectionModel model, HoverCard card = null)
        {
            this.model = model;
            VisualElement bar = root.Q("squad-bar");
            hint = root.Q("squad-hint");
            if (hint is Label hintLabel) hintLabel.text = $"Groups: {HoverCard.KeyOf(GameInput.AssignModifier)} + number saves units or producers";
            bar.Clear();
            for (int i = 0; i < counts.Length; i++)
            {
                int squad = i;
                var button = new Button(() => SquadClicked?.Invoke(squad)) { name = "squad-" + i };
                button.AddToClassList("squad");
                var key = new Label(((i + 1) % 10).ToString());
                key.AddToClassList("squad__key");
                counts[i] = new Label { name = "squad-count-" + i };
                counts[i].AddToClassList("squad__count");
                var line = new VisualElement { pickingMode = PickingMode.Ignore };
                line.AddToClassList("squad__line");
                icons[i] = new CommandIcon { name = "squad-icon-" + i };
                icons[i].AddToClassList("squad__icon");
                line.Add(icons[i]);
                line.Add(counts[i]);
                button.Add(key);
                button.Add(line);
                card?.Attach(button, () => HoverText.Slot(squad, model.SquadCounts[squad], model.SlotBuildings[squad],
                    AlertFeedModel.BuildingName(model.SlotBuildingTypes[squad]) ?? AlertFeedModel.Spaced(model.SlotBuildingTypes[squad].ToString()),
                    HoverCard.KeyOf(GameInput.AssignModifier)), HoverCard.Side.Above);
                bar.Add(button);
                squads[i] = button;
            }
            model.Changed += Show;
            Show();
        }

        private void Show()
        {
            for (int i = 0; i < counts.Length; i++)
            {
                int buildings = model.SlotBuildings[i];
                int n = buildings > 0 ? 0 : model.SquadCounts[i];
                bool group = buildings > 0;
                counts[i].text = group ? buildings.ToString("N0") : n > 0 ? n.ToString("N0") : "–";
                counts[i].EnableInClassList("squad__count--empty", n == 0 && !group);
                counts[i].EnableInClassList("squad__count--group", group);
                icons[i].style.display = group ? DisplayStyle.Flex : DisplayStyle.None;
                if (group && Enum.TryParse(model.SlotBuildingTypes[i].ToString(), out IconKind kind)) icons[i].Icon = kind;
                else if (group) icons[i].Icon = IconKind.Spawner;
                squads[i].EnableInClassList("squad--active", model.Squad == i || (group && model.Count == 0 && SameAs(i)));
                squads[i].tooltip = group ? $"Group {(i + 1) % 10} · {buildings} building{(buildings == 1 ? "" : "s")}" : Tooltip(i, n);
                if (n > 0 || group) squadSaved = true;
            }
            if (hint != null) hint.style.display = squadSaved ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>True when the selected buildings are this slot's building group (its outline shows, like a selected squad).</summary>
        private bool SameAs(int slot)
        {
            int selected = model.BuildingIds.Count;
            return selected > 0 && selected == model.SlotBuildings[slot] && UnitCommander.Instance != null && UnitCommander.Instance.Selection.IsExactlyBuildingGroup(slot);
        }

        /// <summary>A slot's tooltip: its size, then how to save, select and centre it.</summary>
        public static string Tooltip(int squad, int count)
        {
            int key = (squad + 1) % 10;
            string size = count > 0 ? $"{count:N0} unit{(count == 1 ? "" : "s")}" : "empty";
            return $"Squad {key} · {size}\nCtrl+{key} saves the selection here. {key} selects it, twice centres the camera.";
        }

        public void Dispose() => model.Changed -= Show;
    }
}
