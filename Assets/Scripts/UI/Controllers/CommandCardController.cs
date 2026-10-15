using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the command card (<c>Assets/UI/Hud/CommandCard.uxml</c>, Figma <c>Command card – v0.7</c>): the order
    /// buttons (Move, Attack-move, Stop, Hold, and Repair with Builders or Dig with Diggers selected; the armed
    /// order shows as active), the build row with Builders selected (Miner, Spawner, Wall, Bomb, Radar, with
    /// their costs, disabled while the player can't afford them), and, with own buildings selected, one
    /// production row per unit type they make and Cancel (blueprints, full refund) or Demolish (finished, a
    /// share back, after an inline confirm). Raises events only; <see cref="HudController"/> sends them down
    /// the same paths as the keys.
    /// </summary>
    public sealed class CommandCardController : IDisposable
    {
        private readonly SelectionModel selection;
        private readonly HudModel hud;
        private readonly IReadOnlyDictionary<BuildingType, float> buildCosts;
        private readonly IReadOnlyDictionary<UnitType, float> unitCosts;
        private readonly float wallCost, demolishShare;
        private readonly VisualElement card, divider, rows, removal, confirm;
        private readonly Label title, help, queueLabel, buildLabel, buildHint, digHint, refund, question, cancelKey;
        private readonly CommandIcon cancelIcon;
        private readonly Button cancel, wall;
        private readonly string singleHelp;
        private readonly Dictionary<OrderKind, Button> orders = new Dictionary<OrderKind, Button>();
        private readonly Dictionary<BuildingType, Button> builds = new Dictionary<BuildingType, Button>();
        private readonly List<(UnitType type, Label count, Button minus, Button[] adds)> queueRows = new List<(UnitType, Label, Button, Button[])>();
        private BuildingType rowsFor = BuildingType.None;

        /// <summary>Raised by an order button (Move and AttackMove arm, Repair and Dig arm, Stop and Hold act).</summary>
        public event Action<OrderKind> OrderClicked;

        /// <summary>Raised by a building's build button.</summary>
        public event Action<BuildingType> BuildClicked;

        /// <summary>Raised by the Wall button: the next press on the map starts a wall drag.</summary>
        public event Action WallClicked;

        /// <summary>Raised by a production row's buttons: a step (−1, +1, +10, +50) of one unit type for every selected producer.</summary>
        public event Action<IReadOnlyList<int>, UnitType, int> QueueClicked;

        /// <summary>Raised when the selected buildings are to be cancelled or demolished (after the confirm, for finished ones).</summary>
        public event Action<IReadOnlyList<int>> RemoveConfirmed;

        /// <summary>True while the Demolish confirm is showing.</summary>
        public bool ConfirmOpen { get; private set; }

        /// <param name="buildCosts">Each buildable type's upfront cost (types without a button are ignored).</param>
        /// <param name="unitCosts">Each produced unit type's cost, for the production rows.</param>
        /// <param name="spawnRate">The spawner's units per second, for the queue's help line.</param>
        public CommandCardController(VisualElement root, SelectionModel selection, HudModel hud, IReadOnlyDictionary<BuildingType, float> buildCosts,
            IReadOnlyDictionary<UnitType, float> unitCosts, float wallCost, float demolishShare, float spawnRate)
        {
            this.selection = selection;
            this.hud = hud;
            this.buildCosts = buildCosts;
            this.unitCosts = unitCosts;
            this.wallCost = wallCost;
            this.demolishShare = demolishShare;
            card = root.Q("command-card");
            title = root.Q<Label>("command-title");
            help = root.Q<Label>("production-help");
            queueLabel = root.Q<Label>("production-label");
            rows = root.Q("production-rows");
            divider = root.Q("divider");
            buildLabel = root.Q<Label>("build-label");
            buildHint = root.Q<Label>("build-hint");
            digHint = root.Q<Label>("dig-hint");
            removal = root.Q("removal");
            confirm = root.Q("removal-confirm");
            refund = root.Q<Label>("removal-refund");
            question = root.Q<Label>("removal-question");
            cancel = root.Q<Button>("cmd-cancel");
            cancelIcon = root.Q<CommandIcon>("cmd-cancel-icon");
            cancelKey = root.Q<Label>("cmd-cancel-key");
            singleHelp = $"{spawnRate:0.##} per second · cost charged at spawn · max {SpawnerRules.MaxQueue}";
            help.text = singleHelp;

            AddOrder(root, "cmd-move", OrderKind.Move);
            AddOrder(root, "cmd-attack-move", OrderKind.AttackMove);
            AddOrder(root, "cmd-stop", OrderKind.Stop);
            AddOrder(root, "cmd-hold", OrderKind.Hold);
            AddOrder(root, "cmd-repair", OrderKind.Repair);
            AddOrder(root, "cmd-dig", OrderKind.Dig);
            AddBuild(root, "build-miner", BuildingType.Miner);
            AddBuild(root, "build-spawner", BuildingType.SmallUnitSpawner);
            AddBuild(root, "build-bomb", BuildingType.Bomb);
            AddBuild(root, "build-radar", BuildingType.Radar);
            wall = root.Q<Button>("build-wall");
            wall.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); WallClicked?.Invoke(); };
            root.Q<Label>("build-wall-cost").text = $"{wallCost:0.##}/tile";
            buildHint.text = $"Wall: press and drag on the map to lay a line ({wallCost:0.##} per tile).";

            cancel.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); RequestRemove(); };
            root.Q<Button>("removal-yes").clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); Remove(); };
            root.Q<Button>("removal-no").clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); CloseConfirm(); };

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
            if (buildCosts.TryGetValue(type, out float cost)) root.Q<Label>(name + "-cost").text = $"{cost:0.##}";
            builds[type] = button;
        }

        /// <summary>
        /// The Cancel/Demolish button or key: blueprints go at once (full refund, nothing finished is lost); finished
        /// buildings ask first, and a second press while the confirm shows is the yes.
        /// </summary>
        public void RequestRemove()
        {
            if (!Removable) return;
            if (selection.AllBlueprints || ConfirmOpen) Remove();
            else
            {
                ConfirmOpen = true;
                Show();
            }
        }

        /// <summary>Closes the Demolish confirm (Keep, Esc). False when it wasn't open.</summary>
        public bool CloseConfirm()
        {
            if (!ConfirmOpen) return false;
            ConfirmOpen = false;
            Show();
            return true;
        }

        private void Remove()
        {
            ConfirmOpen = false;
            if (Removable) RemoveConfirmed?.Invoke(selection.BuildingIds);
            Show();
        }

        /// <summary>True when the selected own buildings may be cancelled or demolished (never the HQ).</summary>
        private bool Removable => selection.BuildingId != 0 && selection.BuildingType != BuildingType.Base;

        private void Show()
        {
            bool building = selection.BuildingId != 0;
            int count = selection.BuildingIds.Count;
            bool units = selection.Count > 0;
            bool builders = !building && selection.HasBuilders, diggers = !building && selection.HasDiggers;
            card.EnableInClassList("command-card--building", building);
            card.EnableInClassList("command-card--builders", builders);
            if (!building || !Removable) ConfirmOpen = false;
            // Inline display wins over the stylesheet, so every section is set either way.
            Display(digHint, diggers && !builders);
            // The build row is the Builders' (v0.7): without them, nothing can build.
            Display(divider, builders);
            Display(buildLabel, builders);
            Display(card.Q("builds"), builders);
            Display(buildHint, builders);
            Display(removal, building && Removable);
            Display(card.Q("production"), building && SpawnerRules.IsProducer(selection.BuildingType));
            if (building)
            {
                ShowBuilding(count);
                return;
            }

            title.text = "Commands";
            foreach (KeyValuePair<OrderKind, Button> order in orders)
            {
                order.Value.SetEnabled(units);
                order.Value.EnableInClassList("command-button--active", units && selection.ArmedOrder == order.Key);
            }
            Display(orders[OrderKind.Repair], builders);
            Display(orders[OrderKind.Dig], diggers);
            foreach (KeyValuePair<BuildingType, Button> build in builds)
                build.Value.SetEnabled(buildCosts.TryGetValue(build.Key, out float cost) && hud.Resources >= cost);
            wall.SetEnabled(hud.Resources >= wallCost);
        }

        private void ShowBuilding(int count)
        {
            UnitType[] produced = SpawnerRules.Produces(selection.BuildingType);
            string name = AlertFeedModel.BuildingName(selection.BuildingType) ?? selection.BuildingType.ToString();
            title.text = count > 1 ? $"Commands: {count} {name}s selected"
                : selection.AllBlueprints ? $"{name} (blueprint) · {selection.BuildingProgress}%"
                : name;

            if (produced.Length > 0) ShowProduction(produced, count);
            if (!Removable) return;
            float cost = buildCosts.TryGetValue(selection.BuildingType, out float c) ? c : 0f;
            float back = selection.RefundFor(cost, demolishShare);
            bool blueprints = selection.AllBlueprints;
            cancelIcon.Icon = blueprints ? IconKind.Cancel : IconKind.Demolish;
            cancel.tooltip = blueprints ? "Cancel" : "Demolish";
            refund.text = blueprints ? $"Cancel: refunds {back:0} (the full cost) while it is a blueprint."
                : $"Demolish: refunds {back:0} ({(demolishShare == 0.5f ? "half its cost" : $"{demolishShare:P0} of its cost")}).";
            confirm.EnableInClassList("removal__confirm--open", ConfirmOpen);
            question.text = count > 1 ? $"Demolish {count} {name}s? You get {back:0} back." : $"Demolish {name}? You get {back:0} back.";
        }

        private void ShowProduction(UnitType[] produced, int count)
        {
            if (rowsFor != selection.BuildingType) BuildRows(produced);
            queueLabel.text = count > 1 ? "Production queue (each selected spawner)" : "Production queue";
            help.text = count > 1
                ? $"{selection.SpawnerQueue} queued across {count} spawners · each adds to every selected spawner · max {SpawnerRules.MaxQueue} each"
                : produced.Length > 1 ? $"Units come out in the order queued · max {SpawnerRules.MaxQueue}" : singleHelp;
            bool room = selection.SpawnerQueue < SpawnerRules.MaxQueue * count;
            foreach ((UnitType type, Label label, Button minus, Button[] adds) in queueRows)
            {
                int queued = selection.QueueOf(type);
                label.text = queued.ToString();
                minus.SetEnabled(queued > 0);
                foreach (Button add in adds) add.SetEnabled(room);
            }
        }

        /// <summary>One row per unit type the selected building makes (Figma: production rows, v0.7).</summary>
        private void BuildRows(UnitType[] produced)
        {
            rowsFor = selection.BuildingType;
            rows.Clear();
            queueRows.Clear();
            foreach (UnitType type in produced)
            {
                var row = new VisualElement { name = "production-row-" + type };
                row.AddToClassList("production__row");
                var slot = new VisualElement();
                slot.AddToClassList("icon-slot");
                var unit = new Label(type.ToString()) { name = "production-unit" };
                unit.AddToClassList("text");
                unit.AddToClassList("production__unit");
                var price = new Label(unitCosts.TryGetValue(type, out float cost) ? $"{cost:0}" : "") { name = "production-cost" };
                price.AddToClassList("production__cost");
                Button minus = Step(type, "−", -1, "production-minus", false);
                var queued = new Label("0") { name = "production-count" };
                queued.AddToClassList("text");
                queued.AddToClassList("text--readout");
                queued.AddToClassList("production__count");
                Button[] adds = { Step(type, "+", 1, "production-plus", false), Step(type, "+10", 10, "production-plus-10", true), Step(type, "+50", 50, "production-plus-50", true) };
                row.Add(slot);
                row.Add(unit);
                row.Add(price);
                row.Add(minus);
                row.Add(queued);
                foreach (Button add in adds) row.Add(add);
                rows.Add(row);
                queueRows.Add((type, queued, minus, adds));
            }
        }

        private Button Step(UnitType type, string text, int delta, string name, bool wide)
        {
            var button = new Button(() => { WAR2D.Audio.AudioService.Ui("ui_click"); QueueClicked?.Invoke(selection.BuildingIds, type, delta); }) { text = text, name = name };
            button.AddToClassList("button");
            button.AddToClassList("production__button");
            if (wide) button.AddToClassList("production__button--wide");
            return button;
        }

        private static void Display(VisualElement element, bool shown) => element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

        public void Dispose()
        {
            selection.Changed -= Show;
            hud.ResourcesChanged -= Show;
        }
    }
}
