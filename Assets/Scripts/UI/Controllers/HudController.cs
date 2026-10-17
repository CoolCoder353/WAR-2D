using System.Collections.Generic;
using Config;
using Mirror;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// The match HUD: owns the <see cref="HudModel"/> and <see cref="SelectionModel"/>, refreshes them from
    /// client data, and wires the section controllers to <c>Assets/UI/Hud/Hud.uxml</c>. Button events go
    /// down the same paths as the keys (<see cref="UnitCommander"/>, <see cref="BuildingPlacement"/>,
    /// <see cref="WorldStateManager"/>). Lives on the <see cref="UIDocument"/> in Map_2.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudController : MonoBehaviour
    {
        private const float PlayerRefreshSeconds = 0.25f;
        private const float SelectionRefreshSeconds = 0.1f;

        private TopBarController topBar;
        private SelectionPanelController selectionPanel;
        private CommandCardController commandCard;
        private SquadBarController squadBar;
        private HudOverlayController overlay;
        private StartMarkersController startMarkers;
        private readonly List<StartMarker> markers = new List<StartMarker>();
        private readonly List<string> teammatesPicked = new List<string>();
        private MinimapController minimap;
        private AlertFeedController alertFeed;
        private DiplomacyPanelController diplomacy;
        private ClientPlayer alertSource;
        private Button diplomacyButton;
        private InMatchMenuController matchMenu;
        private SettingsController settings;
        private GiftDialogController gift;
        private EndScreenController endScreen;
        private VisualElement root;
        private float playerTimer, selectionTimer;
        private int placedHQs, players;

        /// <summary>The HUD's data.</summary>
        public HudModel Model { get; } = new HudModel();

        /// <summary>The selection, command card and squad bar data.</summary>
        public SelectionModel Selection { get; } = new SelectionModel();

        /// <summary>The alert feed's data.</summary>
        public AlertFeedModel Alerts { get; private set; }

        /// <summary>The in-match menu's controller.</summary>
        public InMatchMenuController MatchMenu => matchMenu;

        /// <summary>The gift dialog's controller.</summary>
        public GiftDialogController Gift => gift;

        /// <summary>The end screen's controller.</summary>
        public EndScreenController EndScreen => endScreen;

        /// <summary>Raised by the in-match menu's Settings button.</summary>
        public event System.Action SettingsRequested;

        /// <summary>The diplomacy panel's controller.</summary>
        public DiplomacyPanelController Diplomacy => diplomacy;

        /// <summary>The minimap's controller.</summary>
        public MinimapController Minimap => minimap;

        /// <summary>The top bar's controller (its buttons are wired by later HUD sections).</summary>
        public TopBarController TopBar => topBar;

        private void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            Palettes.ApplyTo(root);
            Palettes.Changed += ApplyPalette;
            // Each section's template fills the screen; only its panels take the pointer.
            root.Query<TemplateContainer>().ForEach(t => t.pickingMode = PickingMode.Ignore);

            GameConfigData config = ConfigLoader.LoadConfig();
            var costs = new Dictionary<BuildingType, float>();
            foreach (KeyValuePair<BuildingType, BuildingConfig> building in config.Buildings) costs[building.Key] = building.Value.UpfrontCost;
            var unitCosts = new Dictionary<UnitType, float>();
            foreach (KeyValuePair<UnitType, UnitConfig> unit in config.Units) unitCosts[unit.Key] = unit.Value.UpfrontCost;
            float BuildSeconds(UnitType unit, BuildingType at) => config.Units.TryGetValue(unit, out UnitConfig u) ? u.BuildSeconds : 0f;
            string RoleOf(UnitType unit)
            {
                if (!config.Units.TryGetValue(unit, out UnitConfig u)) return null;
                if (u.BlastRadius > 0f) return $"Blast {u.BlastRadius:0.#}, health {u.Health:0}";
                if (u.Damage <= 0) return null;
                string role = $"Range {u.Range:0.#}";
                if (u.Splash > 0f) role += $", splash {u.Splash:0.#}";
                if (u.MinRange > 0f) role += $", can't fire closer than {u.MinRange:0.#}";
                return role + $", health {u.Health:0}";
            }

            topBar = new TopBarController(root, Model);
            selectionPanel = new SelectionPanelController(root, Selection);
            commandCard = new CommandCardController(root, Selection, Model, costs, unitCosts, config.Siege.WallCost, config.Siege.DemolishRefund,
                BuildSeconds, RoleOf, switchClearsQueue: true);
            squadBar = new SquadBarController(root, Selection);
            overlay = new HudOverlayController(root);
            startMarkers = new StartMarkersController(root);
            overlay.OptionClicked += FocusOption;
            startMarkers.Clicked += CentreCamera;
            minimap = new MinimapController(root);
            Alerts = new AlertFeedModel(config.Alerts.ShowSeconds);
            alertFeed = new AlertFeedController(root, Alerts);
            diplomacy = new DiplomacyPanelController(root, Model);
            diplomacyButton = root.Q<Button>("diplomacy-button");
            Model.DiplomacyChanged += ShowDiplomacyButton;
            ShowDiplomacyButton();

            Alerts.PingRequested += minimap.Ping;
            topBar.DiplomacyClicked += diplomacy.Toggle;
            diplomacy.AttackToggled += SetAttack;
            diplomacy.ShareToggled += SetShareVision;

            matchMenu = new InMatchMenuController(root);
            settings = new SettingsController(root);
            settings.Closed += matchMenu.Open;
            matchMenu.SettingsClicked += () => { matchMenu.Close(); settings.Open(); SettingsRequested?.Invoke(); };
            matchMenu.SurrenderConfirmed += () => GameCore.Instance?.Cmd_Surrender();
            matchMenu.LeaveClicked += () => { HostLeftNotice.LeavingOnPurpose = true; GameManager.Instance?.LeaveLobby(); };
            topBar.MenuClicked += matchMenu.Toggle;

            gift = new GiftDialogController(root, Model, config.Gifting.CooldownSeconds);
            gift.GiftRequested += (owner, amount) => GameCore.Instance?.Cmd_GiftResources((uint)owner, amount);
            topBar.GiftClicked += () => { diplomacy.SetOpen(false); gift.Toggle(); };
            topBar.DiplomacyClicked += gift.Close;

            endScreen = new EndScreenController(root);
            endScreen.MainMenuClicked += () => { HostLeftNotice.LeavingOnPurpose = true; GameManager.Instance?.LeaveLobby(); };
            endScreen.QuitClicked += () => GameManager.Instance?.QuitGame();
            ClientPlayer.MatchEnded += OnMatchEnded;
            GameCore.MatchStatsReceived += OnMatchStats;
            GameCore.ClearMatchStats(); // a new match

            Selection.FilterRequested += OnFilter;
            commandCard.OrderClicked += OnOrder;
            commandCard.BuildClicked += OnBuild;
            commandCard.QueueClicked += OnQueue;
            commandCard.ProductionSelected += OnProductionSelected;
            commandCard.WallClicked += OnWall;
            commandCard.RemoveConfirmed += OnRemove;
            squadBar.SquadClicked += OnSquad;
        }

        private void OnDisable()
        {
            Selection.FilterRequested -= OnFilter;
            topBar?.Dispose();
            selectionPanel?.Dispose();
            commandCard?.Dispose();
            squadBar?.Dispose();
            minimap?.Dispose();
            minimap = null;
            alertFeed?.Dispose();
            diplomacy?.Dispose();
            gift?.Dispose();
            settings?.Dispose();
            Palettes.Changed -= ApplyPalette;
            ClientPlayer.MatchEnded -= OnMatchEnded;
            GameCore.MatchStatsReceived -= OnMatchStats;
            Model.DiplomacyChanged -= ShowDiplomacyButton;
            if (alertSource != null) alertSource.AlertsReceived -= OnAlerts;
            alertSource = null;
            topBar = null;
            selectionPanel = null;
            commandCard = null;
            squadBar = null;
        }

        private bool cameraCentred;

        /// <summary>
        /// Once the match map is ready, puts the camera on the local player's HQ clearing (their first dealt option).
        /// </summary>
        private void CentreOnOwnClearing()
        {
            if (cameraCentred) return;
            GameCore core = GameCore.Instance;
            WorldStateManager wsm = WorldStateManager.Instance;
            if (core == null || wsm == null || wsm.Map == null || NetworkClient.localPlayer == null) return;
            if (!NetworkClient.localPlayer.TryGetComponent(out ClientPlayer me) || me.OwnStartOptions.Length == 0) return; // the deal hasn't arrived yet
            cameraCentred = true;
            Unity.Mathematics.int2[] sites = wsm.Map.HqSites;
            int index = me.OwnStartOptions[0];
            if (sites == null || index < 0 || index >= sites.Length) return;
            Unity.Mathematics.int2 site = sites[index];
            CentreCamera(new Vector2(site.x + 0.5f, site.y + 0.5f));
        }

        private static void CentreCamera(Vector2 point)
        {
            Camera cam = Camera.main;
            if (cam != null && cam.TryGetComponent(out Character.Character_Controler controller)) controller.CentreOn(point);
        }

        /// <summary>An Option button: move the camera to the local player's option at <paramref name="index"/>.</summary>
        private void FocusOption(int index)
        {
            Unity.Mathematics.int2[] sites = WorldStateManager.Instance != null && WorldStateManager.Instance.Map != null ? WorldStateManager.Instance.Map.HqSites : null;
            if (sites == null || NetworkClient.localPlayer == null || !NetworkClient.localPlayer.TryGetComponent(out ClientPlayer me)) return;
            if (index < 0 || index >= me.OwnStartOptions.Length) return;
            int site = me.OwnStartOptions[index];
            if (site >= 0 && site < sites.Length) CentreCamera(new Vector2(sites[site].x + 0.5f, sites[site].y + 0.5f));
        }

        /// <summary>
        /// The HQ prompt and the option badges from the local player's own deal (and their teammates'), while placing HQs
        /// (Figma: HUD / HQ placement – timed pick, Unlimited, teammate's options).
        /// </summary>
        private PlacementPrompt ShowStartOptions(GameCore core)
        {
            var prompt = new PlacementPrompt { Players = players, Placed = placedHQs, Focused = -1, PickSeconds = core.Settings.StartPickSeconds,
                SecondsLeft = core.HqPlacementEndTime - NetworkTime.time, TeammatesPicked = teammatesPicked };
            markers.Clear();
            teammatesPicked.Clear();
            WorldStateManager wsm = WorldStateManager.Instance;
            Unity.Mathematics.int2[] sites = wsm != null && wsm.Map != null ? wsm.Map.HqSites : null;
            ClientPlayer me = null;
            bool placing = core.CurrentState == GameState.PlacingHQ && sites != null && NetworkClient.localPlayer != null
                && NetworkClient.localPlayer.TryGetComponent(out me) && !me.hasPlacedHQ;
            if (me != null)
            {
                foreach (var teammate in me.TeammateStarts)
                    if (teammate.Value.pick >= 0) teammatesPicked.Add(NameOf(teammate.Key));
                prompt.HasTeammates = me.TeammateStarts.Count > 0;
            }
            if (!placing)
            {
                startMarkers.Show(markers, null);
                return prompt;
            }

            Camera cam = Camera.main;
            float radius = WAR2D.World.MapGenerator.HqClearRadius;
            prompt.Options = me.OwnStartOptions.Length;
            float nearest = float.MaxValue;
            for (int i = 0; i < me.OwnStartOptions.Length; i++)
            {
                int site = me.OwnStartOptions[i];
                if (site < 0 || site >= sites.Length) continue;
                var centre = new Vector2(sites[site].x + 0.5f, sites[site].y + 0.5f);
                markers.Add(new StartMarker { Centre = centre, Radius = radius, Colour = PlayerPalette.Of(me.colourIndex), OnScreen = (i + 1).ToString(), OffScreen = (i + 1).ToString() });
                if (cam == null) continue;
                Vector3 view = cam.WorldToViewportPoint(centre);
                float distance = ((Vector2)view - new Vector2(0.5f, 0.5f)).sqrMagnitude;
                if (view.x >= 0f && view.x <= 1f && view.y >= 0f && view.y <= 1f && distance < nearest) { nearest = distance; prompt.Focused = i; }
            }
            foreach (var teammate in me.TeammateStarts)
            {
                string name = NameOf(teammate.Key);
                Color32 colour = PlayerPalette.Of(NetworkClient.spawned.TryGetValue((uint)teammate.Key, out NetworkIdentity id) && id.TryGetComponent(out ClientPlayer other) ? other.colourIndex : -1);
                foreach (int site in teammate.Value.options)
                {
                    if (site < 0 || site >= sites.Length) continue;
                    bool picked = site == teammate.Value.pick;
                    markers.Add(new StartMarker { Centre = new Vector2(sites[site].x + 0.5f, sites[site].y + 0.5f), Radius = radius, Colour = colour,
                        OnScreen = picked ? $"{name}'s start" : $"{name} · option", OffScreen = name });
                }
            }
            startMarkers.Show(markers, cam);
            return prompt;
        }

        private void Update()
        {
            CentreOnOwnClearing();
            Model.PullResources();
            if (GameInput.Menu.WasPressedThisFrame() && !endScreen.IsShown)
            {
                if (commandCard.CloseConfirm()) { }
                else if (UnitCommander.Instance != null && UnitCommander.Instance.CancelWallDrag()) { }
                else if (settings.IsOpen) settings.Close();
                else if (gift.IsOpen) gift.Close();
                else if (diplomacy.IsOpen) diplomacy.SetOpen(false);
                else matchMenu.Toggle();
            }
            if (GameInput.Cancel.WasPressedThisFrame() && !matchMenu.IsOpen && !settings.IsOpen && !gift.IsOpen && !diplomacy.IsOpen) commandCard.RequestRemove();
            matchMenu.SetCanSurrender(root, GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.Playing && !endScreen.IsShown);
            if (endScreen.IsShown)
            {
                var (replayText, replayFraction) = EndScreenController.ReplayState(NetworkServer.active, WorldStateManager.Instance != null ? WorldStateManager.Instance.Recorder : null,
                    WAR2D.Net.Replay.ReplayReceiver.Current, SettingsStore.Current.SaveReplays);
                endScreen.ShowReplay(replayText, replayFraction);
            }
            minimap.Update(Time.unscaledDeltaTime);
            ShowWallReadout();
            Model.PullDiplomacy();
            Alerts.Expire(Time.unscaledTime);
            SubscribeAlerts();

            selectionTimer -= Time.unscaledDeltaTime;
            if (selectionTimer <= 0f)
            {
                selectionTimer = SelectionRefreshSeconds;
                Selection.Pull();
            }

            GameCore core = GameCore.Instance;
            playerTimer -= Time.unscaledDeltaTime;
            if (playerTimer <= 0f)
            {
                playerTimer = PlayerRefreshSeconds;
                Model.PullPlayers();
                if (core != null && core.CurrentState == GameState.PlacingHQ) CountPlacedHQs();
            }
            if (core != null) overlay.Show(core.CurrentState, ShowStartOptions(core), core.CountdownEndTime - NetworkTime.time);
            else startMarkers.Show(markers, null);
        }

        private void CountPlacedHQs()
        {
            placedHQs = 0;
            players = 0;
            foreach (ClientPlayer player in FindObjectsByType<ClientPlayer>())
            {
                players++;
                if (player.hasPlacedHQ) placedHQs++;
            }
        }

        /// <summary>The local player's match ended: the end screen replaces the HUD (the table follows at game over).</summary>
        private void OnMatchEnded(EndResult result)
        {
            matchMenu.Close();
            gift.Close();
            diplomacy.SetOpen(false);
            GameCore core = GameCore.Instance;
            // Defeated while others play on: watch the rest of the match (the server now shows the whole map);
            // the end screen and its table come at game over.
            if (result == EndResult.Defeat && core != null && core.CurrentState == GameState.Playing)
            {
                ShowSpectating(true);
                return;
            }
            float seconds = core != null ? (float)(NetworkTime.time - core.MatchStartTime) : 0f;
            var winners = new List<string>();
            if (result == EndResult.Victory) winners.Add("You");
            endScreen.Show(result, EndScreenController.Line(result, result == EndResult.Victory ? null : winners, seconds));
            if (GameCore.LastMatchStats != null) OnMatchStats(GameCore.LastMatchStats);
        }

        /// <summary>The final statistics arrived (game over): fills the table and names the winners.</summary>
        private void OnMatchStats(MatchResult result)
        {
            if (!endScreen.IsShown && !Spectating) return;
            ShowSpectating(false);
            var winners = new List<string>();
            foreach (int owner in result.Winners) winners.Add(NameOf(owner));
            bool won = System.Array.IndexOf(result.Winners, Model.LocalOwnerId) >= 0;
            EndResult mine = result.Winners.Length == 0 ? EndResult.Draw : won ? EndResult.Victory : EndResult.Defeat;
            endScreen.Show(mine, EndScreenController.Line(mine, winners, result.DurationSeconds));
            endScreen.ShowStats(result, NameOf, PlayerPalette.ColourIndexOf);
        }

        private VisualElement spectatingPanel;

        /// <summary>True while the local player, defeated, watches the rest of the match.</summary>
        public bool Spectating => spectatingPanel != null && spectatingPanel.style.display == DisplayStyle.Flex;

        /// <summary>The approved <c>Panel / Spectating – v0.8</c>, top centre, with Leave match.</summary>
        private void ShowSpectating(bool show)
        {
            if (spectatingPanel == null)
            {
                if (!show) return;
                spectatingPanel = new VisualElement { name = "spectating-panel" };
                spectatingPanel.AddToClassList("panel");
                spectatingPanel.AddToClassList("spectating");
                var title = new Label("Spectating");
                title.AddToClassList("panel__title");
                var body = new VisualElement();
                body.AddToClassList("spectating__body");
                var line = new Label("You were eliminated. You see the whole map: every player's units, buildings and fog.");
                line.AddToClassList("text--secondary");
                var leave = new Button(() => { HostLeftNotice.LeavingOnPurpose = true; GameManager.Instance?.LeaveLobby(); }) { text = "Leave match", name = "spectating-leave" };
                leave.AddToClassList("button");
                leave.AddToClassList("spectating__leave");
                body.Add(line);
                body.Add(leave);
                spectatingPanel.Add(title);
                spectatingPanel.Add(body);
                root.Add(spectatingPanel);
            }
            spectatingPanel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private VisualElement wallReadout;
        private Label wallCost, wallBlocked;
        private float wallCostPerTile = -1f;

        /// <summary>The wall drag's readout beside the pointer: valid tiles × WallCost, and the blocked tiles.</summary>
        private void ShowWallReadout()
        {
            WAR2D.Client.WallDrag drag = UnitCommander.Instance != null ? UnitCommander.Instance.Wall : null;
            bool shown = drag != null && drag.Dragging && drag.Line.Count > 0;
            if (wallReadout == null)
            {
                if (!shown) return;
                wallReadout = new VisualElement { name = "wall-readout", pickingMode = PickingMode.Ignore };
                wallReadout.AddToClassList("wall-readout");
                wallCost = new Label { name = "wall-readout-cost" };
                wallCost.AddToClassList("text--readout");
                wallBlocked = new Label { name = "wall-readout-blocked" };
                wallBlocked.AddToClassList("text--danger");
                wallReadout.Add(wallCost);
                wallReadout.Add(wallBlocked);
                root.Add(wallReadout);
                wallCostPerTile = ConfigLoader.LoadConfig().Siege.WallCost;
            }
            wallReadout.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!shown || root.panel == null) return;
            wallCost.text = $"{drag.ValidCount} tile{(drag.ValidCount == 1 ? "" : "s")} · {drag.ValidCount * wallCostPerTile:0}";
            wallBlocked.text = drag.BlockedCount > 0 ? $"{drag.BlockedCount} blocked" : "";
            wallBlocked.style.display = drag.BlockedCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            Vector2 screen = GameInput.Point.ReadValue<Vector2>();
            Vector2 at = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            wallReadout.style.left = at.x + 20f;
            wallReadout.style.top = at.y + 12f;
        }

        private void ApplyPalette() => Palettes.ApplyTo(root);

        /// <summary>The settings screen.</summary>
        public SettingsController Settings => settings;

        private void ShowDiplomacyButton() => diplomacyButton.EnableInClassList("top-bar__hidden", !Model.DiplomacyEnabled);

        /// <summary>Listens to the local player's alerts once it exists.</summary>
        private void SubscribeAlerts()
        {
            if (alertSource != null || NetworkClient.localPlayer == null) return;
            alertSource = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
            if (alertSource != null) alertSource.AlertsReceived += OnAlerts;
        }

        private void OnAlerts(Alert[] alerts)
        {
            string sound = "alert";
            foreach (Alert alert in alerts)
            {
                string building = alert.Kind == AlertKind.UnderAttack ? NearestOwnBuilding(alert.Tile)
                    : alert.Kind == AlertKind.ConstructionFinished ? OwnBuildingAt(alert.Tile) : null;
                Alerts.Add(alert, AlertFeedModel.Text(alert, NameOf, building), Time.unscaledTime);
                if (alert.Kind == AlertKind.ConstructionFinished) sound = "construction_finished";
                if (alert.Kind == AlertKind.EnemySpotted) sound = "radar_ping";
            }
            // One sound per batch; a Radar contact or a finished building has its own.
            WAR2D.Audio.AudioService.Ui(sound);
        }

        /// <summary>The name of the local player's building whose anchor is the tile, or null.</summary>
        private string OwnBuildingAt(Unity.Mathematics.int2 tile)
        {
            foreach (WAR2D.Net.Replication.ClientBuildings.Entry entry in WAR2D.Net.Replication.ClientBuildings.Current.Entries.Values)
                if (entry.Data.ownerId == Model.LocalOwnerId && Unity.Mathematics.math.all((Unity.Mathematics.int2)Unity.Mathematics.math.round(entry.Data.position) == tile))
                    return AlertFeedModel.BuildingName(entry.Data.buildingType);
            return null;
        }

        private const float NearBuildingTiles = 12f;

        /// <summary>The local player's building nearest a tile (within 12 tiles), as players name it.</summary>
        private string NearestOwnBuilding(Unity.Mathematics.int2 tile)
        {
            float best = NearBuildingTiles * NearBuildingTiles;
            string name = null;
            foreach (WAR2D.Net.Replication.ClientBuildings.Entry entry in WAR2D.Net.Replication.ClientBuildings.Current.Entries.Values)
            {
                if (entry.Data.ownerId != Model.LocalOwnerId) continue;
                float d = Unity.Mathematics.math.distancesq(entry.Data.position, tile);
                if (d > best) continue;
                best = d;
                name = AlertFeedModel.BuildingName(entry.Data.buildingType) ?? name;
            }
            return name;
        }

        /// <summary>A player's public nickname.</summary>
        private string NameOf(int ownerId)
        {
            foreach (PlayerRow row in Model.Players) if (row.OwnerId == ownerId) return row.Nickname;
            return "A player";
        }

        /// <summary>Sends the diplomacy panel's attack switch (the server checks it, and the new row comes back).</summary>
        public void SetAttack(int ownerId, bool on) => GameCore.Instance?.Cmd_SetAttack((uint)ownerId, on);

        /// <summary>Sends the diplomacy panel's share-vision switch.</summary>
        public void SetShareVision(int ownerId, bool on) => GameCore.Instance?.Cmd_SetShareVision((uint)ownerId, on);

        private void OnFilter(UnitType type) => UnitCommander.Instance?.Selection.FilterTo(type);

        private void OnOrder(OrderKind kind)
        {
            UnitCommander.Instance?.Order(kind);
            Selection.Pull();
        }

        private void OnBuild(BuildingType type) => BuildingPlacement.Instance?.BeginPlacement(type);

        private void OnQueue(IReadOnlyList<int> buildingIds, UnitType unit, int delta)
        {
            WorldStateManager wsm = WorldStateManager.Instance;
            if (wsm == null || buildingIds.Count == 0) return;
            wsm.CmdChangeQueues(System.Linq.Enumerable.ToArray(buildingIds), delta);
        }

        /// <summary>A producer's type picked in the selector: the server switches it and clears its queue.</summary>
        private void OnProductionSelected(IReadOnlyList<int> buildingIds, UnitType unit)
        {
            WorldStateManager wsm = WorldStateManager.Instance;
            if (wsm == null || buildingIds.Count == 0) return;
            wsm.CmdSetProduction(System.Linq.Enumerable.ToArray(buildingIds), (byte)unit);
        }

        private void OnWall() => UnitCommander.Instance?.BeginWallDrag();

        private void OnRemove(IReadOnlyList<int> buildingIds)
        {
            if (buildingIds.Count == 0) return;
            WorldStateManager.Instance?.CmdCancelBuildings(System.Linq.Enumerable.ToArray(buildingIds));
            UnitCommander.Instance?.Selection.SelectBuildings(System.Array.Empty<int>(), false);
            Selection.Pull();
        }

        private void OnSquad(int squad)
        {
            UnitCommander.Instance?.Selection.SelectSquad(squad);
            Selection.Pull();
        }
    }
}
