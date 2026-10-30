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
    /// with Builders selected (v0.13, Figma <c>Command card – v0.13</c> layout B: Make — Miner and the producers —
    /// over Defend — Wall, Bomb, Radar, Gun turret, Jammer — with their costs, disabled while the player can't afford
    /// them, and one hover card for every build button: name and cost, role, footprint and upkeep), and, with own producers selected, the type selector (one button per type the building
    /// makes, the selected one active, a hover card with its cost, build time and role) above one queue row for
    /// that type; then Cancel (blueprints, full refund) or Demolish (finished, a share back, after an inline
    /// confirm). Raises events only; <see cref="HudController"/> sends them down the same paths as the keys.
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
        private readonly VisualElement card, divider, rows, removal, confirm, production, typesRow;
        private readonly Label title, help, queueLabel, buildLabel, buildHint, digHint, detonateHint, refund, question, cancelKey;
        private readonly Label info, mixedHint;
        private readonly VisualElement buildArea;
        private readonly Func<BuildingType, HoverCardContent> buildCard;
        private readonly Func<UnitType, HoverCardContent> unitCard;
        private readonly WAR2D.UI.HoverCard hover;
        private readonly float repairCostPerHealth;
        private readonly CommandIcon cancelIcon;
        private readonly Button cancel, wall, detonate, rallyAll;
        private readonly Dictionary<Label, Func<UnityEngine.InputSystem.InputAction>> keyLabels = new Dictionary<Label, Func<UnityEngine.InputSystem.InputAction>>();
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
        private readonly List<Label> typeKeyLabels = new List<Label>();

        /// <summary>
        /// The build buttons by card slot (v0.15.1, Figma <c>v0.15.1 Hotkeys and polish</c> H1): the top row (Make) and the bottom row
        /// (Defend); <see cref="BuildingType.None"/> is the Wall.
        /// </summary>
        public static readonly BuildingType[] TopBuilds = { BuildingType.Miner, BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.SiegeWorkshop };

        /// <inheritdoc cref="TopBuilds"/>
        public static readonly BuildingType[] BottomBuilds = { BuildingType.None, BuildingType.Bomb, BuildingType.Radar, BuildingType.GunTurret, BuildingType.Jammer };

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

        /// <summary>Raised when the selected wall tiles are to be cancelled or demolished (after the confirm, unless all are blueprints).</summary>
        public event Action RemoveWallsConfirmed;

        /// <summary>Raised by the Rally all button in the production card's Rally point row (v0.15, frame 8).</summary>
        public event Action RallyAllClicked;

        /// <summary>True while the Demolish confirm is showing.</summary>
        public bool ConfirmOpen { get; private set; }

        /// <param name="buildCosts">Each buildable type's upfront cost (types without a button are ignored).</param>
        /// <param name="unitCosts">Each produced unit type's cost, for the type selector.</param>
        /// <param name="buildSeconds">Seconds to make one unit of a type at a building, for the info line and hover card.</param>
        /// <param name="roleOf">A short role line per unit type for the hover card (range, splash and so on).</param>
        /// <param name="switchClearsQueue">True once the server clears a producer's queue when its type changes (v0.12 plan): the hover card then warns.</param>
        /// <param name="buildCard">A build button's hover card (v0.15: role, then Cost, Upkeep, Size and reach); null shows the name and cost only.</param>
        /// <param name="unitCard">A production type's hover card (role, Cost, Build, Health, Damage, Range); null shows the name only.</param>
        /// <param name="hover">The shared hover card (v0.15, frame 3), opened left of the command card; null for none.</param>
        public CommandCardController(VisualElement root, SelectionModel selection, HudModel hud, IReadOnlyDictionary<BuildingType, float> buildCosts,
            IReadOnlyDictionary<UnitType, float> unitCosts, float wallCost, float demolishShare, Func<UnitType, BuildingType, float> buildSeconds,
            Func<UnitType, string> roleOf, bool switchClearsQueue, Func<BuildingType, HoverCardContent> buildCard = null,
            Func<UnitType, HoverCardContent> unitCard = null, WAR2D.UI.HoverCard hover = null, float repairCostPerHealth = 0f)
        {
            this.buildCard = buildCard;
            this.unitCard = unitCard;
            this.hover = hover;
            this.repairCostPerHealth = repairCostPerHealth;
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
            detonateHint = root.Q<Label>("detonate-hint");
            detonate = root.Q<Button>("cmd-detonate");
            buildArea = root.Q("builds");

            AddOrder(root, "cmd-move", OrderKind.Move, () => GameInput.Command);
            AddOrder(root, "cmd-attack-move", OrderKind.AttackMove, () => GameInput.AttackMove);
            AddOrder(root, "cmd-stop", OrderKind.Stop, () => GameInput.Stop);
            AddOrder(root, "cmd-hold", OrderKind.Hold, () => GameInput.Hold);
            AddOrder(root, "cmd-repair", OrderKind.Repair, () => GameInput.Repair);
            AddOrder(root, "cmd-dig", OrderKind.Dig, () => GameInput.Dig);
            detonateKind = OrderKind.Detonate;
            AddOrder(root, "cmd-detonate", OrderKind.Detonate, () => GameInput.Detonate);
            bomberType = UnitType.SuicideBomber;
            AddBuild(root, "build-miner", BuildingType.Miner);
            AddBuild(root, "build-barracks", BuildingType.Barracks);
            AddBuild(root, "build-heavy-factory", BuildingType.HeavyFactory);
            AddBuild(root, "build-siege-workshop", BuildingType.SiegeWorkshop);
            AddBuild(root, "build-bomb", BuildingType.Bomb);
            AddBuild(root, "build-radar", BuildingType.Radar);
            AddBuild(root, "build-gun-turret", BuildingType.GunTurret);
            AddBuild(root, "build-jammer", BuildingType.Jammer);
            AddSlotKeys(root);
            wall = root.Q<Button>("build-wall");
            wall.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); WallClicked?.Invoke(); };
            AttachBuildCard(wall, BuildingType.None);
            root.Q<Label>("build-wall-cost").text = $"{wallCost:0.##}/tile";
            buildHint.text = $"Wall: press and drag on the map to lay a line ({wallCost:0.##} per tile).";

            cancel.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); RequestRemove(); };
            keyLabels[cancelKey] = () => GameInput.Cancel;
            hover?.Attach(cancel, RemoveCard, WAR2D.UI.HoverCard.Side.Left, card);
            rallyAll = root.Q<Button>("cmd-rally-all");
            if (rallyAll != null)
            {
                rallyAll.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); RallyAllClicked?.Invoke(); };
                hover?.Attach(rallyAll, () => HoverText.RallyAll(RallyAllKey()), WAR2D.UI.HoverCard.Side.Left, card);
            }
            ShowKeys();
            root.Q<Button>("removal-yes").clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); Remove(); };
            root.Q<Button>("removal-no").clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); CloseConfirm(); };

            selection.Changed += Show;
            hud.ResourcesChanged += Show;
            Show();
        }

        private void AddOrder(VisualElement root, string name, OrderKind kind, Func<UnityEngine.InputSystem.InputAction> action)
        {
            Button button = root.Q<Button>(name);
            button.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); OrderClicked?.Invoke(kind); };
            orders[kind] = button;
            Label key = root.Q<Label>(name + "-key");
            if (key != null) keyLabels[key] = action;
            hover?.Attach(button, () => OrderCard(kind, action()), WAR2D.UI.HoverCard.Side.Left, card);
        }

        /// <summary>An order button's card: its words, the current key, and why it is greyed out.</summary>
        private HoverCardContent OrderCard(OrderKind kind, UnityEngine.InputSystem.InputAction action)
        {
            HoverCardContent c = HoverText.Order(kind, WAR2D.UI.HoverCard.KeyOf(action), WAR2D.UI.HoverCard.KeyOf(GameInput.QueueModifier), repairCostPerHealth);
            c.Reason = HoverText.OrderReason(kind, selection.Count > 0);
            return c;
        }

        /// <summary>The Rally all key text: the assign modifier and the order button ("Ctrl RMB").</summary>
        private static string RallyAllKey() => $"{WAR2D.UI.HoverCard.KeyOf(GameInput.AssignModifier)} {WAR2D.UI.HoverCard.KeyOf(GameInput.Command)}";

        /// <summary>
        /// Writes the players' current bindings into the command buttons' key slots (a rebind in Settings shows at once).
        /// The HUD calls it when Settings closes.
        /// </summary>
        public void ShowKeys()
        {
            foreach (KeyValuePair<Label, Func<UnityEngine.InputSystem.InputAction>> pair in keyLabels)
            {
                string text = WAR2D.UI.HoverCard.KeyOf(pair.Value());
                if (text.Length > 0) pair.Key.text = text;
            }
            for (int slot = 0; slot < typeKeyLabels.Count; slot++) typeKeyLabels[slot].text = WAR2D.UI.HoverCard.KeyOf(GameInput.CardTop(slot));
            Label rallyKey = rallyAll?.Q<Label>("cmd-rally-all-key");
            if (rallyKey != null) rallyKey.text = RallyAllKey();
        }

        /// <summary>Shows Rally all as active while it waits for its click.</summary>
        public void ShowRallyAllArmed(bool armed) => rallyAll?.EnableInClassList("command-button--active", armed);

        /// <summary>The Cancel/Demolish button's card.</summary>
        private HoverCardContent RemoveCard()
        {
            string key = WAR2D.UI.HoverCard.KeyOf(GameInput.Cancel);
            bool blueprints = WallsOnly ? selection.WallBlueprints == selection.WallCount : selection.AllBlueprints;
            if (blueprints) return new HoverCardContent("Cancel", key, "Take the blueprint back.").Pair("Refund", "full cost", HoverTone.Cost);
            return new HoverCardContent("Demolish", key, "Tear this building down, after a confirm.")
                .Pair("Refund", $"{demolishShare:P0}", HoverTone.Cost).Line("Blueprint: Cancel, full refund");
        }

        /// <summary>The build button's element name for a slot's building (<see cref="BuildingType.None"/>: the Wall).</summary>
        private static string BuildName(BuildingType type) => type switch
        {
            BuildingType.None => "build-wall",
            BuildingType.HeavyFactory => "build-heavy-factory",
            BuildingType.SiegeWorkshop => "build-siege-workshop",
            BuildingType.GunTurret => "build-gun-turret",
            _ => "build-" + type.ToString().ToLowerInvariant(),
        };

        /// <summary>Puts the card-slot keys on the build buttons' corners (a rebind shows through <see cref="ShowKeys"/>).</summary>
        private void AddSlotKeys(VisualElement root)
        {
            for (int slot = 0; slot < TopBuilds.Length; slot++)
            {
                int s = slot;
                Label key = root.Q<Label>(BuildName(TopBuilds[slot]) + "-key");
                if (key != null) keyLabels[key] = () => GameInput.CardTop(s);
            }
            for (int slot = 0; slot < BottomBuilds.Length; slot++)
            {
                int s = slot;
                Label key = root.Q<Label>(BuildName(BottomBuilds[slot]) + "-key");
                if (key != null) keyLabels[key] = () => GameInput.CardBottom(s);
            }
        }

        /// <summary>The key of the card slot holding a build button, or null.</summary>
        private static string BuildKey(BuildingType type)
        {
            int top = Array.IndexOf(TopBuilds, type), bottom = Array.IndexOf(BottomBuilds, type);
            if (top >= 0) return WAR2D.UI.HoverCard.KeyOf(GameInput.CardTop(top));
            return bottom >= 0 ? WAR2D.UI.HoverCard.KeyOf(GameInput.CardBottom(bottom)) : null;
        }

        /// <summary>The first slot (0-based) whose key went down this frame, or -1.</summary>
        private static int PressedSlot(int slots, Func<int, UnityEngine.InputSystem.InputAction> action)
        {
            for (int slot = 0; slot < slots; slot++) if (action(slot).WasPressedThisFrame()) return slot;
            return -1;
        }

        /// <summary>
        /// The card-slot keys (v0.15.1, Figma H1–H2), once a frame while the HUD has the keyboard. With Builders selected a key does what
        /// its build button does (place that building, or arm the Wall drag). With own producers of one kind selected the top-row keys
        /// work the type selector: on the type being made a key adds one to every selected producer's queue (ten with the queue
        /// modifier), on another type it switches to it, as a click does. Returns the cursor notice when the key's button is greyed
        /// out ("Radar: need 250 (you have 90)", "Queue full"), else null.
        /// </summary>
        public string HandleHotkeys()
        {
            int top = PressedSlot(GameInput.CardTopSlots, GameInput.CardTop);
            int bottom = top >= 0 ? -1 : PressedSlot(GameInput.CardBottomSlots, GameInput.CardBottom);
            if (top < 0 && bottom < 0) return null;
            bool building = selection.BuildingId != 0 || WallsOnly;
            if (!building && selection.HasBuilders)
            {
                if (top >= TopBuilds.Length || bottom >= BottomBuilds.Length) return null;
                return Build(top >= 0 ? TopBuilds[top] : BottomBuilds[bottom]);
            }
            bool producing = building && !WallsOnly && SpawnerRules.IsProducer(selection.BuildingType) && selection.BlueprintCount == 0 && !selection.MixedKinds;
            if (!producing || top < 0 || rowsFor != selection.BuildingType || top >= shownTypes.Length) return null;
            UnitType type = shownTypes[top];
            if (type != Chosen()) { Pick(type); return null; }
            if (selection.SpawnerQueue >= SpawnerRules.MaxQueue * selection.BuildingIds.Count) return "Queue full";
            WAR2D.Audio.AudioService.Ui("ui_click");
            QueueClicked?.Invoke(selection.BuildingIds, type, GameInput.QueueModifier.IsPressed() ? 10 : 1);
            return null;
        }

        /// <summary>A build key: the button's click when it is enabled, else why not.</summary>
        private string Build(BuildingType type)
        {
            bool isWall = type == BuildingType.None;
            float cost = isWall ? wallCost : buildCosts.TryGetValue(type, out float c) ? c : float.PositiveInfinity;
            string name = isWall ? "Wall" : AlertFeedModel.BuildingName(type) ?? AlertFeedModel.Spaced(type.ToString());
            if (hud.Resources < cost) return $"{name}: need {cost:0.##} (you have {Math.Floor(hud.Resources):0})";
            WAR2D.Audio.AudioService.Ui("ui_click");
            if (isWall) WallClicked?.Invoke();
            else BuildClicked?.Invoke(type);
            return null;
        }

        private void AddBuild(VisualElement root, string name, BuildingType type)
        {
            Button button = root.Q<Button>(name);
            button.clicked += () => { WAR2D.Audio.AudioService.Ui("ui_click"); BuildClicked?.Invoke(type); };
            if (buildCosts.TryGetValue(type, out float cost)) root.Q<Label>(name + "-cost").text = $"{cost:0.##}";
            builds[type] = button;
            AttachBuildCard(button, type);
        }

        /// <summary>The shared hover card over a build button (<see cref="BuildingType.None"/> is the Wall).</summary>
        private void AttachBuildCard(Button button, BuildingType type) => hover?.Attach(button, () => BuildTip(type), WAR2D.UI.HoverCard.Side.Left, card);

        /// <summary>
        /// The card for a build button (frame 3, Gun turret): name, role, then Cost (gold, or red when it is what greys the
        /// button out), Upkeep, Size and reach, and "Need 150 (you have 90)" when the player can't afford it.
        /// </summary>
        public HoverCardContent BuildTip(BuildingType type)
        {
            string name = type == BuildingType.None ? "Wall" : AlertFeedModel.BuildingName(type) ?? AlertFeedModel.Spaced(type.ToString());
            float cost = type == BuildingType.None ? wallCost : buildCosts.TryGetValue(type, out float c) ? c : 0f;
            HoverCardContent from = buildCard?.Invoke(type);
            var tip = new HoverCardContent(name, BuildKey(type), from?.Role);
            bool afford = hud.Resources >= cost;
            tip.Pair("Cost", type == BuildingType.None ? $"{cost:0.##} per tile" : $"{cost:0.##}", afford ? HoverTone.Cost : HoverTone.Danger);
            if (from != null)
            {
                foreach ((string label, string value, HoverTone tone) in from.Pairs) if (label != "Cost") tip.Pair(label, value, tone);
                foreach (string line in from.Lines) tip.Line(line);
            }
            tip.Reason = HoverText.Afford(cost, hud.Resources);
            return tip;
        }

        /// <summary>
        /// The Cancel/Demolish button or key: blueprints go at once (full refund, nothing finished is lost); finished
        /// buildings ask first, and a second press while the confirm shows is the yes.
        /// </summary>
        public void RequestRemove()
        {
            if (!Removable) return;
            if ((WallsOnly ? selection.WallBlueprints == selection.WallCount : selection.AllBlueprints) || ConfirmOpen || !SettingsStore.Current.ConfirmDemolish) Remove();
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
            if (WallsOnly) RemoveWallsConfirmed?.Invoke();
            else if (Removable) RemoveConfirmed?.Invoke(selection.BuildingIds);
            Show();
        }

        /// <summary>True when the selected own buildings may be cancelled or demolished (never the HQ).</summary>
        private bool Removable => WallsOnly || (selection.BuildingId != 0 && selection.BuildingType != BuildingType.Base);

        /// <summary>True when own walls or wall blueprints are selected and nothing else.</summary>
        private bool WallsOnly => selection.WallCount > 0 && selection.BuildingId == 0 && selection.Count == 0;

        private void Show()
        {
            bool building = selection.BuildingId != 0 || WallsOnly;
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
            Display(buildArea, builders);
            Display(buildHint, builders);
            Display(removal, building && Removable);
            // A blueprint makes nothing yet: production shows once the selected buildings are all finished.
            bool producing = building && SpawnerRules.IsProducer(selection.BuildingType) && selection.BlueprintCount == 0;
            Display(production, producing);
            hover?.Refresh();
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

        private void ShowWalls()
        {
            int count = selection.WallCount, blueprints = selection.WallBlueprints;
            bool allBlueprints = blueprints == count;
            float back = ConstructionRules.Refund(true, wallCost, demolishShare) * blueprints +
                         ConstructionRules.Refund(false, wallCost, demolishShare) * (count - blueprints);
            title.text = count > 1 ? $"Walls: {count} selected" : allBlueprints ? "Wall (blueprint)" : "Wall";
            cancelIcon.Icon = allBlueprints ? IconKind.Cancel : IconKind.Demolish;
            cancel.tooltip = allBlueprints ? "Cancel" : "Demolish";
            refund.text = allBlueprints ? $"Cancel: refunds {back:0} (the full cost) while they are blueprints."
                : $"Demolish: refunds {back:0} (blueprints in full, built walls {demolishShare:P0} of their cost).";
            confirm.EnableInClassList("removal__confirm--open", ConfirmOpen);
            question.text = count > 1 ? $"Demolish {count} walls? You get {back:0} back." : $"Demolish this wall? You get {back:0} back.";
        }

        private void ShowBuilding(int count)
        {
            if (WallsOnly) { ShowWalls(); return; }
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
            if (mixed) return;
            if (rowsFor != selection.BuildingType) BuildProduction(produced);
            UnitType chosen = Chosen();
            IReadOnlyList<UnitType> inUse = selection.ProductionTypes;
            foreach (KeyValuePair<UnitType, Button> type in typeButtons)
            {
                bool active = inUse.Count > 0 ? ListHas(inUse, type.Key) : type.Key == chosen;
                type.Value.EnableInClassList("command-button--active", active);
            }
            string building = AlertFeedModel.BuildingName(selection.BuildingType) ?? AlertFeedModel.Spaced(selection.BuildingType.ToString());
            info.text = inUse.Count > 1
                ? "Mixed: " + string.Join(", ", System.Linq.Enumerable.Select(inUse, t => $"{AlertFeedModel.UnitName(t)} ({selection.QueueOf(t)})"))
                : $"{AlertFeedModel.UnitName(chosen)} · {Cost(chosen)} each · {Seconds(chosen)} s per unit";
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

        private static bool ListHas(IReadOnlyList<UnitType> list, UnitType type)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == type) return true;
            return false;
        }

        /// <summary>
        /// The type the queue row works on: the server's choice for the first selected producer, else the one picked
        /// for this kind of building, else the type with the most queued, else the first it makes.
        /// </summary>
        private UnitType Chosen()
        {
            // The server's choice for the first selected producer wins over the local pick.
            if (selection.ProductionTypes.Count > 0 && Array.IndexOf(shownTypes, selection.ProductionTypes[0]) >= 0) return selection.ProductionTypes[0];
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
            typeKeyLabels.Clear();
            foreach (UnitType type in produced)
            {
                var button = new Button(() => Pick(type)) { name = "type-" + type };
                button.AddToClassList("command-button");
                if (typeKeyLabels.Count < GameInput.CardTopSlots)
                {
                    // v0.15.1 (Figma H2): the top-row card slot that works this type.
                    var key = new Label(WAR2D.UI.HoverCard.KeyOf(GameInput.CardTop(typeKeyLabels.Count))) { name = "type-key", pickingMode = PickingMode.Ignore };
                    key.AddToClassList("command-button__hotkey");
                    button.Add(key);
                    typeKeyLabels.Add(key);
                }
                var icon = new CommandIcon();
                if (Enum.TryParse(type.ToString(), out IconKind kind)) icon.Icon = kind;
                button.Add(icon);
                var cost = new Label(Cost(type)) { name = "type-cost" };
                cost.AddToClassList("command-button__key");
                cost.AddToClassList("command-button__cost");
                button.Add(cost);
                hover?.Attach(button, () => TypeTip(type), WAR2D.UI.HoverCard.Side.Left, card);
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
            chosenTypes[selection.BuildingType] = type;
            hover?.Hide();
            // Always sent: the model can lag a frame behind the server, which ignores a pick of the type already made.
            ProductionSelected?.Invoke(selection.BuildingIds, type);
            Show();
        }

        /// <summary>The hover card of a type button (frame 3, Heavy): role, cost, build time and stats, and a warning when switching would clear a queue.</summary>
        public HoverCardContent TypeTip(UnitType type)
        {
            HoverCardContent tip = unitCard?.Invoke(type) ?? new HoverCardContent(AlertFeedModel.UnitName(type), null, roleOf?.Invoke(type))
                .Pair("Cost", Cost(type), HoverTone.Cost).Pair("Build", $"{Seconds(type)} s");
            int slot = Array.IndexOf(shownTypes, type);
            if (slot >= 0 && slot < GameInput.CardTopSlots) tip.Key = WAR2D.UI.HoverCard.KeyOf(GameInput.CardTop(slot));
            UnitType chosen = Chosen();
            int queued = selection.SpawnerQueue;
            if (switchClearsQueue && type != chosen && queued > 0) tip.Warning = $"Switching clears the queue ({queued} {AlertFeedModel.UnitName(chosen)})";
            return tip;
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
