using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Client;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;

public class UnitCommander : NetworkBehaviour
{
    public static UnitCommander Instance { get; private set; }

    public int2 visualAdditionalRange = new int2(5, 5);

    private int2 startcorner;
    private int2 endcorner;
    private bool selecting;
    /// <summary>
    /// An order waiting for its target: set by the attack-move key or the command card's Move and
    /// Attack-move buttons; the next left or right click sends it. Null when nothing is armed.
    /// </summary>
    public OrderKind? ArmedOrder { get; private set; }

    private int2 lastSentCorner1 = new int2(int.MinValue, int.MinValue);
    private int2 lastSentCorner2 = new int2(int.MinValue, int.MinValue);
    private float viewSendTimer;

    public GameObject selectionBox;

    private ClientPlayer localPlayer;

    /// <summary>The local player's selection and squads.</summary>
    public WAR2D.Client.Selection Selection { get; } = new WAR2D.Client.Selection();
    private Vector3 dragStart;


    public Dictionary<int, GameObject> buildingGameObjects = new Dictionary<int, GameObject>();


    [ClientCallback]
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;

            //Start the selection box as inactive
            selectionBox.SetActive(false);
            if (GetComponent<WAR2D.Client.ClientWorld>() == null) gameObject.AddComponent<WAR2D.Client.ClientWorld>();

