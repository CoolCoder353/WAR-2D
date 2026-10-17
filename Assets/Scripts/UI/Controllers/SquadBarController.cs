using System;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the squad bar (<c>Assets/UI/Hud/SquadBar.uxml</c>): one button per squad (keys 1–9, 0) with
    /// its live unit count from the server, "–" when empty. Clicking selects the squad. Until a squad is
    /// saved this match a hint line above the bar says how squads work; every slot has a tooltip that says it too
    /// (Figma <c>v0.12 Fixes</c>, <c>Squad bar – hint states</c>).
    /// </summary>
    public sealed class SquadBarController : IDisposable
    {
        private readonly SelectionModel model;
        private readonly Label[] counts = new Label[Sim.Squads.Count];
        private readonly Button[] squads = new Button[Sim.Squads.Count];
        private readonly VisualElement hint;
        private bool squadSaved;

        /// <summary>Raised when a squad is clicked (index 0–9, as keys 1–9, 0).</summary>
        public event Action<int> SquadClicked;

        public SquadBarController(VisualElement root, SelectionModel model)
        {
            this.model = model;
            VisualElement bar = root.Q("squad-bar");
            hint = root.Q("squad-hint");
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
                button.Add(key);
                button.Add(counts[i]);
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
                int n = model.SquadCounts[i];
                counts[i].text = n > 0 ? n.ToString("N0") : "–";
                counts[i].EnableInClassList("squad__count--empty", n == 0);
                squads[i].EnableInClassList("squad--active", model.Squad == i);
                squads[i].tooltip = Tooltip(i, n);
                if (n > 0) squadSaved = true;
            }
            if (hint != null) hint.style.display = squadSaved ? DisplayStyle.None : DisplayStyle.Flex;
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
