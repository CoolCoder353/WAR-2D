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
        private float playerTimer, selectionTimer;
        private int placedHQs, players;

        /// <summary>The HUD's data.</summary>
        public HudModel Model { get; } = new HudModel();

        /// <summary>The selection, command card and squad bar data.</summary>
        public SelectionModel Selection { get; } = new SelectionModel();

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
            topBar = null;
            selectionPanel = null;
            commandCard = null;
            squadBar = null;
        }

        private void Update()
        {
            Model.PullResources();
            minimap.Update(Time.unscaledDeltaTime);

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
