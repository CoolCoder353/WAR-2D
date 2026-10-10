using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
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
    /// The command card and the order keys share these paths. Move and AttackMove arm the order for the
    /// next click; Stop and Hold apply to the selection at once.
    /// </summary>
    [Client]
    public void Order(OrderKind kind)
    {
        if (kind == OrderKind.Move || kind == OrderKind.AttackMove) Arm(kind);
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

    /// <summary>Arms Move or AttackMove for the next click (only with units selected).</summary>
    private void Arm(OrderKind kind)
    {
        if (Selection.Selected.Count > 0) ArmedOrder = kind;
    }

    /// <summary>Selects one of the local player's buildings (others are ignored).</summary>
    [Client]
    public void SelectBuilding(BuildingData building)
    {
        if (localPlayer == null || building.ownerId != BuildingData.UIntToInt(localPlayer.netId)) return;
        ArmedOrder = null;
        Selection.SelectBuilding(building.id);
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

        //This stops the client from disconnecting if the worldstatemanager has not loaded yet or the client is not connected, i.e the client is loading in or out
        if (!NetworkClient.isConnected || WorldStateManager.Instance == null)
        {
            return;
        }


        if (GameInput.AttackMove.WasPressedThisFrame()) Arm(OrderKind.AttackMove);
        if (GameInput.Stop.WasPressedThisFrame()) Order(OrderKind.Stop);
        if (GameInput.Hold.WasPressedThisFrame()) Order(OrderKind.Hold);

        // While placing a building, clicks belong to the placement.
        BuildingPlacement placement = BuildingPlacement.Instance;
        bool clickUsed = placement != null && (placement.IsPlacing || placement.ActiveFrame == Time.frameCount);
        if (clickUsed) ArmedOrder = null;

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

            Vector3 worldPosition = GetMouseWorldPosition();
            Vector3 startPosition = new Vector3(startcorner.x, startcorner.y, 0);
            float sqrdistance = (worldPosition - startPosition).sqrMagnitude;
            if (sqrdistance < 1000 && sqrdistance > 1) // Only update if the mouse is within 100 units from the origin and more than 1 unit away
            {
                Vector3 center = (worldPosition + startPosition) / 2;
                selectionBox.transform.position = center;
                Vector3 size = new Vector3(Mathf.Abs(worldPosition.x - startPosition.x), Mathf.Abs(worldPosition.y - startPosition.y), 1);
                selectionBox.transform.localScale = size;

            }




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
                Selection.SelectBox(lo, hi, me, GameInput.AppendModifier.IsPressed());
                if (click) { lastClickTime = Time.unscaledTime; lastClickAt = b; }
            }
        }

        if (!clickUsed && GameInput.Command.WasPressedThisFrame() && !GameInput.PointerOverUI)
        {
            Vector3 worldPosition = GetMouseWorldPosition();
            int2 goal = new int2((int)worldPosition.x, (int)worldPosition.y);
            Selection.Order(OrderKind.Move, GameInput.QueueModifier.IsPressed(), goal);
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

    /// <summary>Mirrors the buildings the server sends (see ClientBuildings) as GameObjects, including ones already known.</summary>
    [Client]
    private void SubscribeBuildings()
    {
        subscribedBuildings = ClientBuildings.Current;
        subscribedBuildings.Entered += OnBuildingEntered;
        subscribedBuildings.HealthChanged += OnBuildingHealthChanged;
        subscribedBuildings.Hidden += OnBuildingHidden;
        subscribedBuildings.Removed += OnBuildingRemoved;
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
        subscribedBuildings.Entered -= OnBuildingEntered;
        subscribedBuildings.HealthChanged -= OnBuildingHealthChanged;
        subscribedBuildings.Hidden -= OnBuildingHidden;
        subscribedBuildings.Removed -= OnBuildingRemoved;
        Palettes.Changed -= Retint;
        subscribedBuildings = null;
    }

    /// <summary>A building is seen: (re)creates its GameObject, replacing a ghost.</summary>
    private void OnBuildingEntered(BuildingData data, HealthComponent health)
    {
        if (buildingGameObjects.ContainsKey(data.id)) BuildingListRemove(0, data);
        BuildingListInsert(0, data);
        AddHealthComponent(health);
    }

    private void OnBuildingHealthChanged(HealthComponent health) => AddHealthComponent(health);

    /// <summary>A building left sight: it stays, dimmed, as last seen.</summary>
    private void OnBuildingHidden(int id)
    {
        if (buildingGameObjects.TryGetValue(id, out GameObject go) && go.TryGetComponent(out SpriteRenderer sprite))
            sprite.color = GhostTint(sprite.color);
    }

    /// <summary>A building's tint: its owner's colour (current palette), half-mixed into the sprite.</summary>
    private static Color Tint(int ownerId) => Color.Lerp(Color.white, PlayerPalette.OfOwner(ownerId), 0.5f);

    /// <summary>A last-seen ghost: dimmed and see-through.</summary>
    private static Color GhostTint(Color tint) => new Color(tint.r * 0.55f, tint.g * 0.55f, tint.b * 0.55f, 0.7f);

    /// <summary>Re-tints every building for a new palette.</summary>
    private void Retint()
    {
        if (this == null) { Palettes.Changed -= Retint; return; } // destroyed after the client stopped (OnDestroy is client-only)
        if (subscribedBuildings == null) return;
        foreach (ClientBuildings.Entry entry in subscribedBuildings.Entries.Values)
        {
            if (!buildingGameObjects.TryGetValue(entry.Data.id, out GameObject go) || go == null || !go.TryGetComponent(out SpriteRenderer sprite)) continue;
            Color tint = Tint(entry.Data.ownerId);
            sprite.color = entry.Ghost ? GhostTint(tint) : tint;
        }
    }

    private void OnBuildingRemoved(int id)
    {
        if (Selection.SelectedBuilding == id) Selection.ClearBuilding();
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
        go.transform.position = new Vector3(centre.x, centre.y, 0);

        SpriteRenderer buildingSprite = go.AddComponent<SpriteRenderer>();
        buildingSprite.sprite = Resources.Load<Sprite>(unit.buildingType.ToString());
        buildingSprite.color = Tint(unit.ownerId);
        go.AddComponent<BoxCollider2D>().isTrigger = true;
        go.name = $"Building_{unit.buildingType}_{unit.id}";

        // Apply rotation from server
        go.transform.rotation = Quaternion.Euler(0, 0, unit.rotation);
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


        //If only the position is changed, tween move the game object
        if (oldUnit.position.x != newUnit.position.x || oldUnit.position.y != newUnit.position.y)
        {
            //TODO: Tween move the game object
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
                buildingGameObjects[oldUnit.id].GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(newUnit.buildingType.ToString());
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