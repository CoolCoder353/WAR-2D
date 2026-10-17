using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// Binds the command card (<c>Assets/UI/Hud/CommandCard.uxml</c>, Figma <c>Command card – v0.7</c> and
    /// <c>Command card – v0.12</c>): the order buttons (Move, Attack-move, Stop, Hold, and Repair with Builders,
    /// Dig with Diggers or Detonate with Suicide bombers selected; the armed order shows as active), the build row
    /// with Builders selected (Miner, the producers, Wall, Bomb, Radar, with their costs, disabled while the player
    /// can't afford them), and, with own producers selected, the type selector (one button per type the building
    /// makes, the selected one active, a hover card with its cost, build time and role) above one queue row for
    /// that type; then Cancel (blueprints, full refund) or Demolish (finished, a share back, after an inline
    /// confirm). Raises events only; <see cref="HudController"/> sends them down the same paths as the keys.
    /// Buttons for types the game doesn't have yet (the Heavy Factory, the Siege Workshop, Detonate) stay hidden
    /// until the v0.12 plan adds their enum values (found by name, so this compiles before and after).
    /// </summary>
    public sealed class CommandCardController : IDisposable
    {
        private readonly SelectionModel selection;
        private readonly HudModel hud;
        private readonly IReadOnlyDictionary<BuildingType, float> buildCosts;
        private readonly IReadOnlyDictionary<UnitType, float> unitCosts;
        private readonly float wallCost, demolishShare;
        private readonly Func<UnitType, BuildingType, float> buildSeconds;
        private readonly Func<UnitType, string> roleOf;
        private readonly bool switchClearsQueue;
        private readonly VisualElement card, divider, rows, removal, confirm, production, typesRow, tooltip;
        private readonly Label title, help, queueLabel, buildLabel, buildHint, digHint, detonateHint, refund, question, cancelKey;
        private readonly Label info, mixedHint, tipTitle, tipRole, tipWarning;
        private readonly CommandIcon cancelIcon;
        private readonly Button cancel, wall, detonate;
        private readonly OrderKind? detonateKind;
        private readonly UnitType? bomberType;
        private readonly Dictionary<OrderKind, Button> orders = new Dictionary<OrderKind, Button>();
        private readonly Dictionary<BuildingType, Button> builds = new Dictionary<BuildingType, Button>();
        private readonly Dictionary<UnitType, Button> typeButtons = new Dictionary<UnitType, Button>();
        private readonly Dictionary<BuildingType, UnitType> chosenTypes = new Dictionary<BuildingType, UnitType>();
        private Label queueCount;
        private Button queueMinus;
        private Button[] queueAdds = Array.Empty<Button>();
        private UnitType[] shownTypes = Array.Empty<UnitType>();
        private BuildingType rowsFor = BuildingType.None;

        /// <summary>Raised by an order button (Move and AttackMove arm, Repair and Dig arm, Stop and Hold act).</summary>
        public event Action<OrderKind> OrderClicked;

        /// <summary>Raised by a building's build button.</summary>
        public event Action<BuildingType> BuildClicked;

        /// <summary>Raised by the Wall button: the next press on the map starts a wall drag.</summary>
        public event Action WallClicked;

        /// <summary>Raised by the queue row's buttons: a step (−1, +1, +10, +50) of the selected unit type for every selected producer.</summary>
        public event Action<IReadOnlyList<int>, UnitType, int> QueueClicked;

        /// <summary>Raised when a type that isn't selected is picked in the type selector, for every selected producer.</summary>
        public event Action<IReadOnlyList<int>, UnitType> ProductionSelected;

        /// <summary>Raised when the selected buildings are to be cancelled or demolished (after the confirm, for finished ones).</summary>
        public event Action<IReadOnlyList<int>> RemoveConfirmed;

        /// <summary>True while the Demolish confirm is showing.</summary>
        public bool ConfirmOpen { get; private set; }

        /// <param name="buildCosts">Each buildable type's upfront cost (types without a button are ignored).</param>
        /// <param name="unitCosts">Each produced unit type's cost, for the type selector.</param>
        /// <param name="buildSeconds">Seconds to make one unit of a type at a building, for the info line and hover card.</param>
        /// <param name="roleOf">A short role line per unit type for the hover card (range, splash and so on).</param>
        /// <param name="switchClearsQueue">True once the server clears a producer's queue when its type changes (v0.12 plan): the hover card then warns.</param>
        public CommandCardController(VisualElement root, SelectionModel selection, HudModel hud, IReadOnlyDictionary<BuildingType, float> buildCosts,
            IReadOnlyDictionary<UnitType, float> unitCosts, float wallCost, float demolishShare, Func<UnitType, BuildingType, float> buildSeconds,
            Func<UnitType, string> roleOf, bool switchClearsQueue)
        {
            this.buildSeconds = buildSeconds;
            this.roleOf = roleOf;
            this.switchClearsQueue = switchClearsQueue;
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
            production = root.Q("production");
            typesRow = root.Q("production-types");
            info = root.Q<Label>("production-info");
            mixedHint = root.Q<Label>("production-mixed");
            tooltip = root.Q("production-tooltip");
            tipTitle = root.Q<Label>("production-tooltip-title");
            tipRole = root.Q<Label>("production-tooltip-role");
            tipWarning = root.Q<Label>("production-tooltip-warning");
            detonateHint = root.Q<Label>("detonate-hint");
            detonate = root.Q<Button>("cmd-detonate");

            AddOrder(root, "cmd-move", OrderKind.Move);
            AddOrder(root, "cmd-attack-move", OrderKind.AttackMove);
            AddOrder(root, "cmd-stop", OrderKind.Stop);
            AddOrder(root, "cmd-hold", OrderKind.Hold);
            AddOrder(root, "cmd-repair", OrderKind.Repair);
            AddOrder(root, "cmd-dig", OrderKind.Dig);
            if (Enum.TryParse("Detonate", out OrderKind kind)) { detonateKind = kind; AddOrder(root, "cmd-detonate", kind); }
            if (Enum.TryParse("SuicideBomber", out UnitType bomber)) bomberType = bomber;
            AddBuild(root, "build-miner", BuildingType.Miner);
            AddBuild(root, "build-barracks", BuildingType.SmallUnitSpawner);
            AddBuildNamed(root, "build-heavy-factory", "HeavyFactory");
            AddBuildNamed(root, "build-siege-workshop", "SiegeWorkshop");
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

        /// <summary>A build button for a type found by name; hidden for good while the game has no such type.</summary>
        private void AddBuildNamed(VisualElement root, string name, string type)
        {
            if (Enum.TryParse(type, out BuildingType parsed)) AddBuild(root, name, parsed);
            else Display(root.Q<Button>(name), false);
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
            bool bombers = !building && bomberType.HasValue && detonateKind.HasValue && selection.HasType(bomberType.Value);
            card.EnableInClassList("command-card--building", building);
            card.EnableInClassList("command-card--builders", builders);
            if (!building || !Removable) ConfirmOpen = false;
            // Inline display wins over the stylesheet, so every section is set either way.
            Display(digHint, diggers && !builders);
            Display(detonateHint, bombers && !builders && !diggers);
            // The build row is the Builders' (v0.7): without them, nothing can build.
            Display(divider, builders);
            Display(buildLabel, builders);
            Display(card.Q("builds"), builders);
            Display(buildHint, builders);
            Display(removal, building && Removable);
            // A blueprint makes nothing yet: production shows once the selected buildings are all finished.
            bool producing = building && SpawnerRules.IsProducer(selection.BuildingType) && selection.BlueprintCount == 0;
            Display(production, producing);
            if (!producing) tooltip.EnableInClassList("production__tooltip--open", false);
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
            Display(detonate, bombers);
            foreach (KeyValuePair<BuildingType, Button> build in builds)
                build.Value.SetEnabled(buildCosts.TryGetValue(build.Key, out float cost) && hud.Resources >= cost);
            wall.SetEnabled(hud.Resources >= wallCost);
        }

        private void ShowBuilding(int count)
        {
            UnitType[] produced = SpawnerRules.Produces(selection.BuildingType);
            string name = AlertFeedModel.BuildingName(selection.BuildingType) ?? AlertFeedModel.Spaced(selection.BuildingType.ToString());
            title.text = selection.MixedKinds ? $"Commands: {count} producers selected"
                : count > 1 ? $"Commands: {count} {Plural(name)} selected"
                : selection.AllBlueprints ? $"{name} (blueprint) · {selection.BuildingProgress}%"
                : name;

            if (produced.Length > 0 && selection.BlueprintCount == 0) ShowProduction(produced, count);
            if (!Removable) return;
            float cost = buildCosts.TryGetValue(selection.BuildingType, out float c) ? c : 0f;
            float back = selection.RefundFor(cost, demolishShare);
            bool blueprints = selection.AllBlueprints;
            cancelIcon.Icon = blueprints ? IconKind.Cancel : IconKind.Demolish;
            cancel.tooltip = blueprints ? "Cancel" : "Demolish";
            refund.text = blueprints ? $"Cancel: refunds {back:0} (the full cost) while it is a blueprint."
                : $"Demolish: refunds {back:0} ({(demolishShare == 0.5f ? "half its cost" : $"{demolishShare:P0} of its cost")}).";
            confirm.EnableInClassList("removal__confirm--open", ConfirmOpen);
            question.text = count > 1 ? $"Demolish {count} {Plural(name)}? You get {back:0} back." : $"Demolish {name}? You get {back:0} back.";
        }

        /// <summary>
        /// The type selector and the one queue row (Figma <c>Command card – v0.12</c>). Producers of different kinds
        /// share no row: only a hint and the rally line show.
        /// </summary>
        private void ShowProduction(UnitType[] produced, int count)
        {
            bool mixed = selection.MixedKinds;
            Display(typesRow, !mixed);
            Display(info, !mixed);
            Display(rows, !mixed);
            Display(help, !mixed);
            Display(mixedHint, mixed);
            if (mixed)
            {
                tooltip.EnableInClassList("production__tooltip--open", false);
                return;
            }
            if (rowsFor != selection.BuildingType) BuildProduction(produced);
            UnitType chosen = Chosen();
            foreach (KeyValuePair<UnitType, Button> type in typeButtons)
                type.Value.EnableInClassList("command-button--active", type.Key == chosen);
            string building = AlertFeedModel.BuildingName(selection.BuildingType) ?? AlertFeedModel.Spaced(selection.BuildingType.ToString());
            info.text = $"{AlertFeedModel.UnitName(chosen)} · {Cost(chosen)} each · {Seconds(chosen)} s per unit";
            queueLabel.text = "Production";
            help.text = count > 1
                ? $"{selection.SpawnerQueue} queued across {count} {Plural(building)} · max {SpawnerRules.MaxQueue} each · cost charged at spawn"
                : $"max {SpawnerRules.MaxQueue} · cost charged at spawn";
            int queued = selection.QueueOf(chosen);
            queueCount.text = queued.ToString();
            queueMinus.SetEnabled(queued > 0);
            bool room = selection.SpawnerQueue < SpawnerRules.MaxQueue * count;
            foreach (Button add in queueAdds) add.SetEnabled(room);
        }

        /// <summary>
        /// The type the queue row works on: the one picked for this kind of building, else the type with the most
        /// queued (so a producer already making something shows it), else the first it makes.
        /// </summary>
        private UnitType Chosen()
        {
            if (chosenTypes.TryGetValue(selection.BuildingType, out UnitType picked) && Array.IndexOf(shownTypes, picked) >= 0) return picked;
            UnitType best = shownTypes[0];
            foreach (UnitType type in shownTypes)
                if (selection.QueueOf(type) > selection.QueueOf(best)) best = type;
            return best;
        }

        /// <summary>One command button per type the building makes (cost in the key slot), and the queue row.</summary>
        private void BuildProduction(UnitType[] produced)
        {
            rowsFor = selection.BuildingType;
            shownTypes = produced;
            typesRow.Clear();
            typeButtons.Clear();
            foreach (UnitType type in produced)
            {
                var button = new Button(() => Pick(type)) { name = "type-" + type };
                button.AddToClassList("command-button");
                var icon = new CommandIcon();
                if (Enum.TryParse(type.ToString(), out IconKind kind)) icon.Icon = kind;
                button.Add(icon);
                var cost = new Label(Cost(type)) { name = "type-cost" };
                cost.AddToClassList("command-button__key");
                cost.AddToClassList("command-button__cost");
                button.Add(cost);
                button.RegisterCallback<PointerEnterEvent>(_ => ShowTip(type, button));
                button.RegisterCallback<PointerLeaveEvent>(_ => tooltip.EnableInClassList("production__tooltip--open", false));
                typesRow.Add(button);
                typeButtons[type] = button;
            }

            rows.Clear();
            var row = new VisualElement { name = "production-row" };
            row.AddToClassList("production__row");
            queueMinus = Step("−", -1, "production-minus", false);
            queueCount = new Label("0") { name = "production-count" };
            queueCount.AddToClassList("text");
            queueCount.AddToClassList("text--readout");
            queueCount.AddToClassList("production__count");
            queueAdds = new[] { Step("+", 1, "production-plus", false), Step("+10", 10, "production-plus-10", true), Step("+50", 50, "production-plus-50", true) };
            row.Add(queueMinus);
            row.Add(queueCount);
            foreach (Button add in queueAdds) row.Add(add);
            rows.Add(row);
        }

        /// <summary>A type picked in the selector: the queue row works on it from now on.</summary>
        private void Pick(UnitType type)
        {
            WAR2D.Audio.AudioService.Ui("ui_click");
            bool changed = Chosen() != type;
            chosenTypes[selection.BuildingType] = type;
            tooltip.EnableInClassList("production__tooltip--open", false);
            if (changed) ProductionSelected?.Invoke(selection.BuildingIds, type);
            Show();
        }

        /// <summary>The hover card under a type button: cost, build time, role, and a warning when switching would clear a queue.</summary>
        private void ShowTip(UnitType type, Button button)
        {
            tipTitle.text = $"{AlertFeedModel.UnitName(type)} · {Cost(type)} · {Seconds(type)} s per unit";
            string role = roleOf?.Invoke(type);
            tipRole.text = role ?? "";
            Display(tipRole, !string.IsNullOrEmpty(role));
            UnitType chosen = Chosen();
            int queued = selection.SpawnerQueue;
            bool warn = switchClearsQueue && type != chosen && queued > 0;
            tipWarning.text = warn ? $"Switching clears the queue ({queued} {AlertFeedModel.UnitName(chosen)})" : "";
            Display(tipWarning, warn);
            Rect at = button.worldBound, area = production.worldBound;
            tooltip.style.left = at.xMin - area.xMin + at.width / 2f;
            tooltip.style.top = at.yMax - area.yMin + 4f;
            tooltip.EnableInClassList("production__tooltip--open", true);
        }

        private string Cost(UnitType type) => unitCosts.TryGetValue(type, out float cost) ? $"{cost:0}" : "";

        private string Seconds(UnitType type) => $"{buildSeconds(type, selection.BuildingType):0.#}";

        private static string Plural(string name) => name.EndsWith("s") ? name : name + "s";

        private Button Step(string text, int delta, string name, bool wide)
        {
            var button = new Button(() => { WAR2D.Audio.AudioService.Ui("ui_click"); QueueClicked?.Invoke(selection.BuildingIds, Chosen(), delta); }) { text = text, name = name };
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
