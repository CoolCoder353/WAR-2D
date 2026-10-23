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

    /// <summary>The world feedback for orders: moved goals, queued goals, the Dig drag and dig marks.</summary>
    public OrderFeedback Feedback { get; private set; }
    private Vector3 dragStart;



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
            Feedback = gameObject.AddComponent<OrderFeedback>();
            Feedback.Init(Config.ConfigLoader.LoadConfig().Orders.MaxQueued, IsDiggableHere, PositionOf);
            Selection.Ordered += OnOrdered;
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
            foreach (ClearingRing ring in rangeRings) if (ring != null) Destroy(ring.gameObject);
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
        if (QueueFull(queue)) return;
        Selection.Order(kind, queue, goal);
    }

    /// <summary>True when a Shift-queued order would be dropped: the first selected unit already has the most goals (it is not sent).</summary>
    private bool QueueFull(bool queue) => queue && Feedback != null && Selection.First != 0 && Feedback.Queue.IsFull(Selection.First);

    /// <summary>Where the client draws a unit, or null when it doesn't know it.</summary>
    private static float2? PositionOf(int id) =>
        ClientWorld.Instance != null && ClientWorld.Instance.TryGet(id, out ClientUnitView view) ? view.Position : (float2?)null;

    /// <summary>
    /// A tile a dig area digs, as far as this client knows: rock, a gem, or a wall of a player we attack. Never the
    /// border ring or our own or an ally's wall (the server's <c>SimCommandSystem.IsAreaDiggable</c>).
    /// </summary>
    private bool IsDiggableHere(int2 tile)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        if (wsm == null || wsm.Map == null || localPlayer == null) return false;
        (int2 min, int2 max) = wsm.MapBounds;
        if (!CommandValidator.IsInside(tile, min + 1, max - 1)) return false;
        TileType type = wsm.Map.Grid.TileAt(tile);
        if (type == TileType.Wall || type == TileType.Gem) return true;
        if (type != TileType.PlayerWall) return false;
        int owner = ClientTerrain.Current.WallOwner(wsm.Map.Grid.Index(tile));
        return owner != 0 && owner != BuildingData.UIntToInt(localPlayer.netId) && IsEnemy(owner);
    }

    /// <summary>Follows every order sent: the goals each unit holds, dig marks it replaces, and a goal the server will move off rock.</summary>
    private void OnOrdered(OrderKind kind, bool queue, int2 goal, IReadOnlyList<int> ids)
    {
        if (Feedback == null) return;
        if (kind == OrderKind.Stop || kind == OrderKind.Hold || kind == OrderKind.Detonate)
        {
            Feedback.Queue.Cleared(ids);
            Feedback.Unmark(ids);
            return;
        }
        int2 to = goal;
        if ((kind == OrderKind.Move || kind == OrderKind.AttackMove) && TryMovedGoal(goal, out int2 moved))
        {
            Feedback.GoalMoved(goal, moved);
            to = moved;
        }
        Feedback.Queue.Sent(ids, (float2)to + 0.5f, queue);
        if (!queue) Feedback.Unmark(ids);
    }

    /// <summary>
    /// The open ground the server will send a goal on rock to (<see cref="WAR2D.Sim.OrderBook.SnapGoal"/> on this
    /// client's copy of the map, from the selection's centre). False when the goal stays where it is, or has no open
    /// ground near it.
    /// </summary>
    private bool TryMovedGoal(int2 goal, out int2 moved)
    {
        moved = goal;
        WorldStateManager wsm = WorldStateManager.Instance;
        if (wsm == null || wsm.Map == null || !wsm.Map.Grid.Contains(goal)) return false;
        int2 from = Selection.TryCentre(out float2 centre) ? (int2)math.floor(centre) : goal;
        return WAR2D.Sim.OrderBook.SnapGoal(wsm.Map.Grid, goal, from, WAR2D.Sim.OrderBook.GoalSnapRadius, out moved) && !moved.Equals(goal);
    }

    /// <summary>Arms an order for the next click (only with units selected; Repair needs Builders, Dig Diggers).</summary>
    private void Arm(OrderKind kind)
    {
        if (Selection.Selected.Count == 0) return;
        if (kind == OrderKind.Repair && !SelectionHas(UnitType.Builder)) return;
        if (kind == OrderKind.Dig && !SelectionHas(UnitType.Digger)) return;
        CancelWallDrag();
        CancelDigDrag();
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
        Feedback?.Queue.Sent(builders, (float2)tile + 0.5f, queue);
        if (!queue) Feedback?.Unmark(builders);
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

    private bool digDragging;
    private int2 digFrom;

    /// <summary>True while a Dig drag is under way (Dig armed and the button held on the map).</summary>
    public bool DigDragging => digDragging;

    /// <summary>Drops a Dig drag under way and disarms Dig. False when none was under way.</summary>
    [Client]
    public bool CancelDigDrag()
    {
        if (!digDragging) return false;
        digDragging = false;
        Feedback?.HideDigPreview();
        ArmedOrder = null;
        return true;
    }

    /// <summary>
    /// Runs the Dig drag for this frame (Figma <c>Dig – drag an area</c>). With Dig armed, a press on the map starts a box;
    /// the tiles it would dig light up; release sends it (<see cref="FinishDigDrag"/>). Right-click cancels. True
    /// when the drag used this frame's clicks.
    /// </summary>
    private bool UpdateDigDrag()
    {
        if (!digDragging)
        {
            if (ArmedOrder != OrderKind.Dig || !GameInput.Select.WasPressedThisFrame() || GameInput.PointerOverUI) return false;
            digDragging = true;
            digFrom = MouseTile();
        }
        if (GameInput.Command.WasPressedThisFrame()) { CancelDigDrag(); return true; }
        int2 tile = MouseTile();
        if (DigBox(digFrom, tile, out int2 lo, out int2 hi, out bool clamped)) Feedback?.ShowDigPreview(lo, hi, clamped);
        else Feedback?.HideDigPreview();
        if (GameInput.Select.WasReleasedThisFrame()) FinishDigDrag(tile, GameInput.QueueModifier.IsPressed());
        return true;
    }

    private static int2 MouseTile()
    {
        Vector3 world = GetMouseWorldPosition();
        return (int2)math.floor(new float2(world.x, world.y));
    }

    /// <summary>The drag's box as the server will take it (<see cref="CommandValidator.TryDigArea"/>); <paramref name="clamped"/> when the size cap cut it.</summary>
    private static bool DigBox(int2 from, int2 to, out int2 lo, out int2 hi, out bool clamped)
    {
        lo = hi = from;
        clamped = false;
        WorldStateManager wsm = WorldStateManager.Instance;
        if (wsm == null || wsm.Map == null) return false;
        int maxSide = Config.ConfigLoader.LoadConfig().Orders.MaxDigArea;
        (int2 min, int2 max) = wsm.MapBounds;
        clamped = math.any(math.abs(to - from) + 1 > maxSide);
        return CommandValidator.TryDigArea(from, to, min, max, maxSide, out lo, out hi);
    }

    /// <summary>
    /// Ends the Dig drag at <paramref name="tile"/>: a click without a drag digs that one tile (as before); a box gives the
    /// selected Diggers a dig area. <paramref name="keep"/> (Shift) queues it and keeps Dig armed.
    /// </summary>
    [Client]
    public void FinishDigDrag(int2 tile, bool keep)
    {
        if (!digDragging) return;
        digDragging = false;
        Feedback?.HideDigPreview();
        if (!keep) ArmedOrder = null;
        if (tile.Equals(digFrom))
        {
            if (!QueueFull(keep)) Selection.Order(OrderKind.Dig, keep, tile);
            return;
        }
        if (WorldStateManager.Instance == null || !DigBox(digFrom, tile, out int2 lo, out int2 hi, out _)) return;
        var diggers = new List<int>();
        foreach (int id in Selection.Selected) if (TypeOf(id) == UnitType.Digger) diggers.Add(id);
        if (diggers.Count == 0) return;
        diggers.Sort();
        ushort token = (ushort)UnityEngine.Random.Range(0, ushort.MaxValue);
        List<byte[]> chunks = OrderIdCodec.EncodeChunks(diggers);
        for (int i = 0; i < chunks.Count; i++)
            WorldStateManager.Instance.CmdDigAreaChunk(token, chunks[i], i == chunks.Count - 1, keep, digFrom, tile);
        Feedback?.AddDigMarks(lo, hi, diggers, keep);
        Feedback?.Queue.Sent(diggers, ((float2)lo + hi + 1f) * 0.5f, keep);
    }

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
            if (!Known.Entries.TryGetValue(id, out ClientBuildings.Entry e) || e.Ghost || !SpawnerRules.IsProducer(e.Data.buildingType)) return false;
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
        if (GameInput.Detonate.WasPressedThisFrame()) Order(OrderKind.Detonate);

        // While placing a building, clicks belong to the placement.
        BuildingPlacement placement = BuildingPlacement.Instance;
        bool clickUsed = placement != null && (placement.IsPlacing || placement.ActiveFrame == Time.frameCount);
        if (clickUsed) { ArmedOrder = null; CancelWallDrag(); }
        if (clickUsed) CancelDigDrag();
        clickUsed |= UpdateWallDrag();
        clickUsed |= UpdateDigDrag();
        Feedback?.ShowQueueOf(GameInput.QueueModifier.IsPressed() && Selection.Selected.Count > 0 ? Selection.First : 0);

        // With an order armed, the next left or right click sends it instead of selecting or moving.
        if (!clickUsed && ArmedOrder.HasValue && (GameInput.Select.WasPressedThisFrame() || GameInput.Command.WasPressedThisFrame()))
        {
            OrderKind kind = ArmedOrder.Value;
            ArmedOrder = null;
            if (!GameInput.PointerOverUI)
            {
                Vector3 target = GetMouseWorldPosition();
                bool queue = GameInput.QueueModifier.IsPressed();
                if (!QueueFull(queue)) Selection.Order(kind, queue, new int2((int)target.x, (int)target.y));
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
            {
                WorldStateManager.Instance?.CmdSetRally(System.Linq.Enumerable.ToArray(Selection.SelectedBuildings), goal, false);
                // The server moves a rally on rock to open ground like any goal: show where.
                WorldStateManager wsm = WorldStateManager.Instance;
                if (wsm != null && wsm.Map != null && wsm.Map.Grid.Contains(goal) &&
                    WAR2D.Sim.OrderBook.SnapGoal(wsm.Map.Grid, goal, goal, WAR2D.Sim.OrderBook.GoalSnapRadius, out int2 rally) && !rally.Equals(goal))
                    Feedback?.GoalMoved(goal, rally);
            }
            else if (!QueueFull(GameInput.QueueModifier.IsPressed())) OrderClick(goal, GameInput.QueueModifier.IsPressed());
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

    /// <summary>The buildings the client knows (the instanced renderer draws them; clicks resolve through the picker).</summary>
    private ClientBuildings Known => subscribedBuildings ?? ClientBuildings.Current;

    /// <summary>Follows the buildings the server sends (see ClientBuildings), including ones already known.</summary>
    [Client]
    private void SubscribeBuildings()
    {
        subscribedBuildings = ClientBuildings.Current;
        picker.Attach(subscribedBuildings);
        subscribedBuildings.Entered += OnBuildingEntered;
        subscribedBuildings.HealthChanged += OnBuildingHealthChanged;
        subscribedBuildings.Removed += OnBuildingRemoved;
        foreach (ClientBuildings.Entry entry in subscribedBuildings.Entries.Values) OnBuildingEntered(entry.Data, entry.Health);
    }

    private void UnsubscribeBuildings()
    {
        if (subscribedBuildings == null) return;
        picker.Detach();
        subscribedBuildings.Entered -= OnBuildingEntered;
        subscribedBuildings.HealthChanged -= OnBuildingHealthChanged;
        subscribedBuildings.Removed -= OnBuildingRemoved;
        subscribedBuildings = null;
    }

    /// <summary>The visual centre of a building's footprint.</summary>
    private static float2 CentreOf(in BuildingData data) =>
        Footprint.VisualCenter((int2)math.round(data.position), WorldStateManager.GetBuildingSize(data.buildingType));

    /// <summary>Each seen building's health at its last change, and whether it was finished then.</summary>
    private readonly Dictionary<int, (float health, bool finished)> lastBuildingHealth = new Dictionary<int, (float, bool)>();

    private void OnBuildingEntered(BuildingData data, HealthComponent health) =>
        lastBuildingHealth[data.id] = (health.currentHealth, Known.Entries.TryGetValue(data.id, out ClientBuildings.Entry e) && e.Progress >= 100);

    /// <summary>
    /// A seen building's health changed: one that was already finished at its last change and gained health
    /// is being repaired (sparks). The construction step that finishes a blueprint raises its health too;
    /// that is not a repair.
    /// </summary>
    private void OnBuildingHealthChanged(HealthComponent health)
    {
        bool finished = Known.Entries.TryGetValue(health.entityId, out ClientBuildings.Entry e) && e.Progress >= 100;
        if (finished && lastBuildingHealth.TryGetValue(health.entityId, out var before) && before.finished && health.currentHealth > before.health)
            Effects.RepairSparks((Vector2)CentreOf(e.Data));
        lastBuildingHealth[health.entityId] = (health.currentHealth, finished);
    }

    private void OnBuildingRemoved(int id)
    {
        lastBuildingHealth.Remove(id);
        Selection.RemoveBuilding(id);
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
        ClientBuildings known = Known;
        for (int i = 0; i < ids.Count; i++)
        {
            if (!known.Entries.TryGetValue(ids[i], out ClientBuildings.Entry e) || e.Ghost) continue;
            if (shown == buildingMarkers.Count) buildingMarkers.Add(new GameObject("BuildingSelection").AddComponent<BuildingSelectionMarker>());
            buildingMarkers[shown++].Show(ids[i], CentreOf(e.Data), WorldStateManager.GetBuildingSize(e.Data.buildingType));
            if (localPlayer == null || !localPlayer.TryGetRally(ids[i], out int2 rally) || effects == null) continue;
            // A rally on rock sits where its units will go: the open ground the server moves it to.
            WorldStateManager wsm = WorldStateManager.Instance;
            if (wsm != null && wsm.Map != null && wsm.Map.Grid.Contains(rally) &&
                WAR2D.Sim.OrderBook.SnapGoal(wsm.Map.Grid, rally, rally, WAR2D.Sim.OrderBook.GoalSnapRadius, out int2 open)) rally = open;
            if (flags == rallyFlags.Count)
            {
                var flag = new GameObject("RallyFlag").AddComponent<SpriteRenderer>();
                flag.sortingOrder = SortingOrders.Effects;
                rallyFlags.Add(flag);
            }
            SpriteRenderer f = rallyFlags[flags++];
            f.sprite = effects.Sprite("RallyFlag", (int)(Time.time * 2f) % Mathf.Max(1, effects.FrameCount("RallyFlag")));
            f.sharedMaterial = TeamSpriteMaterial.For(e.Data.ownerId, false, ArtAtlas.Effects);
            f.transform.position = new Vector3(rally.x + 0.5f, rally.y + 0.5f, 0f);
            f.gameObject.SetActive(true);
        }
        for (int i = shown; i < buildingMarkers.Count; i++) buildingMarkers[i].Hide();
        ShowRangeRings(ids);
        for (int i = flags; i < rallyFlags.Count; i++) rallyFlags[i].gameObject.SetActive(false);
    }

    private readonly List<ClearingRing> rangeRings = new List<ClearingRing>();

    /// <summary>Most range rings shown at once (one per selected Radar, Gun turret or Jammer).</summary>
    private const int MaxRangeRings = 32;

    /// <summary>A ring drawn for a building that isn't working (a blueprint, or unpaid) is this opaque (Figma: Range rings – v0.13).</summary>
    public const float IdleRingOpacity = 0.4f;

    /// <summary>
    /// Selected own Radars, Gun turrets and Jammers show their range: a dashed ring in the owner's colour at the
    /// Radar's sight, the turret's range or the jam radius (Figma: World look – v0.7, Range rings – v0.13), at
    /// 40% while the building is a blueprint or looks unpaid.
    /// </summary>
    private void ShowRangeRings(IReadOnlyList<int> ids)
    {
        int shown = 0;
        Color32 colour = PlayerPalette.Of(localPlayer != null ? localPlayer.colourIndex : -1);
        int own = localPlayer != null ? BuildingData.UIntToInt(localPlayer.netId) : int.MinValue;
        foreach (int id in ids)
        {
            if (shown >= MaxRangeRings) break;
            if (!Known.Entries.TryGetValue(id, out ClientBuildings.Entry e) || e.Ghost || e.Data.ownerId != own) continue;
            float radius = DefenceView.RingRadius(e.Data.buildingType);
            if (radius <= 0f) continue;
            bool idle = e.Progress < 100 || (e.Data.buildingType != BuildingType.Radar && DefenceView.LooksUnpaid(e));
            Color32 c = colour;
            c.a = (byte)(idle ? 255 * IdleRingOpacity : 255);
            if (shown == rangeRings.Count) rangeRings.Add(ClearingRing.Create("RangeRing", SortingOrders.ClearingRing));
            rangeRings[shown++].Set(CentreOf(e.Data), radius, c);
        }
        for (int i = shown; i < rangeRings.Count; i++) if (rangeRings[i] != null) rangeRings[i].gameObject.SetActive(false);
    }

    /// <summary>The local player's building (seen now, not a ghost) whose footprint holds the world point.</summary>
    private bool TryOwnBuildingAt(float2 point, int ownerId, out BuildingData building)
    {
        if (picker.TryPick((int2)math.floor(point), out int id) && Known.Entries.TryGetValue(id, out ClientBuildings.Entry e) &&
            !e.Ghost && e.Data.ownerId == ownerId)
        {
            building = e.Data;
            return true;
        }
        building = default;
        return false;
    }

    #endregion
}