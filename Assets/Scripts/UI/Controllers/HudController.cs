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
        private ReconnectBannerController reconnect;
        private SelectionPanelController selectionPanel;
        private CommandCardController commandCard;
        private SquadBarController squadBar;
        private HudOverlayController overlay;
        private StartMarkersController startMarkers;
        private readonly List<StartMarker> markers = new List<StartMarker>();
        private readonly List<string> teammatesPicked = new List<string>();
        private MinimapController minimap;
        private AlertFeedController alertFeed;
        private OrderTagsController orderTags;
        private DiplomacyPanelController diplomacy;
        private ClientPlayer alertSource;
        private Button diplomacyButton;
        private InMatchMenuController matchMenu;
        private ManualController manual;
        private bool manualFromMenu;

        /// <summary>The Field manual (tests).</summary>
        public ManualController Manual => manual;
        private SettingsController settings;
        private GiftDialogController gift;
        private EndScreenController endScreen;
        private HoverCard hover;
        private UnitCommander noticeSource;
        private VisualElement root;
        private VisualElement hoverRoot; // the root the hover sounds are registered on (OnEnable can run again)
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
            if (hoverRoot != root) { Widgets.HoverSounds(root); hoverRoot = root; }
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
                if (u.IsBomber) return $"Blast {u.BlastRadius:0.#}, health {u.Health:0}";
                if (u.Damage <= 0) return null;
                string role = $"Range {u.Range:0.#}";
                if (u.Splash > 0f) role += $", splash {u.Splash:0.#}";
                if (u.MinRange > 0f) role += $", can't fire closer than {u.MinRange:0.#}";
                return role + $", health {u.Health:0}";
            }

            // The build buttons' hover card (v0.15, Figma v0.15 QoL redesign, frame 3): role, then Cost, Upkeep, Size and reach.
            HoverCardContent BuildCard(BuildingType type)
            {
                string role = type switch
                {
                    BuildingType.None => "Press and drag on the map to lay a line",
                    BuildingType.Miner => "Income: mines the gem tile it faces (R turns it)",
                    BuildingType.Barracks => "Makes Scouts, Tanks, Flamethrowers",
                    BuildingType.HeavyFactory => "Makes Heavies, Artillery, Snipers",
                    BuildingType.SiegeWorkshop => "Makes Diggers, Bombers",
                    BuildingType.Mine => "A hidden trap, walkable",
                    BuildingType.Radar => "Sees over walls",
                    BuildingType.GunTurret => "Fires ten shots a second. Shoots Bombers first",
                    BuildingType.Jammer => "Hides an area from enemies",
                    _ => null,
                };
                var card = new HoverCardContent(null, null, role);
                if (type == BuildingType.None) return card.Pair("Size", "1 tile each");
                if (!config.Buildings.TryGetValue(type, out BuildingConfig b)) return card;
                if (b.RunningCost > 0f) card.Pair("Upkeep", $"{b.RunningCost:0.#}/s");
                card.Pair("Size", $"{b.Width}×{b.Height}");
                // v0.20 (Figma B2): the build time with one Builder, which the Gun turret's BuildSeconds makes long.
                if (type != BuildingType.Base) card.Pair("Build", $"{b.Health / Mathf.Max(0.01f, b.BuildRate(config.Siege.BuildPower)):0} s");
                if (type == BuildingType.GunTurret) card.Pair("Range", $"{b.Range:0.#}");
                else if (type == BuildingType.Jammer) card.Pair("Jams", $"{config.Siege.JamRadius:0.#} tiles");
                else if (type == BuildingType.Radar) card.Pair("Sight", $"{b.Sight:0.#}");
                return card;
            }

            // v0.15: the one shared hover card (command-tooltip) every button's card goes through.
            hover = new HoverCard(root);
            topBar = new TopBarController(root, Model, hover);
            selectionPanel = new SelectionPanelController(root, Selection, hover);
            commandCard = new CommandCardController(root, Selection, Model, costs, unitCosts, config.Siege.WallCost, config.Siege.DemolishRefund,
                BuildSeconds, RoleOf, switchClearsQueue: true, BuildCard, unit => HoverText.Unit(unit, config.GetUnit(unit), Model.Resources, config),
                hover, config.Siege.RepairCostPerHealth, config.Simulation.MaxUnitsPerPlayer);
            squadBar = new SquadBarController(root, Selection, hover);
            overlay = new HudOverlayController(root);
            startMarkers = new StartMarkersController(root);
            overlay.OptionClicked += FocusOption;
            startMarkers.Clicked += CentreCamera;
            minimap = new MinimapController(root);
            Alerts = new AlertFeedModel(config.Alerts.ShowSeconds);
            alertFeed = new AlertFeedController(root, Alerts, hover);
            reconnect = new ReconnectBannerController(root, topBar, Alerts); // v0.15 item 19: drop banners, away tags, alerts
            Model.Economy.LowFunds += OnLowFunds;
            // The selection panel and squad bar sit right of the minimap and follow it when its size changes (S, M, L).
            root.Q("minimap")?.RegisterCallback<GeometryChangedEvent>(FollowMinimap);
            root.Q("selection-panel")?.RegisterCallback<GeometryChangedEvent>(FollowSelectionPanel);
            orderTags = new OrderTagsController(root);
            diplomacy = new DiplomacyPanelController(root, Model);
            diplomacyButton = root.Q<Button>("diplomacy-button");
            Model.DiplomacyChanged += ShowDiplomacyButton;
            ShowDiplomacyButton();

            Alerts.PingRequested += minimap.Ping;
            worldMarkers = new WorldMarkersController(root, minimap);
            Alerts.FocusRequested += tile =>
            {
                Camera cam = Camera.main;
                if (cam != null && cam.TryGetComponent(out Character.Character_Controler controller)) controller.CentreOn(new Unity.Mathematics.float2(tile.x + 0.5f, tile.y + 0.5f));
            };
            topBar.DiplomacyClicked += diplomacy.Toggle;
            topBar.HqClicked += () => { UnitCommander.Instance?.JumpToHq(); Selection.Pull(); };
            topBar.BuildersClicked += all => { UnitCommander.Instance?.SelectIdleBuilders(all); Selection.Pull(); };
            topBar.ShowBuilderClicked += () =>
            {
                if (UnitCommander.Instance == null) return;
                (int position, int count) = UnitCommander.Instance.ShowNextBuilder();
                topBar.ShowBuilderPosition(position, count);
            };
            topBar.ArmyClicked += () => { UnitCommander.Instance?.SelectArmy(); Selection.Pull(); };
            topBar.DecayingClicked += SelectDecaying;
            diplomacy.AttackToggled += SetAttack;
            diplomacy.ShareToggled += SetShareVision;

            matchMenu = new InMatchMenuController(root);
            settings = new SettingsController(root);
            settings.Closed += matchMenu.Open;
            settings.Closed += commandCard.ShowKeys; // a rebind shows on the command buttons at once
            matchMenu.SettingsClicked += () => { matchMenu.Close(); settings.Open(); SettingsRequested?.Invoke(); };
            matchMenu.SurrenderConfirmed += () => GameCore.Instance?.Cmd_Surrender();
            matchMenu.LeaveClicked += () => { HostLeftNotice.LeavingOnPurpose = true; GameManager.Instance?.LeaveLobby(); };
            topBar.MenuClicked += matchMenu.Toggle;
            manual = new ManualController(root);
            manual.SettingsRequested += () => { manualFromMenu = false; settings.Open(); SettingsRequested?.Invoke(); };
            manual.Closed += () => { if (manualFromMenu) matchMenu.Open(); manualFromMenu = false; };
            matchMenu.ManualClicked += () => { matchMenu.Close(); manualFromMenu = true; manual.Open(); };

            gift = new GiftDialogController(root, Model, config.Gifting.CooldownSeconds);
            gift.GiftRequested += (owner, amount) => GameCore.Instance?.Cmd_GiftResources((uint)owner, amount);
            topBar.GiftClicked += () => { diplomacy.SetOpen(false); gift.Toggle(); };
            topBar.DiplomacyClicked += gift.Close;

            endScreen = new EndScreenController(root);
            endScreen.MainMenuClicked += () => { HostLeftNotice.LeavingOnPurpose = true; GameManager.Instance?.LeaveLobby(); };
            endScreen.QuitClicked += () => GameManager.Instance?.QuitGame();
            endScreen.RematchClicked += () => GameCore.Instance?.Cmd_Rematch();
            ClientPlayer.MatchEnded += OnMatchEnded;
            GameCore.MatchStatsReceived += OnMatchStats;
            GameCore.MatchSummaryReceived += ShowSummary;
            GameCore.RematchStarting += endScreen.ShowReturning;
            GameCore.ClearMatchStats(); // a new match

            Selection.FilterRequested += OnFilter;
            Selection.EverywhereRequested += OnEverywhere;
            commandCard.RallyAllClicked += () => UnitCommander.Instance?.ArmRallyAll();
            commandCard.OrderClicked += OnOrder;
            commandCard.RoamClicked += () => { UnitCommander.Instance?.StartRoaming(); Selection.Pull(); };
            commandCard.BuildClicked += OnBuild;
            commandCard.QueueClicked += OnQueue;
            commandCard.ProductionSelected += OnProductionSelected;
            commandCard.WallClicked += OnWall;
            commandCard.RemoveConfirmed += OnRemove;
            commandCard.RemoveWallsConfirmed += () => { UnitCommander.Instance?.RemoveSelectedWalls(); Selection.Pull(); };
            squadBar.SquadClicked += OnSquad;
        }

        private void OnDisable()
        {
            DialogOpen = false;
            Selection.FilterRequested -= OnFilter;
            Selection.EverywhereRequested -= OnEverywhere;
            Model.Economy.LowFunds -= OnLowFunds;
            if (noticeSource != null) noticeSource.Notice -= OnNotice;
            noticeSource = null;
            hover?.Hide();
            topBar?.Dispose();
            selectionPanel?.Dispose();
            commandCard?.Dispose();
            squadBar?.Dispose();
            minimap?.Dispose();
            minimap = null;
            alertFeed?.Dispose();
            reconnect?.Dispose();
            reconnect = null;
            diplomacy?.Dispose();
            gift?.Dispose();
            settings?.Dispose();
            Palettes.Changed -= ApplyPalette;
            ClientPlayer.MatchEnded -= OnMatchEnded;
            GameCore.MatchStatsReceived -= OnMatchStats;
            GameCore.MatchSummaryReceived -= ShowSummary;
            GameCore.RematchStarting -= endScreen.ShowReturning;
            Model.DiplomacyChanged -= ShowDiplomacyButton;
            if (alertSource != null) alertSource.AlertsReceived -= OnAlerts;
            alertSource = null;
            topBar = null;
            selectionPanel = null;
            commandCard = null;
            squadBar = null;
        }

        private bool cameraCentred;
        private readonly List<MinimapStart> starts = new List<MinimapStart>();

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
            // The pick needs the whole ring in view (playtest 2026-11-04: 1:1 on a small window was all HQ).
            if (core.CurrentState == GameState.PlacingHQ && Camera.main != null && Camera.main.TryGetComponent(out Character.Character_Controler controller))
                controller.ZoomOutToShow(WAR2D.World.MapGenerator.HqClearRadius + HqPickMargin);
        }

        private bool ownHqSeen;

        /// <summary>
        /// When the own HQ first appears, brings it into view. A Random or timed-out pick places it at any of the player's options,
        /// while the camera is still on the one it opened on, with nothing but fog to see there.
        /// </summary>
        private void ShowOwnHqOnce()
        {
            if (ownHqSeen || Model.LocalOwnerId == 0) return;
            if (!WAR2D.Net.Replication.ClientBuildings.Current.TryFindOwn(Model.LocalOwnerId, BuildingType.Base, out WAR2D.Net.Replication.ClientBuildings.Entry hq)) return;
            ownHqSeen = true;
            Camera cam = Camera.main;
            if (cam == null) return;
            // The anchor is the footprint's middle tile, not its corner.
            Unity.Mathematics.float2 centre = Footprint.VisualCenter((Unity.Mathematics.int2)Unity.Mathematics.math.round(hq.Data.position), WorldStateManager.GetBuildingSize(BuildingType.Base));
            Vector3 at = cam.transform.position;
            if (!CameraZoom.InView(new Unity.Mathematics.float2(at.x, at.y), cam.orthographicSize, cam.aspect, centre)) CentreCamera(new Vector2(centre.x, centre.y));
        }

        /// <summary>Tiles shown past the start ring when the HQ pick opens.</summary>
        private const float HqPickMargin = 2f;

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
                minimap?.ShowStarts(null);
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
            int ownMarkers = markers.Count;
            foreach (var teammate in me.TeammateStarts)
            {
                string name = NameOf(teammate.Key);
                Color32 colour = PlayerPalette.Of(ClientPlayer.TryFind(teammate.Key, out ClientPlayer other) ? other.colourIndex : -1);
                foreach (int site in teammate.Value.options)
                {
                    if (site < 0 || site >= sites.Length) continue;
                    bool picked = site == teammate.Value.pick;
                    markers.Add(new StartMarker { Centre = new Vector2(sites[site].x + 0.5f, sites[site].y + 0.5f), Radius = radius, Colour = colour,
                        OnScreen = picked ? $"{name}'s start" : $"{name} · option", OffScreen = name });
                }
            }
            startMarkers.Show(markers, cam);
            starts.Clear();
            for (int i = 0; i < markers.Count; i++)
            {
                StartMarker marker = markers[i];
                bool own = i < ownMarkers; // the player's own options come first, numbered as their Option buttons
                starts.Add(new MinimapStart { Centre = new Unity.Mathematics.float2(marker.Centre.x, marker.Centre.y), Colour = marker.Colour, Own = own, Label = own ? marker.OnScreen : null });
            }
            minimap?.ShowStarts(starts);
            return prompt;
        }

        private bool menuPauseSent;

        /// <summary>
        /// True while the in-match menu, Settings, the Field manual or the gift dialog is open: hotkeys, orders and placement wait,
        /// as they do for the card keys. The diplomacy panel isn't here: it is not modal, so the match can be played around it.
        /// </summary>
        public static bool DialogOpen { get; private set; }

        /// <summary>
        /// Asks the server to pause while the in-match menu (or Settings opened from it) is open, and to resume when it
        /// closes; the server accepts only when this is the one human connected. The menu shows the server's answer.
        /// </summary>
        private void SyncMenuPause()
        {
            GameCore core = GameCore.Instance;
            bool want = (matchMenu.IsOpen || settings.IsOpen || manual.IsOpen) && !endScreen.IsShown;
            if (core != null && NetworkClient.active && want != menuPauseSent)
            {
                menuPauseSent = want;
                core.CmdSetMenuPause(want);
            }
            matchMenu.ShowPaused(core != null && core.IsMenuPaused && want);
        }

        // Profiler markers (nearly free in a release player): the HUD's parts that grow with the units known.
        private static readonly Unity.Profiling.ProfilerMarker MinimapMarker = new Unity.Profiling.ProfilerMarker("Hud.Minimap"),
            MarkersMarker = new Unity.Profiling.ProfilerMarker("Hud.WorldMarkers"), TagsMarker = new Unity.Profiling.ProfilerMarker("Hud.OrderTags"),
            SelectionMarker = new Unity.Profiling.ProfilerMarker("Hud.Selection"), QuickMarker = new Unity.Profiling.ProfilerMarker("Hud.Quick");

        private void Update()
        {
            CentreOnOwnClearing();
            ShowOwnHqOnce();
            Model.PullResources();
            if (GameInput.FieldManual.WasPressedThisFrame() && !endScreen.IsShown && !settings.IsOpen)
            {
                if (manual.IsOpen) manual.Close();
                else { matchMenu.Close(); manual.Open(ManualPageOfSelection()); }
            }
            if (GameInput.Menu.WasPressedThisFrame() && !endScreen.IsShown)
            {
                hover.Hide();
                if (manual.IsOpen) manual.Close();
                else if (commandCard.CloseConfirm()) { }
                else if (minimap.FiltersOpen) minimap.SetFiltersOpen(false);
                else if (UnitCommander.Instance != null && UnitCommander.Instance.CancelWallDrag()) { }
                else if (UnitCommander.Instance != null && UnitCommander.Instance.CancelDigDrag()) { }
                else if (settings.IsOpen) settings.Close();
                else if (gift.IsOpen) gift.Close();
                else if (diplomacy.IsOpen) diplomacy.SetOpen(false);
                else matchMenu.Toggle();
            }
            DialogOpen = manual.IsOpen || matchMenu.IsOpen || settings.IsOpen || gift.IsOpen;
            if (GameInput.Cancel.WasPressedThisFrame() && !manual.IsOpen && !matchMenu.IsOpen && !settings.IsOpen && !gift.IsOpen && !diplomacy.IsOpen) commandCard.RequestRemove();
            // Alerts leave after Alerts.ShowSeconds, but not from under the pointer (playtest 2026-10-30).
            if (alertFeed != null && alertFeed.Hovered) Alerts.Hold(Time.unscaledDeltaTime);
            else Alerts.Expire(Time.unscaledTime);
            // Next alert (Space, rebindable): the newest unvisited alert with a place, then the one before it.
            if (GameInput.NextAlert.WasPressedThisFrame() && !manual.IsOpen && !matchMenu.IsOpen && !settings.IsOpen && !gift.IsOpen && !endScreen.IsShown) Alerts.FocusNextUnvisited();
            // v0.15.1 card-slot keys (Figma H1–H2): build and production, not while a menu or dialog has the keyboard.
            if (!manual.IsOpen && !matchMenu.IsOpen && !settings.IsOpen && !gift.IsOpen && !diplomacy.IsOpen && !endScreen.IsShown && !Spectating)
            {
                string why = commandCard.HandleHotkeys();
                if (why != null)
                {
                    WAR2D.Audio.AudioService.Ui("ui_error"); // the key's button is greyed out
                    OnNotice(why);
                }
            }
            SubscribeNotices();
            ShowCursorNotice();
            matchMenu.SetCanSurrender(root, GameCore.Instance != null && GameCore.Instance.CurrentState == GameState.Playing && !endScreen.IsShown);
            SyncMenuPause();
            if (endScreen.IsShown)
            {
                var (replayText, replayFraction) = EndScreenController.ReplayState(NetworkServer.active, WorldStateManager.Instance != null ? WorldStateManager.Instance.Recorder : null);
                endScreen.ShowReplay(replayText, replayFraction);
                bool owner = NetworkClient.localPlayer != null && NetworkClient.localPlayer.TryGetComponent(out ClientPlayer local) && local.isServerOwner;
                endScreen.ShowRematch(owner, NetworkServer.active && WorldStateManager.Instance != null && WorldStateManager.Instance.Recorder != null && !WorldStateManager.Instance.Recorder.Finished);
            }
            using (MinimapMarker.Auto()) minimap.Update(Time.unscaledDeltaTime);
            if (Spectating) spectator.Tick(Time.unscaledDeltaTime);
            using (MarkersMarker.Auto()) worldMarkers.Update(!Spectating && !endScreen.IsShown && !matchMenu.IsOpen && !settings.IsOpen && !manual.IsOpen);
            using (TagsMarker.Auto()) orderTags.Update(UnitCommander.Instance != null ? UnitCommander.Instance.Feedback : null, Camera.main, GameInput.Point.ReadValue<Vector2>());
            ShowWallReadout();
            Model.PullDiplomacy();
            SubscribeAlerts();

            selectionTimer -= Time.unscaledDeltaTime;
            if (selectionTimer <= 0f)
            {
                selectionTimer = SelectionRefreshSeconds;
                using (SelectionMarker.Auto()) Selection.Pull();
            }

            GameCore core = GameCore.Instance;
            playerTimer -= Time.unscaledDeltaTime;
            if (playerTimer <= 0f)
            {
                playerTimer = PlayerRefreshSeconds;
                Model.PullPlayers();
                if (core != null && core.CurrentState == GameState.PlacingHQ) CountPlacedHQs();
                using (QuickMarker.Auto()) PullQuick();
            }
            if (core != null) overlay.Show(core.CurrentState, ShowStartOptions(core), core.CountdownEndTime - NetworkTime.time);
            else startMarkers.Show(markers, null);
        }

        /// <summary>
        /// The v0.15 top bar refresh (4 Hz): the quick-select counts, the net readout (its five-second average moves between
        /// resource updates), the decaying count, the HQ button (hidden without an own HQ, so when spectating or in a replay)
        /// and Rally all's armed state.
        /// </summary>
        private void PullQuick()
        {
            UnitCommander commander = UnitCommander.Instance;
            int owner = Model.LocalOwnerId;
            topBar.ShowQuick(commander != null && owner != 0 ? commander.Selection.CountQuick(owner) : default);
            int decaying = 0;
            foreach (WAR2D.Net.Replication.ClientBuildings.Entry entry in WAR2D.Net.Replication.ClientBuildings.Current.Entries.Values)
                if (entry.Data.ownerId == owner && WAR2D.Client.DefenceView.Unpaid(entry)) decaying++;
            topBar.SetDecaying(decaying);
            topBar.ShowNet();
            topBar.ShowHq(owner != 0 && !Spectating && WAR2D.Net.Replication.ClientBuildings.Current.TryFindOwn(owner, BuildingType.Base, out _));
            commandCard.ShowRallyAllArmed(commander != null && commander.RallyAllArmed);
        }

        /// <summary>"· select" in the Decaying state: selects the own buildings the client knows are unpaid.</summary>
        private void SelectDecaying()
        {
            UnitCommander commander = UnitCommander.Instance;
            if (commander == null) return;
            var ids = new List<int>();
            foreach (KeyValuePair<int, WAR2D.Net.Replication.ClientBuildings.Entry> pair in WAR2D.Net.Replication.ClientBuildings.Current.Entries)
                if (pair.Value.Data.ownerId == Model.LocalOwnerId && WAR2D.Client.DefenceView.Unpaid(pair.Value)) ids.Add(pair.Key);
            if (ids.Count == 0) return;
            commander.Selection.ClearWalls();
            commander.Selection.SelectBuildings(ids, false);
            Selection.Pull();
        }

        /// <summary>The low-funds warning (state C): one alert row per drop, re-armed once net is positive again.</summary>
        private void OnLowFunds()
        {
            string text = $"Resources will run out in {EconomyReadout.Clock(Model.Economy.SecondsUntilEmpty)}";
            Alerts.Add(new Alert { Kind = AlertKind.ResourcesLow }, text, Time.unscaledTime);
            if (SettingsStore.Current.SoundsAlert(AlertKind.ResourcesLow)) WAR2D.Audio.AudioService.Ui("alert");
        }

        /// <summary>Keeps the selection panel, squad bar and its hint just right of the minimap, whatever its size.</summary>
        private void FollowMinimap(GeometryChangedEvent e)
        {
            if (!(e.target is VisualElement map) || float.IsNaN(map.layout.xMax)) return;
            float left = map.layout.xMax + 16f;
            foreach (string name in new[] { "selection-panel", "squad-bar", "squad-hint" })
            {
                VisualElement element = root.Q(name);
                if (element != null) element.style.left = left;
            }
        }

        /// <summary>Lowest the squad bar sits (its USS bottom), and the gap kept above the selection panel and to the hint.</summary>
        private const float SquadBarBottom = 142f, SquadBarGap = 8f, SquadHintAbove = 44f;

        /// <summary>
        /// Keeps the squad bar and its hint above the selection panel when the panel grows past them (playtest 2026-11-02: a
        /// tall selection, many types or the upkeep line, covered the squad bar).
        /// </summary>
        private void FollowSelectionPanel(GeometryChangedEvent e)
        {
            if (!(e.target is VisualElement panel)) return;
            float top = panel.resolvedStyle.display == DisplayStyle.None || float.IsNaN(panel.layout.height) ? 0f
                : panel.resolvedStyle.bottom + panel.layout.height + SquadBarGap;
            float bottom = Mathf.Max(SquadBarBottom, top);
            VisualElement bar = root.Q("squad-bar"), hint = root.Q("squad-hint");
            if (bar != null) bar.style.bottom = bottom;
            if (hint != null) hint.style.bottom = bottom + SquadHintAbove;
        }

        private Label cursorNotice;
        private string noticeText;
        private float noticeUntil;
        private const float NoticeSeconds = 1f;

        /// <summary>Listens to the commander's "No idle Builders" notices once it exists.</summary>
        private void SubscribeNotices()
        {
            UnitCommander commander = UnitCommander.Instance;
            if (commander == noticeSource) return;
            if (noticeSource != null) noticeSource.Notice -= OnNotice;
            noticeSource = commander;
            if (noticeSource != null) noticeSource.Notice += OnNotice;
        }

        private void OnNotice(string text)
        {
            noticeText = text;
            noticeUntil = Time.unscaledTime + NoticeSeconds;
        }

        /// <summary>
        /// The HUD's cursor tag (frames 1 and 8): a shortcut's notice for a second ("No idle Builders"), or "Rally all producers (4)"
        /// while Rally all waits for its click.
        /// </summary>
        private void ShowCursorNotice()
        {
            UnitCommander commander = UnitCommander.Instance;
            string text = Time.unscaledTime < noticeUntil ? noticeText : null;
            if (text == null && commander != null && commander.RallyAllArmed) text = $"{(commander.AttackRallyArmed ? "Attack rally" : "Rally")} all producers ({commander.OwnProducerIds().Count})";
            else if (text == null && commander != null && commander.AttackRallyArmed) text = commander.NothingSelected ? "Attack rally: Rally all, or Ctrl + click" : "Attack rally: click the map";
            if (cursorNotice == null)
            {
                if (text == null) return;
                cursorNotice = new Label { name = "hud-cursor-tag", pickingMode = PickingMode.Ignore };
                cursorNotice.AddToClassList("order-tag");
                cursorNotice.AddToClassList("cursor-notice");
                root.Add(cursorNotice);
            }
            cursorNotice.style.display = text != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (text == null || root.panel == null) return;
            cursorNotice.text = text;
            Vector2 screen = GameInput.Point.ReadValue<Vector2>();
            Vector2 at = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            cursorNotice.style.left = at.x + 18f;
            cursorNotice.style.top = at.y + 14f;
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
            endScreen.ShowStats(result, NameOf, ColourIndexOf, owner => ResultTagOf(owner, result));
            if (GameCore.LastMatchSummary.HasValue) ShowSummary(GameCore.LastMatchSummary.Value);
        }

        /// <summary>The page of what is selected: the first unit's type, else the first building's; null for the last page opened.</summary>
        private static string ManualPageOfSelection()
        {
            UnitCommander commander = UnitCommander.Instance;
            if (commander == null) return null;
            int first = commander.Selection.First;
            if (first != 0 && WAR2D.Client.ClientWorld.Instance != null && WAR2D.Client.ClientWorld.Instance.TryGet(first, out WAR2D.Client.ClientUnitView unit)) return ManualModel.PageOf(unit.Type);
            foreach (int id in commander.Selection.SelectedBuildings)
                if (commander.KnownBuildings.Entries.TryGetValue(id, out WAR2D.Net.Replication.ClientBuildings.Entry e)) return ManualModel.PageOf(e.Data.buildingType);
            return null;
        }

        private void ShowSummary(SummaryData summary)
        {
            if (!endScreen.IsShown) return; // the statistics show the screen; they call this again
            endScreen.ShowSummary(summary, NameOf, owner => PlayerPalette.Of(ColourIndexOf(owner)), Model.LocalOwnerId);
        }

        private SpectatorController spectator;
        private WorldMarkersController worldMarkers;

        /// <summary>True while the local player, defeated, watches the rest of the match.</summary>
        public bool Spectating => spectator != null && spectator.Panel.style.display == DisplayStyle.Flex;

        /// <summary>The replay viewer's layout for a live match (Figma R1; <see cref="SpectatorController"/>).</summary>
        private void ShowSpectating(bool show)
        {
            if (spectator == null)
            {
                if (!show) return;
                spectator = new SpectatorController(root, () => Model.Players, AiTagOf, () => { HostLeftNotice.LeavingOnPurpose = true; GameManager.Instance?.LeaveLobby(); });
            }
            spectator.Panel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            // The panel replaces the controls: a player who is out has nothing to order, gift or negotiate with.
            root.Q("gift-button")?.EnableInClassList("top-bar__hidden", show);
            ShowDiplomacyButton();
            foreach (string layer in SpectatorHiddenLayers)
            {
                VisualElement element = root.Q(layer);
                if (element != null) element.style.display = show ? DisplayStyle.None : new StyleEnum<DisplayStyle>(StyleKeyword.Null);
            }
        }

        private static readonly string[] SpectatorHiddenLayers = { "command-card-instance", "squad-bar-instance", "selection-panel-instance", "top-bar-instance" };

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

        private void ShowDiplomacyButton() => diplomacyButton.EnableInClassList("top-bar__hidden", !Model.DiplomacyEnabled || Spectating);

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
                if (alert.Kind == AlertKind.Ping)
                {
                    worldMarkers.AddPing(alert.Tile, PlayerPalette.Of(ColourIndexOf(alert.OtherOwnerId)), NameOf(alert.OtherOwnerId));
                    sound = "radar_ping";
                    if (alert.OtherOwnerId == Model.LocalOwnerId) continue; // your own ping shows without a row
                }
                string building = alert.Kind == AlertKind.UnderAttack ? NearestOwnBuilding(alert.Tile)
                    : alert.Kind == AlertKind.ConstructionFinished ? OwnBuildingAt(alert.Tile) : null;
                // An AI named by the alert gets its tag after its callsign (Figma 95:563); Under attack never names anyone.
                bool names = AlertFeedModel.NamesAPlayer(alert.Kind);
                Alerts.Add(alert, AlertFeedModel.Text(alert, NameOf, building), Time.unscaledTime,
                    names ? NameOf(alert.OtherOwnerId) : null, names ? AiTagOf(alert.OtherOwnerId) : null);
                if (alert.Kind == AlertKind.ConstructionFinished) sound = building == AlertFeedModel.BuildingName(BuildingType.Jammer) ? "jammer_online" : "construction_finished";
                if (alert.Kind == AlertKind.EnemySpotted) sound = "radar_ping";
            }
            // One sound per batch; a Radar contact or a finished building has its own (a Jammer coming online hums, v0.13).
            // Settings → Gameplay → Alerts: a batch whose kinds are all muted plays nothing.
            bool audible = false;
            foreach (Alert alert in alerts) audible |= SettingsStore.Current.SoundsAlert(alert.Kind);
            if (audible) WAR2D.Audio.AudioService.Ui(sound);
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

        /// <summary>A player's AI tag ("AI · Hard"), or null for a human or an unknown owner.</summary>
        private string AiTagOf(int ownerId)
        {
            foreach (PlayerRow row in Model.Players) if (row.OwnerId == ownerId) return row.AiTag;
            return null;
        }

        /// <summary>An AI's tag on the end screen: difficulty and the style it played, "(random)" when the host left it on Random.</summary>
        private string ResultTagOf(int ownerId, MatchResult result)
        {
            foreach (PlayerRow row in Model.Players)
                if (row.OwnerId == ownerId)
                    return row.IsAi ? PlayerNameLabel.ResultTagText(row.Difficulty, row.PickedStyle, result.PlayedStyle(ownerId)) : null;
            return null;
        }

        /// <summary>A player's public nickname.</summary>
        /// <summary>A player's colour as last seen: one who left has no object for <see cref="PlayerPalette"/> to read it from.</summary>
        private int ColourIndexOf(int ownerId)
        {
            foreach (PlayerRow row in Model.Players) if (row.OwnerId == ownerId) return row.ColourIndex;
            return PlayerPalette.ColourIndexOf(ownerId);
        }

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

        /// <summary>A type chip double-clicked: every own unit of the type on the map (the camera stays put).</summary>
        private void OnEverywhere(UnitType type)
        {
            UnitCommander.Instance?.Selection.SelectTypeEverywhere(type, Model.LocalOwnerId);
            Selection.Pull();
        }

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

        /// <summary>
        /// A squad slot clicked: selects its squad, or its building group (v0.15: the producers and their card; clicked again with
        /// them selected, the camera centres on the first).
        /// </summary>
        private void OnSquad(int squad)
        {
            WAR2D.Client.Selection selection = UnitCommander.Instance != null ? UnitCommander.Instance.Selection : null;
            if (selection == null) return;
            if (selection.IsBuildingGroup(squad))
            {
                selection.ClearWalls();
                if (selection.IsExactlyBuildingGroup(squad) &&
                    WAR2D.Net.Replication.ClientBuildings.Current.Entries.TryGetValue(selection.FirstOfBuildingGroup(squad), out WAR2D.Net.Replication.ClientBuildings.Entry first))
                    { Unity.Mathematics.float2 c = Footprint.VisualCenter((Unity.Mathematics.int2)Unity.Mathematics.math.round(first.Data.position), WorldStateManager.GetBuildingSize(first.Data.buildingType)); CentreCamera(new Vector2(c.x, c.y)); }
                else selection.SelectBuildingGroup(squad);
            }
            else selection.SelectSquad(squad);
            Selection.Pull();
        }
    }
}
