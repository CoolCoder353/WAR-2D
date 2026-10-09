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
        private MinimapController minimap;
        private AlertFeedController alertFeed;
        private DiplomacyPanelController diplomacy;
        private ClientPlayer alertSource;
        private Button diplomacyButton;
        private float playerTimer, selectionTimer;
        private int placedHQs, players;

        /// <summary>The HUD's data.</summary>
        public HudModel Model { get; } = new HudModel();

        /// <summary>The selection, command card and squad bar data.</summary>
        public SelectionModel Selection { get; } = new SelectionModel();

        /// <summary>The alert feed's data.</summary>
        public AlertFeedModel Alerts { get; private set; }

        /// <summary>The diplomacy panel's controller.</summary>
        public DiplomacyPanelController Diplomacy => diplomacy;

        /// <summary>The minimap's controller.</summary>
        public MinimapController Minimap => minimap;

        /// <summary>The top bar's controller (its buttons are wired by later HUD sections).</summary>
        public TopBarController TopBar => topBar;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            // Each section's template fills the screen; only its panels take the pointer.
            root.Query<TemplateContainer>().ForEach(t => t.pickingMode = PickingMode.Ignore);

            GameConfigData config = ConfigLoader.LoadConfig();
            var costs = new Dictionary<BuildingType, float>
            {
                [BuildingType.Miner] = config.GetBuilding(BuildingType.Miner).UpfrontCost,
                [BuildingType.SmallUnitSpawner] = config.GetBuilding(BuildingType.SmallUnitSpawner).UpfrontCost,
            };

            topBar = new TopBarController(root, Model);
            selectionPanel = new SelectionPanelController(root, Selection);
            commandCard = new CommandCardController(root, Selection, Model, costs, config.GetBuilding(BuildingType.SmallUnitSpawner).SpawnRate);
            squadBar = new SquadBarController(root, Selection);
            overlay = new HudOverlayController(root);
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

            Selection.FilterRequested += OnFilter;
            commandCard.OrderClicked += OnOrder;
            commandCard.BuildClicked += OnBuild;
            commandCard.QueueClicked += OnQueue;
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
            Model.DiplomacyChanged -= ShowDiplomacyButton;
            if (alertSource != null) alertSource.AlertsReceived -= OnAlerts;
            alertSource = null;
            topBar = null;
            selectionPanel = null;
            commandCard = null;
            squadBar = null;
        }

        private void Update()
        {
            Model.PullResources();
            minimap.Update(Time.unscaledDeltaTime);
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
            if (core != null) overlay.Show(core.CurrentState, placedHQs, players, core.CountdownEndTime - NetworkTime.time);
        }

        private void CountPlacedHQs()
        {
            placedHQs = 0;
            players = 0;
            foreach (ClientPlayer player in FindObjectsByType<ClientPlayer>(FindObjectsSortMode.None))
            {
                players++;
                if (player.hasPlacedHQ) placedHQs++;
            }
        }

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
            foreach (Alert alert in alerts)
                Alerts.Add(alert, AlertFeedModel.Text(alert, NameOf, alert.Kind == AlertKind.UnderAttack ? NearestOwnBuilding(alert.Tile) : null), Time.unscaledTime);
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

        private void OnQueue(int buildingId, int delta)
        {
            WorldStateManager wsm = WorldStateManager.Instance;
            if (wsm == null || buildingId == 0) return;
            if (delta > 0) wsm.BuildingClicked(buildingId);
            else wsm.CmdDequeueUnit(buildingId);
        }

        private void OnSquad(int squad)
        {
            UnitCommander.Instance?.Selection.SelectSquad(squad);
            Selection.Pull();
        }
    }
}
