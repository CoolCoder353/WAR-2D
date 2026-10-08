using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public class UnitCommander : NetworkBehaviour
{
    public static UnitCommander Instance { get; private set; }

    public int2 visualAdditionalRange = new int2(5, 5);

    private int2 startcorner;
    private int2 endcorner;
    private bool selecting;

    private int2 lastSentCorner1 = new int2(int.MinValue, int.MinValue);
    private int2 lastSentCorner2 = new int2(int.MinValue, int.MinValue);
    private float viewSendTimer;

    public GameObject selectionBox;

    private ClientPlayer localPlayer;


    public Dictionary<int, GameObject> buildingGameObjects = new Dictionary<int, GameObject>();


    [ClientCallback]
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;

            //Start the selection box as inactive
            selectionBox.SetActive(false);

            localPlayer = NetworkClient.connection.identity.GetComponent<ClientPlayer>();
            localPlayer.SetBuildingHandles();
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
            Instance = null;
        }
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


        //Mouse down, start selection
        if (GameInput.Select.WasPressedThisFrame() && !GameInput.PointerOverUI)
        {
            selecting = true;
            selectionBox.SetActive(true);
            Vector3 worldPosition = GetMouseWorldPosition();
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

        }

        if (GameInput.Command.WasPressedThisFrame() && !GameInput.PointerOverUI)
        {
            Vector3 worldPosition = GetMouseWorldPosition();
            int2 goal = new int2((int)worldPosition.x, (int)worldPosition.y);
            // Debug.Log($"Moving units in box {startcorner}, {endcorner} units to {goal.x},{goal.y} -> client side");
            WorldStateManager.Instance.CmdMoveUnits(goal, startcorner, endcorner);
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

        go.AddComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(unit.buildingType.ToString());
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
        else
        {
            Debug.LogWarning($"No client script found for building type {unit.buildingType}");
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