            localPlayer = NetworkClient.connection.identity.GetComponent<ClientPlayer>();
            SubscribeBuildings();
        }
        else
        {
            Destroy(this);
        }
    }

    [ClientCallback]
    private void OnDestroy()
    {
        if (Instance == this)
        {
            UnsubscribeBuildings();
            wallDrag?.Dispose();
            if (radarRing != null) Destroy(radarRing.gameObject);
            Instance = null;
        }
    }

    /// <summary>Draws the units after this frame's prediction.</summary>
    [ClientCallback]
    private void LateUpdate()
    {
        Selection.Prune();
        WAR2D.Client.ClientWorld.Instance?.Draw(Selection.Selected);
    }

    /// <summary>
    /// The command card and the order keys share these paths. Move, AttackMove, Repair and Dig arm the order
    /// for the next click; Stop and Hold apply to the selection at once.
    /// </summary>
    [Client]
    public void Order(OrderKind kind)
    {
        if (kind == OrderKind.Move || kind == OrderKind.AttackMove || kind == OrderKind.Repair || kind == OrderKind.Dig) Arm(kind);
        else
        {
            ArmedOrder = null;
            Selection.Order(kind, false, default);
        }
    }

    /// <summary>
    /// Sends the selection to a point (the minimap's right-click): the armed order if there is one,
    /// else Move. <paramref name="queue"/> queues it after the current order.
    /// </summary>
    [Client]
    public void OrderAt(int2 goal, bool queue)
    {
        OrderKind kind = ArmedOrder ?? OrderKind.Move;
        ArmedOrder = null;
        Selection.Order(kind, queue, goal);
    }

    /// <summary>Arms an order for the next click (only with units selected; Repair needs Builders, Dig Diggers).</summary>
    private void Arm(OrderKind kind)
    {
        if (Selection.Selected.Count == 0) return;
        if (kind == OrderKind.Repair && !SelectionHas(UnitType.Builder)) return;
        if (kind == OrderKind.Dig && !SelectionHas(UnitType.Digger)) return;
        CancelWallDrag();
        ArmedOrder = kind;
    }

    /// <summary>True when a selected unit is of the type.</summary>
    private bool SelectionHas(UnitType type)
    {
        ClientWorld world = ClientWorld.Instance;
        if (world == null) return false;
        foreach (int id in Selection.Selected)
            if (world.TryGet(id, out ClientUnitView view) && view.Type == type) return true;
        return false;
    }

    /// <summary>The type of a selected unit (None when unknown).</summary>
    private UnitType TypeOf(int id) => ClientWorld.Instance != null && ClientWorld.Instance.TryGet(id, out ClientUnitView view) ? view.Type : UnitType.None;

    /// <summary>
    /// Sends the selected Builders to build what was just placed at <paramref name="tile"/> (a blueprint's anchor or
    /// a wall segment); the rest of the selection is left alone. <paramref name="queue"/> queues it behind their job.
    /// </summary>
    [Client]
    public void OrderBuilders(OrderKind kind, int2 tile, bool queue)
    {
        if (!SelectionHas(UnitType.Builder) || WorldStateManager.Instance == null) return;
        var builders = new List<int>();
        foreach (int id in Selection.Selected) if (TypeOf(id) == UnitType.Builder) builders.Add(id);
        builders.Sort();
        ushort token = (ushort)UnityEngine.Random.Range(0, ushort.MaxValue);
        List<byte[]> chunks = OrderIdCodec.EncodeChunks(builders);
        for (int i = 0; i < chunks.Count; i++)
            WorldStateManager.Instance.CmdOrderChunk(token, chunks[i], i == chunks.Count - 1, (byte)kind, queue, tile);
    }

    private WallDrag wallDrag;

    /// <summary>The wall drag (armed by the Wall button), for the HUD's cost readout.</summary>
    public WallDrag Wall => wallDrag ??= new WallDrag(
        t => WorldStateManager.Instance != null && WorldStateManager.Instance.Map != null ? WorldStateManager.Instance.Map.Grid.TileAt(t) : TileType.Border,
        t => picker.TryPick(t, out _),
        Config.ConfigLoader.LoadConfig().Siege.MaxWallLine);

    /// <summary>Arms the wall drag: the next press on the map starts a line (the command card's Wall button).</summary>
    [Client]
    public void BeginWallDrag()
    {
        ArmedOrder = null;
        BuildingPlacement.Instance?.Cancel();
        Wall.Arm();
    }

    /// <summary>Drops an armed wall drag. False when none was armed.</summary>
    [Client]
    public bool CancelWallDrag() => wallDrag != null && wallDrag.Cancel();

    /// <summary>
    /// Runs the wall drag for this frame. Press starts the line, the drag re-routes it, release sends
    /// <c>CmdPlaceWall</c> and gives the selected Builders a Build order per valid tile (the first replaces their
    /// job unless Shift is held; the rest queue behind it, up to the waypoint limit). Shift keeps the Wall armed;
    /// right-click cancels. True when the drag used this frame's clicks.
    /// </summary>
    private bool UpdateWallDrag()
    {
        if (wallDrag == null || !wallDrag.Armed) return false;
        if (GameInput.Command.WasPressedThisFrame()) { wallDrag.Cancel(); return true; }
        Vector3 world = GetMouseWorldPosition();
        int2 tile = (int2)math.floor(new float2(world.x, world.y));
        if (!wallDrag.Dragging && GameInput.Select.WasPressedThisFrame() && !GameInput.PointerOverUI) wallDrag.Press(tile);
        if (wallDrag.Dragging) wallDrag.Move(tile, PlayerPalette.Of(localPlayer != null ? localPlayer.colourIndex : -1));
        if (wallDrag.Dragging && GameInput.Select.WasReleasedThisFrame()) FinishWallDrag(GameInput.QueueModifier.IsPressed());
        return true;
    }

    /// <summary>Places the dragged line and orders the Builders onto it; <paramref name="keep"/> (Shift) keeps the Wall armed.</summary>
    [Client]
    public void FinishWallDrag(bool keep)
    {
        if (wallDrag == null || !wallDrag.Dragging) return;
        var placed = new List<int2>();
        for (int i = 0; i < wallDrag.Line.Count; i++) if (wallDrag.IsValid(i)) placed.Add(wallDrag.Line[i]);
        if (wallDrag.Release(out int2 from, out int2 to) && WorldStateManager.Instance != null)
        {
            WorldStateManager.Instance.CmdPlaceWall(from, to);
            WAR2D.Audio.AudioService.Ui("building_placed");
            int orders = Mathf.Min(placed.Count, Config.ConfigLoader.LoadConfig().Orders.MaxQueued + 1);
            for (int i = 0; i < orders; i++) OrderBuilders(OrderKind.Build, placed[i], keep || i > 0);
        }
        wallDrag.ClearLine();
        if (!keep) wallDrag.Cancel();
    }

    /// <summary>What the local client knows of a tile, for <see cref="ClickOrders"/>.</summary>
    private ClickTarget TargetAt(int2 tile)
    {
        var target = new ClickTarget { Tile = TileType.Border };
        WorldStateManager wsm = WorldStateManager.Instance;
        if (wsm == null || wsm.Map == null || !wsm.Map.Grid.Contains(tile)) return target;
        target.Tile = wsm.Map.Grid.TileAt(tile);
        int index = wsm.Map.Grid.Index(tile);
        target.WallOwner = ClientTerrain.Current.WallOwner(index);
        target.WallHealth = ClientTerrain.Current.WallHealth(index);
        if (picker.TryPick(tile, out int id) && (subscribedBuildings ?? ClientBuildings.Current).Entries.TryGetValue(id, out ClientBuildings.Entry e) && !e.Ghost)
        {
            target.HasBuilding = true;
            target.BuildingOwner = e.Data.ownerId;
            target.Blueprint = e.Progress < 100;
            target.Damaged = e.Health.currentHealth < e.Health.maxHealth;
        }
        return target;
    }

    /// <summary>True for an owner on the local player's attack mask (the only diplomacy row it knows).</summary>
    private bool IsEnemy(int owner)
    {
        GameCore core = GameCore.Instance;
        if (core == null || localPlayer == null) return true;
        int slot = core.PlayerOrder.IndexOf(owner);
        return slot < 0 || (localPlayer.AttackMask & (1 << slot)) != 0;
    }

    /// <summary>The right-click: each part of the selection takes the order it can there (<see cref="ClickOrders"/>).</summary>
    private void OrderClick(int2 goal, bool queue)
    {
        (OrderKind builders, OrderKind diggers, OrderKind others) = ClickOrders.For(TargetAt(goal), BuildingData.UIntToInt(localPlayer.netId), IsEnemy);
        Selection.OrderEach(id => TypeOf(id) switch
        {
            UnitType.Builder => builders,
            UnitType.Digger => diggers,
            _ => others,
        }, queue, goal);
    }

    /// <summary>Selects one of the local player's buildings (others are ignored).</summary>
    [Client]
    public void SelectBuilding(BuildingData building, bool append = false)
    {
        if (localPlayer == null || building.ownerId != BuildingData.UIntToInt(localPlayer.netId)) return;
        ArmedOrder = null;
        // Only spawners combine (their production queues and rally points act together).
        bool spawner = SpawnerRules.IsProducer(building.buildingType);
        Selection.SelectBuilding(building.id, append && spawner && AllSpawners(Selection.SelectedBuildings));
    }

    /// <summary>True when every listed building is one of our spawners we can see.</summary>
    private bool AllSpawners(IReadOnlyList<int> ids)
    {
        foreach (int id in ids)
            if (!buildingGameObjects.TryGetValue(id, out GameObject go) || go == null || !go.TryGetComponent(out BuildingDataClient data) ||
                !SpawnerRules.IsProducer(data.buildingData.buildingType)) return false;
        return true;
    }

    /// <summary>The ids of our spawners (seen now) with a footprint tile inside the box.</summary>
    private List<int> OwnSpawnersIn(float2 lo, float2 hi, int ownerId)
    {
        var inBox = new List<int>();
        picker.InBox((int2)math.floor(lo), (int2)math.floor(hi), inBox);
        var ids = new List<int>();
        ClientBuildings known = subscribedBuildings ?? ClientBuildings.Current;
        foreach (int id in inBox)
            if (known.Entries.TryGetValue(id, out ClientBuildings.Entry e) && !e.Ghost && e.Data.ownerId == ownerId &&
                SpawnerRules.IsProducer(e.Data.buildingType)) ids.Add(id);
        return ids;
    }

    private const float DoubleTapSeconds = 0.35f;
    private float lastClickTime = -1f, lastSquadTapTime = -1f;
    private float2 lastClickAt;
    private int lastSquadTap = -1;

    /// <summary>The world rectangle the main camera shows.</summary>
    private static (float2 min, float2 max) CameraView()
    {
        Camera cam = Camera.main;
        if (cam == null) return (float2.zero, float2.zero);
        float halfH = cam.orthographicSize, halfW = halfH * cam.aspect;
        Vector3 c = cam.transform.position;
        return (new float2(c.x - halfW, c.y - halfH), new float2(c.x + halfW, c.y + halfH));
    }

    public static Vector3 GetMouseWorldPosition()
    {
        return GameInput.PointerWorld();
    }

    [ClientCallback]
    public void Update()
    {
        ShowBuildingSelection();

        //This stops the client from disconnecting if the worldstatemanager has not loaded yet or the client is not connected, i.e the client is loading in or out
        if (!NetworkClient.isConnected || WorldStateManager.Instance == null)
        {
            return;
        }


        if (GameInput.AttackMove.WasPressedThisFrame()) Arm(OrderKind.AttackMove);
        if (GameInput.Repair.WasPressedThisFrame()) Arm(OrderKind.Repair);
        if (GameInput.Dig.WasPressedThisFrame()) Arm(OrderKind.Dig);
        if (GameInput.Stop.WasPressedThisFrame()) Order(OrderKind.Stop);
        if (GameInput.Hold.WasPressedThisFrame()) Order(OrderKind.Hold);

        // While placing a building, clicks belong to the placement.
        BuildingPlacement placement = BuildingPlacement.Instance;
        bool clickUsed = placement != null && (placement.IsPlacing || placement.ActiveFrame == Time.frameCount);
        if (clickUsed) { ArmedOrder = null; CancelWallDrag(); }
        clickUsed |= UpdateWallDrag();

        // With an order armed, the next left or right click sends it instead of selecting or moving.
        if (!clickUsed && ArmedOrder.HasValue && (GameInput.Select.WasPressedThisFrame() || GameInput.Command.WasPressedThisFrame()))
        {
            OrderKind kind = ArmedOrder.Value;
            ArmedOrder = null;
            if (!GameInput.PointerOverUI)
            {
                Vector3 target = GetMouseWorldPosition();
                Selection.Order(kind, GameInput.QueueModifier.IsPressed(), new int2((int)target.x, (int)target.y));
                clickUsed = true;
            }
        }

        //Mouse down, start selection
        if (!clickUsed && GameInput.Select.WasPressedThisFrame() && !GameInput.PointerOverUI)
        {
            selecting = true;
            selectionBox.SetActive(true);
            Vector3 worldPosition = GetMouseWorldPosition();
            dragStart = worldPosition;
            startcorner = new int2((int)worldPosition.x, (int)worldPosition.y);
            endcorner = startcorner;
            selectionBox.transform.position = new Vector3(startcorner.x, startcorner.y, 0);
            selectionBox.transform.localScale = new Vector3(0, 0, 1);
        }

        if (selecting && GameInput.Select.IsPressed())
        {
            // The box follows the drag at any size (selection has no limit), from where the drag began.
            Vector3 worldPosition = GetMouseWorldPosition();
            Vector3 startPosition = new Vector3(dragStart.x, dragStart.y, 0);
            selectionBox.transform.position = (worldPosition + startPosition) / 2;
            selectionBox.transform.localScale = new Vector3(Mathf.Abs(worldPosition.x - startPosition.x), Mathf.Abs(worldPosition.y - startPosition.y), 1);
        }

        //Mouse up, end selection 
        if (selecting && GameInput.Select.WasReleasedThisFrame())
        {
            selectionBox.SetActive(false);
            Vector3 worldPosition = GetMouseWorldPosition();
            endcorner = new int2((int)worldPosition.x, (int)worldPosition.y);
            selecting = false;
            // A click (no drag) still selects what is under the pointer: pad the box to half a tile.
            float2 a = new float2(dragStart.x, dragStart.y), b = new float2(worldPosition.x, worldPosition.y);
            float2 lo = math.min(a, b) - 0.5f, hi = math.max(a, b) + 0.5f;
            int me = BuildingData.UIntToInt(localPlayer.netId);
            bool click = math.distance(a, b) < 0.5f;
            if (click && Time.unscaledTime - lastClickTime < DoubleTapSeconds && math.distance(b, lastClickAt) < 1f)
            {
                // Double-click: every own unit of the clicked unit's type on screen.
                (float2 viewMin, float2 viewMax) = CameraView();
                Selection.SelectTypeAt(b, viewMin, viewMax, me);
                lastClickTime = -1f;
            }
            else
            {
                bool append = GameInput.AppendModifier.IsPressed();
                var keptBuildings = new List<int>(append ? Selection.SelectedBuildings : System.Array.Empty<int>());
                Selection.SelectBox(lo, hi, me, append);
                if (Selection.Selected.Count == 0)
                {
                    // No unit of ours there: a click on one of our buildings selects it (Shift adds or removes a spawner),
                    // and a box selects our spawners inside it, so their queues and rally points act together.
                    if (append) Selection.SelectBuildings(keptBuildings, false);
                    if (click && TryOwnBuildingAt(b, me, out BuildingData building)) SelectBuilding(building, append);
                    else if (!click) Selection.SelectBuildings(OwnSpawnersIn(lo, hi, me), append);
                }
                if (click) { lastClickTime = Time.unscaledTime; lastClickAt = b; }
            }
        }

        if (!clickUsed && GameInput.Command.WasPressedThisFrame() && !GameInput.PointerOverUI)
        {
            Vector3 worldPosition = GetMouseWorldPosition();
            int2 goal = new int2((int)worldPosition.x, (int)worldPosition.y);
            // With only spawners selected, a right-click sets their rally point.
            if (Selection.Selected.Count == 0 && Selection.SelectedBuildings.Count > 0 && AllSpawners(Selection.SelectedBuildings))
                WorldStateManager.Instance?.CmdSetRally(ToArray(Selection.SelectedBuildings), goal, false);
            else OrderClick(goal, GameInput.QueueModifier.IsPressed());
        }

        for (int squad = 0; squad < WAR2D.Sim.Squads.Count; squad++)
        {
            if (!GameInput.Squad(squad).WasPressedThisFrame()) continue;
            if (GameInput.AssignModifier.IsPressed()) Selection.AssignSquad(squad);
            else
            {
                Selection.SelectSquad(squad);
                // Double-tapping a squad key centres the camera on the squad.
                if (squad == lastSquadTap && Time.unscaledTime - lastSquadTapTime < DoubleTapSeconds && Selection.TryCentre(out float2 centre))
                {
                    Camera main = Camera.main;
                    if (main != null && main.TryGetComponent(out Character.Character_Controler controller)) controller.CentreOn(centre);
                    lastSquadTap = -1;
                }
                else
                {
                    lastSquadTap = squad;
                    lastSquadTapTime = Time.unscaledTime;
                }
            }
        }

        //Get the corners of the camera (orthographic bounds)
        Camera cam = Camera.main;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;
        Vector3 c = cam.transform.position;
        int2 corner1 = new int2((int)math.floor(c.x - halfW) - visualAdditionalRange.x, (int)math.floor(c.y - halfH) - visualAdditionalRange.y);
        int2 corner2 = new int2((int)math.ceil(c.x + halfW) + visualAdditionalRange.x, (int)math.ceil(c.y + halfH) + visualAdditionalRange.y);

        // // Place the selection box at this point to show the box the server thinks the client can see for testing purposes

        // selectionBox.transform.position = (cameraCorner1 + cameraCorner2) / 2;
        // selectionBox.transform.localScale = new Vector3(Mathf.Abs(corner2.x - corner1.x), Mathf.Abs(corner2.y - corner1.y), 1);

        // TIM.Console.Log($"Updating client view to {corner1} {corner2}", TIM.MessageType.Network);
        // TIM.Console.Log($"Using WorldStateManager {WorldStateManager.Instance}", TIM.MessageType.Network);


        if (WorldStateManager.Instance.Map != null)
        {
            var (mapMin, mapMax) = WorldStateManager.Instance.MapBounds;
            corner1 = math.clamp(corner1, mapMin, mapMax);
            corner2 = math.clamp(corner2, mapMin, mapMax);
        }

        //Request from the server to update what the client can see for the next frame
        viewSendTimer += Time.unscaledDeltaTime;
        if ((!corner1.Equals(lastSentCorner1) || !corner2.Equals(lastSentCorner2)) && viewSendTimer >= 0.1f)
        {
            WorldStateManager.Instance.UpdateClientView(corner1, corner2);
            lastSentCorner1 = corner1;
            lastSentCorner2 = corner2;
            viewSendTimer = 0f;
        }
    }


    #region Buildings

    private ClientBuildings subscribedBuildings;
    private readonly BuildingPicker picker = new BuildingPicker(WorldStateManager.GetBuildingSize);

    /// <summary>Mirrors the buildings the server sends (see ClientBuildings) as GameObjects, including ones already known.</summary>
    [Client]
    private void SubscribeBuildings()
    {
        subscribedBuildings = ClientBuildings.Current;
        picker.Attach(subscribedBuildings);
        subscribedBuildings.Entered += OnBuildingEntered;
        subscribedBuildings.HealthChanged += OnBuildingHealthChanged;
        subscribedBuildings.Hidden += OnBuildingHidden;
        subscribedBuildings.Removed += OnBuildingRemoved;
        subscribedBuildings.ProgressChanged += OnBuildingProgress;
        Palettes.Changed += Retint;
        foreach (ClientBuildings.Entry entry in subscribedBuildings.Entries.Values)
        {
            OnBuildingEntered(entry.Data, entry.Health);
            if (entry.Ghost) OnBuildingHidden(entry.Data.id);
        }
    }

    private void UnsubscribeBuildings()
    {
        if (subscribedBuildings == null) return;
        picker.Detach();
        subscribedBuildings.Entered -= OnBuildingEntered;
        subscribedBuildings.HealthChanged -= OnBuildingHealthChanged;
        subscribedBuildings.Hidden -= OnBuildingHidden;
        subscribedBuildings.Removed -= OnBuildingRemoved;
        subscribedBuildings.ProgressChanged -= OnBuildingProgress;
        Palettes.Changed -= Retint;
        subscribedBuildings = null;
    }

    /// <summary>A seen building's construction moved on: its blueprint look follows.</summary>
    private void OnBuildingProgress(int id, byte progress)
    {
        if (!buildingGameObjects.TryGetValue(id, out GameObject go) || go == null || !go.TryGetComponent(out ConstructionView view)) return;
        go.TryGetComponent(out SpriteRenderer sprite);
        int2 size = go.TryGetComponent(out BuildingDataClient data) ? WorldStateManager.GetBuildingSize(data.buildingData.buildingType) : new int2(1, 1);
        view.Set(sprite, size, progress);
    }

    /// <summary>A building is seen: (re)creates its GameObject, replacing a ghost.</summary>
    private void OnBuildingEntered(BuildingData data, HealthComponent health)
    {
        if (buildingGameObjects.ContainsKey(data.id)) BuildingListRemove(0, data);
        BuildingListInsert(0, data);
        lastBuildingHealth[data.id] = health.currentHealth;
        AddHealthComponent(health);
    }

    private readonly Dictionary<int, float> lastBuildingHealth = new Dictionary<int, float>();

    /// <summary>A seen building's health changed: a finished one that gained health is being repaired (sparks).</summary>
    private void OnBuildingHealthChanged(HealthComponent health)
    {
        if (lastBuildingHealth.TryGetValue(health.entityId, out float before) && health.currentHealth > before &&
            (subscribedBuildings ?? ClientBuildings.Current).Entries.TryGetValue(health.entityId, out ClientBuildings.Entry e) && e.Progress >= 100 &&
            buildingGameObjects.TryGetValue(health.entityId, out GameObject go) && go != null)
            Effects.RepairSparks(go.transform.position);
        lastBuildingHealth[health.entityId] = health.currentHealth;
        AddHealthComponent(health);
    }

    /// <summary>A building left sight: it stays, grey and see-through, as last seen (and stops animating).</summary>
    private void OnBuildingHidden(int id)
    {
        if (!buildingGameObjects.TryGetValue(id, out GameObject go) || !go.TryGetComponent(out SpriteRenderer sprite)) return;
        if (go.TryGetComponent(out BuildingDataClient data)) sprite.sharedMaterial = TeamSpriteMaterial.For(data.buildingData.ownerId, true);
        if (go.TryGetComponent(out SpriteFrameAnimator animator)) animator.enabled = false;
    }

    private readonly List<BuildingSelectionMarker> buildingMarkers = new List<BuildingSelectionMarker>();
    private readonly List<SpriteRenderer> rallyFlags = new List<SpriteRenderer>();

    /// <summary>
    /// Keeps corner brackets on each selected building and a rally flag on each selected spawner's rally
    /// point (the owner's own data; hidden when nothing is selected).
    /// </summary>
    private void ShowBuildingSelection()
    {
        IReadOnlyList<int> ids = Selection.SelectedBuildings;
        int shown = 0, flags = 0;
        WAR2D.Art.ArtAtlas effects = WAR2D.Art.ArtAtlas.Load(WAR2D.Art.ArtAtlas.Effects);
        for (int i = 0; i < ids.Count; i++)
        {
            if (!buildingGameObjects.TryGetValue(ids[i], out GameObject go) || go == null || !go.TryGetComponent(out BuildingDataClient data)) continue;
            if (shown == buildingMarkers.Count) buildingMarkers.Add(new GameObject("BuildingSelection").AddComponent<BuildingSelectionMarker>());
            buildingMarkers[shown++].Show(ids[i], go, WorldStateManager.GetBuildingSize(data.buildingData.buildingType));
            if (localPlayer == null || !localPlayer.TryGetRally(ids[i], out int2 rally) || effects == null) continue;
            if (flags == rallyFlags.Count)
            {
                var flag = new GameObject("RallyFlag").AddComponent<SpriteRenderer>();
                flag.sortingOrder = SortingOrders.Effects;
                rallyFlags.Add(flag);
            }
            SpriteRenderer f = rallyFlags[flags++];
            f.sprite = effects.Sprite("RallyFlag", (int)(Time.time * 2f) % Mathf.Max(1, effects.FrameCount("RallyFlag")));
            f.sharedMaterial = TeamSpriteMaterial.For(data.buildingData.ownerId, false, ArtAtlas.Effects);
            f.transform.position = new Vector3(rally.x + 0.5f, rally.y + 0.5f, 0f);
            f.gameObject.SetActive(true);
        }
        for (int i = shown; i < buildingMarkers.Count; i++) buildingMarkers[i].Show(0, null, default);
        ShowRadarRange(ids);
        for (int i = flags; i < rallyFlags.Count; i++) rallyFlags[i].gameObject.SetActive(false);
    }

    private ClearingRing radarRing;
    private float radarSight = -1f;

    /// <summary>A selected own Radar shows its range: a dashed ring in its owner's colour (Figma: World look – v0.7).</summary>
    private void ShowRadarRange(IReadOnlyList<int> ids)
    {
        int radar = 0;
        float2 centre = default;
        ClientBuildings known = subscribedBuildings ?? ClientBuildings.Current;
        foreach (int id in ids)
        {
            if (!known.Entries.TryGetValue(id, out ClientBuildings.Entry e) || e.Ghost || e.Data.buildingType != BuildingType.Radar) continue;
            radar = id;
            centre = Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(BuildingType.Radar));
            break;
        }
        if (radar == 0)
        {
            if (radarRing != null) radarRing.gameObject.SetActive(false);
            return;
        }
        if (radarSight < 0f) radarSight = Config.ConfigLoader.LoadConfig().GetBuilding(BuildingType.Radar).Sight;
        if (radarRing == null) radarRing = ClearingRing.Create("RadarRange", SortingOrders.ClearingRing);
        radarRing.Set(centre, radarSight, PlayerPalette.Of(localPlayer != null ? localPlayer.colourIndex : -1));
    }

    private static int[] ToArray(IReadOnlyList<int> ids)
    {
        var array = new int[ids.Count];
        for (int i = 0; i < array.Length; i++) array[i] = ids[i];
        return array;
    }

    /// <summary>Shows a building type's atlas sprite on <paramref name="go"/>, animating its idle frames.</summary>
    private static void ShowBuildingSprite(GameObject go, SpriteRenderer sprite, BuildingType type, int facing)
    {
        ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Buildings);
        if (atlas == null) Debug.LogError($"Resources/{ArtAtlas.Folder}/{ArtAtlas.Buildings} is missing: run WAR-2D/Art/Build all.");
        if (!go.TryGetComponent(out SpriteFrameAnimator animator)) animator = go.AddComponent<SpriteFrameAnimator>();
        // The spawner's lamp blinks once a second; the drill turns every tick.
        animator.Play(atlas, type.ToString(), type == BuildingType.SmallUnitSpawner ? ArtStyle.FramesPerSecond / 2 : 1, facing);
        // Taller buildings stand in front of the ones behind them (the 3/4 view).
        sprite.sortingOrder = SortingOrders.Building(go.transform.position.y);
    }

    /// <summary>Re-tints every building for a new palette.</summary>
    private void Retint()
    {
        if (this == null) { Palettes.Changed -= Retint; return; } // destroyed after the client stopped (OnDestroy is client-only)
        if (subscribedBuildings == null) return;
        foreach (ClientBuildings.Entry entry in subscribedBuildings.Entries.Values)
        {
            if (!buildingGameObjects.TryGetValue(entry.Data.id, out GameObject go) || go == null || !go.TryGetComponent(out SpriteRenderer sprite)) continue;
            sprite.sharedMaterial = TeamSpriteMaterial.For(entry.Data.ownerId, entry.Ghost);
        }
    }

    /// <summary>The local player's building (seen now, not a ghost) whose footprint holds the world point.</summary>
    private bool TryOwnBuildingAt(float2 point, int ownerId, out BuildingData building)
    {
        if (picker.TryPick((int2)math.floor(point), out int id) &&
            (subscribedBuildings ?? ClientBuildings.Current).Entries.TryGetValue(id, out ClientBuildings.Entry e) &&
            !e.Ghost && e.Data.ownerId == ownerId)
        {
            building = e.Data;
            return true;
        }
        building = default;
        return false;
    }

    private void OnBuildingRemoved(int id)
    {
        lastBuildingHealth.Remove(id);
        Selection.RemoveBuilding(id);
        if (!buildingGameObjects.TryGetValue(id, out GameObject go)) return;
        Destroy(go);
        buildingGameObjects.Remove(id);
    }
    //This is called when a unit is added or inserted into the list, returning the index of the list and the unit itself
    [Client]
    public void BuildingListInsert(int index, BuildingData unit)
    {
        // Debug.Log($"UnitListInsert called with unit id: '{unit.id}', sprite:  '{unit.spriteName}', position : '{unit.position}'");
        if (buildingGameObjects.ContainsKey(unit.id))
        {
            Debug.LogError("BuildingListInsert called with building that already exists in the list. Building id: " + unit.id);
            return;
        }

        //Create a new game object
        GameObject go = new GameObject();
        int2 anchor = (int2)math.round(unit.position);
        float2 centre = Footprint.VisualCenter(anchor, WorldStateManager.GetBuildingSize(unit.buildingType));
        // Upright: the Miner shows its rotation as a rendered facing (the 3/4 view only reads one way up).
        go.transform.position = new Vector3(centre.x, centre.y, 0);

        SpriteRenderer buildingSprite = go.AddComponent<SpriteRenderer>();
        buildingSprite.sharedMaterial = TeamSpriteMaterial.For(unit.ownerId, false);
        ShowBuildingSprite(go, buildingSprite, unit.buildingType, TeamSpriteMaterial.Facing(unit.buildingType, unit.rotation));
        // Clicks hit the footprint, not the sprite's headroom or shadow.
        int2 footprint = WorldStateManager.GetBuildingSize(unit.buildingType);
        BoxCollider2D collider = go.AddComponent<BoxCollider2D>();
        collider.isTrigger = true;
        collider.size = new Vector2(footprint.x, footprint.y);
        go.name = $"Building_{unit.buildingType}_{unit.id}";
        byte progress = (subscribedBuildings ?? ClientBuildings.Current).Entries.TryGetValue(unit.id, out ClientBuildings.Entry known) ? known.Progress : (byte)100;
        go.AddComponent<ConstructionView>().Set(buildingSprite, footprint, progress);

        //Check if there is a client script for the building type
        Type buildingType = null;

        var buildingDataClient = go.AddComponent<BuildingDataClient>();
        buildingDataClient.buildingData = unit;
        // Map building types to their corresponding client class names
        switch (unit.buildingType)
        {
            case BuildingType.SmallUnitSpawner:
                buildingType = typeof(SpawnerClientManager);
                break;
            // Add other building types as needed
            case BuildingType.Base:
            case BuildingType.Miner:
            case BuildingType.Bomb:
            case BuildingType.Radar:
                break; // no client-side behaviour
            default:
                Debug.LogWarning($"No client script mapping found for building type {unit.buildingType}");
                break;
        }

        if (buildingType != null)
        {
            //Add the component to the game object
            var clientcomponent = go.AddComponent(buildingType);
            var field = clientcomponent.GetType().GetField("buildingData");
            if (field != null)
            {
                field.SetValue(clientcomponent, unit);
            }
            else
            {
                Debug.LogWarning($"Client script '{buildingType.Name}' does not contain a 'buildingData' field. Or it is spelled incorrectly. Most likely the ladder.");
            }
        }

        buildingGameObjects.Add(unit.id, go);
    }

    //Called when a unit is removed from the list, returning the index of the list and the old unit itself
    [Client]
    public void BuildingListRemove(int index, BuildingData OldUnit)
    {
        //Remove the game object from the list
        if (buildingGameObjects.ContainsKey(OldUnit.id))
        {
            Destroy(buildingGameObjects[OldUnit.id]);
            buildingGameObjects.Remove(OldUnit.id);
        }
    }

    //Called when the enitre list is cleared
    [Client]
    public void BuildingListClear()
    {
        //Destroy all the game objects
        foreach (var item in buildingGameObjects)
        {
            Destroy(item.Value);
        }
        buildingGameObjects.Clear();
    }

    //Called when an item in the list is set to a new value
    //Note: I am not sure whether this will be called if something inside the object is changed, or if the object itself is changed
    [Client]
    public void BuildingListSet(int index, BuildingData oldUnit, BuildingData newUnit)
    {

        if (oldUnit.id != newUnit.id)
        {
            Debug.LogError("BuildingListSet called with different id for old and new building");
            return;
        }

        //If the unit is not in the list, add it, this should not happen but just in case
        if (!buildingGameObjects.ContainsKey(newUnit.id))
        {
            ////UnitListInsert(index, newUnit);
            return;
        }


        //If only the position is changed, move the game object (buildings do not move once placed)
        if (oldUnit.position.x != newUnit.position.x || oldUnit.position.y != newUnit.position.y)
        {
            if (buildingGameObjects.ContainsKey(oldUnit.id))
            {
                int2 anchor = (int2)math.round(newUnit.position);
                float2 centre = Footprint.VisualCenter(anchor, WorldStateManager.GetBuildingSize(newUnit.buildingType));
                buildingGameObjects[oldUnit.id].transform.position = new Vector3(centre.x, centre.y, 0);
            }
        }
        //If the sprite is changed, change the sprite
        if (oldUnit.buildingType != newUnit.buildingType)
        {
            if (buildingGameObjects.ContainsKey(oldUnit.id))
            {
                GameObject go = buildingGameObjects[oldUnit.id];
                ShowBuildingSprite(go, go.GetComponent<SpriteRenderer>(), newUnit.buildingType, TeamSpriteMaterial.Facing(newUnit.buildingType, newUnit.rotation));
            }
        }

    }

    [Client]
    public void HealthListInsert(int index, HealthComponent health)
    {
        AddHealthComponent(health);
    }
    [Client]
    public void HealthListSet(int index, HealthComponent old, HealthComponent health)
    {
        AddHealthComponent(health);
    }
    [Client]
    public void HealthListRemove(int index, HealthComponent health)
    {
        AddHealthComponent(health, remove: true);
    }
    [Client]
    public void HealthListClear()
    {
        foreach (var buildingGO in buildingGameObjects.Values)
        {
            var healthComponent = buildingGO.GetComponent<BuildingDataClient>();
            if (healthComponent != null)
            {
                Destroy(healthComponent);
            }
        }
    }
    [Client]
    private void AddHealthComponent(HealthComponent health, bool remove = false)
    {
        if (buildingGameObjects.ContainsKey(health.entityId))
        {
            var buildingGO = buildingGameObjects[health.entityId];
            var healthComponent = buildingGO.GetComponent<BuildingDataClient>();
            if (healthComponent == null && !remove)
            {
                healthComponent = buildingGO.AddComponent<BuildingDataClient>();
            }
            if (!remove)
            {
                healthComponent.healthComponent = health;
            }
            else
            {
                Destroy(healthComponent);
            }
        }
        else
        {
            Debug.LogWarning($"No game object found for entity id {health.entityId} to {(remove ? "remove" : "add")} HealthComponent.This is normal when entitys are being cleaned up.");
        }
    }



    #endregion
}