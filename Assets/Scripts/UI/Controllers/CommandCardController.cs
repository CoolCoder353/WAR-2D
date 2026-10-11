using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the command card (<c>Assets/UI/Hud/CommandCard.uxml</c>): the order buttons (Move,
    /// Attack-move, Stop, Hold; the armed order shows as active), the build buttons (disabled while the
    /// player can't afford them), and, with an own spawner selected, its production queue. Raises events
    /// only; <see cref="HudController"/> sends them down the same paths as the keys.
    /// </summary>
    public sealed class CommandCardController : IDisposable
    {
        private readonly SelectionModel selection;
        private readonly HudModel hud;
        private readonly IReadOnlyDictionary<BuildingType, float> buildCosts;
        private readonly VisualElement card;
        private readonly Label title, unitName, count, help, queueLabel;
        private readonly Button minus, plus, plus10, plus50;
        private readonly string singleHelp;
        private readonly Dictionary<OrderKind, Button> orders = new Dictionary<OrderKind, Button>();
        private readonly Dictionary<BuildingType, Button> builds = new Dictionary<BuildingType, Button>();

        /// <summary>Raised by an order button.</summary>
        public event Action<OrderKind> OrderClicked;

        /// <summary>Raised by a build button.</summary>
        public event Action<BuildingType> BuildClicked;

        /// <summary>Raised by the production queue's buttons: a step (−1, +1, +10, +50) for every selected spawner.</summary>
        public event Action<IReadOnlyList<int>, int> QueueClicked;

        /// <param name="buildCosts">Each buildable type's upfront cost (types without a button are ignored).</param>
        /// <param name="spawnRate">The spawner's units per second, for the queue's help line.</param>
        public CommandCardController(VisualElement root, SelectionModel selection, HudModel hud, IReadOnlyDictionary<BuildingType, float> buildCosts, float spawnRate)
        {
            this.selection = selection;
            this.hud = hud;
            this.buildCosts = buildCosts;
            card = root.Q("command-card");
            title = root.Q<Label>("command-title");
            unitName = root.Q<Label>("production-unit");
            count = root.Q<Label>("production-count");
            help = root.Q<Label>("production-help");
            minus = root.Q<Button>("production-minus");
            plus = root.Q<Button>("production-plus");
            singleHelp = $"{spawnRate:0.##} per second · cost charged at spawn · max {SpawnerRules.MaxQueue}";
            help.text = singleHelp;

            AddOrder(root, "cmd-move", OrderKind.Move);
            AddOrder(root, "cmd-attack-move", OrderKind.AttackMove);
            AddOrder(root, "cmd-stop", OrderKind.Stop);
            AddOrder(root, "cmd-hold", OrderKind.Hold);
            AddBuild(root, "build-miner", BuildingType.Miner);
            AddBuild(root, "build-spawner", BuildingType.SmallUnitSpawner);
            plus10 = root.Q<Button>("production-plus-10");
            plus50 = root.Q<Button>("production-plus-50");
            queueLabel = root.Q<Label>("production-label");
            void Step(Button button, int delta) => button.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); QueueClicked?.Invoke(selection.BuildingIds, delta); };
            Step(minus, -1);
            Step(plus, 1);
            Step(plus10, 10);
            Step(plus50, 50);

            selection.Changed += Show;
            hud.ResourcesChanged += Show;
            Show();
        }

        private void AddOrder(VisualElement root, string name, OrderKind kind)
        {
            Button button = root.Q<Button>(name);
            button.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); OrderClicked?.Invoke(kind); };
            orders[kind] = button;
        }

        private void AddBuild(VisualElement root, string name, BuildingType type)
        {
            Button button = root.Q<Button>(name);
            button.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); BuildClicked?.Invoke(type); };
            builds[type] = button;
        }

        private void Show()
        {
            UnitType produces = SpawnerRules.UnitFor(selection.BuildingType);
            bool spawner = selection.BuildingId != 0 && produces != UnitType.None;
            int spawners = selection.BuildingIds.Count;
            card.EnableInClassList("command-card--building", spawner);
            title.text = !spawner ? "Commands" : spawners > 1 ? $"Commands: {spawners} Spawners selected" : "Commands: Spawner selected";
            if (spawner)
            {
                unitName.text = produces.ToString();
                count.text = selection.SpawnerQueue.ToString();
                queueLabel.text = spawners > 1 ? "Production queue (each selected spawner)" : "Production queue";
                help.text = spawners > 1
                    ? $"{selection.SpawnerQueue} queued across {spawners} spawners · each adds to every selected spawner · max {SpawnerRules.MaxQueue} each"
                    : singleHelp;
                bool room = selection.SpawnerQueue < SpawnerRules.MaxQueue * spawners;
                minus.SetEnabled(selection.SpawnerQueue > 0);
                plus.SetEnabled(room);
                plus10.SetEnabled(room);
                plus50.SetEnabled(room);
                return;
            }

            bool units = selection.Count > 0;
            foreach (KeyValuePair<OrderKind, Button> order in orders)
            {
                order.Value.SetEnabled(units);
                order.Value.EnableInClassList("command-button--active", units && selection.ArmedOrder == order.Key);
            }
            foreach (KeyValuePair<BuildingType, Button> build in builds)
                build.Value.SetEnabled(buildCosts.TryGetValue(build.Key, out float cost) && hud.Resources >= cost);
        }

        public void Dispose()
        {
            selection.Changed -= Show;
            hud.ResourcesChanged -= Show;
        }
    }
}
