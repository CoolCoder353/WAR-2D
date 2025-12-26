using System;
using System.Collections.Generic;
using DG.Tweening;
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

    public GameObject selectionBox;

    private ClientPlayer localPlayer;

    private Dictionary<int, GameObject> unitGameObjects = new Dictionary<int, GameObject>();
    private Dictionary<int, double> lastUnitAttackTimes = new Dictionary<int, double>();

    public Dictionary<int, GameObject> buildingGameObjects = new Dictionary<int, GameObject>();


    public int tilesBuildingWillCover = 1;

    [ClientCallback]
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;

            //Start the selection box as inactive
            selectionBox.SetActive(false);

            localPlayer = NetworkClient.connection.identity.GetComponent<ClientPlayer>();
            localPlayer.SetUnitHandles();
            localPlayer.SetBuildingHandles();
            localPlayer.onResponseFromTilesCovered.AddListener(ResponseFromTilesCovered);

        }
        else
        {
            Destroy(this);
        }
    }

    [Client]
    public void ResponseFromTilesCovered(int tiles)
    {
        tilesBuildingWillCover = tiles;
    }

    [ClientCallback]
    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        localPlayer.RemoveUnitHandles();
    }

    public static Vector3 GetMouseWorldPosition()
    {
        Vector3 mousePosition = Input.mousePosition;

        Vector3 truePosition = new Vector3(Camera.main.pixelWidth - mousePosition.x, Camera.main.pixelHeight - mousePosition.y, Camera.main.transform.position.z);

        Vector3 worldPosition = Camera.main.ScreenToWorldPoint(truePosition);

        worldPosition.z = 0;
        return worldPosition;
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
        if (Input.GetMouseButtonDown(0))
        {
            selectionBox.SetActive(true);
            Vector3 worldPosition = GetMouseWorldPosition();
            startcorner = new int2((int)worldPosition.x, (int)worldPosition.y);
            endcorner = startcorner;
            selectionBox.transform.position = new Vector3(startcorner.x, startcorner.y, 0);
            selectionBox.transform.localScale = new Vector3(0, 0, 1);
        }

        if (Input.GetMouseButton(0))
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
        if (Input.GetMouseButtonUp(0))
        {
            selectionBox.SetActive(false);
            Vector3 worldPosition = GetMouseWorldPosition();
            endcorner = new int2((int)worldPosition.x, (int)worldPosition.y);

        }

        if (Input.GetMouseButtonDown(1))
        {
            Vector3 worldPosition = GetMouseWorldPosition();
            int2 goal = new int2((int)worldPosition.x, (int)worldPosition.y);
            // Debug.Log($"Moving units in box {startcorner}, {endcorner} units to {goal.x},{goal.y} -> client side");
            WorldStateManager.Instance.CmdMoveUnits(goal, startcorner, endcorner);
        }

        //Get where the camera is looking at in the scene
        Vector3 cameraStart = Camera.main.ScreenToWorldPoint(new Vector3(Camera.main.pixelWidth, Camera.main.pixelHeight, Camera.main.transform.position.z));
        Vector3 cameraPositionEnd = Camera.main.ScreenToWorldPoint(new Vector3(0, 0, Camera.main.transform.position.z));

        //Get the corners of the camera
        Vector3 cameraCorner1 = new Vector3(cameraStart.x, cameraStart.y, 0) + new Vector3(-visualAdditionalRange.x, -visualAdditionalRange.y, 0);
        Vector3 cameraCorner2 = new Vector3(cameraPositionEnd.x, cameraPositionEnd.y, 0) + new Vector3(visualAdditionalRange.x, visualAdditionalRange.y, 0);

        int2 corner1 = new int2((int)cameraCorner1.x, (int)cameraCorner1.y);
        int2 corner2 = new int2((int)cameraCorner2.x, (int)cameraCorner2.y);

        // // Place the selection box at this point to show the box the server thinks the client can see for testing purposes

        // selectionBox.transform.position = (cameraCorner1 + cameraCorner2) / 2;
        // selectionBox.transform.localScale = new Vector3(Mathf.Abs(corner2.x - corner1.x), Mathf.Abs(corner2.y - corner1.y), 1);

        // TIM.Console.Log($"Updating client view to {corner1} {corner2}", TIM.MessageType.Network);
        // TIM.Console.Log($"Using WorldStateManager {WorldStateManager.Instance}", TIM.MessageType.Network);


        //Request from the server to update what the client can see for the next frame
        WorldStateManager.Instance.UpdateClientView(corner1, corner2);

        MoveUnits();
        VisualizeAttackingUnits();
    }


    [Client]
    private void VisualizeAttackingUnits()
    {
        foreach (ClientUnit unit in localPlayer.visuableUnits)
        {
            if (!lastUnitAttackTimes.ContainsKey(unit.id))
            {
                lastUnitAttackTimes[unit.id] = unit.lastAttackTime;
            }

            if (unit.targetId != -1 && unit.lastAttackTime > lastUnitAttackTimes[unit.id])
            {
                lastUnitAttackTimes[unit.id] = unit.lastAttackTime;

                unitGameObjects.TryGetValue(unit.id, out GameObject attackerObject);
                unitGameObjects.TryGetValue(unit.targetId, out GameObject enemyObject);

                if (attackerObject != null && enemyObject != null)
                {
                    GameObject bullet = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Destroy(bullet.GetComponent<Collider>());

                    Vector3 startPos = attackerObject.transform.position;
                    Vector3 endPos = enemyObject.transform.position;

                    bullet.transform.position = startPos;
                    bullet.transform.localScale = new Vector3(0.5f, 0.1f, 1);
                    bullet.GetComponent<Renderer>().material.color = Color.yellow;
                    bullet.GetComponent<Renderer>().material.renderQueue = 3000;

                    Vector3 direction = (endPos - startPos).normalized;
                    bullet.transform.right = direction;

                    bullet.transform.DOMove(endPos, 0.2f).SetEase(Ease.Linear).OnComplete(() => Destroy(bullet));
                }
            }
        }
    }


    //Goes through all visible units and makes sure the game objects are in the right place
    [Client]
    private void MoveUnits()
    {

        // Debug.LogWarning($"Moving the visable units of client '{localPlayer.nickname}', '{localPlayer.visuableUnits.Count}' units");
        foreach (ClientUnit unit in localPlayer.visuableUnits)
        {
            unitGameObjects.TryGetValue(unit.id, out GameObject go);
            if (go != null)
            {
                if (go.transform.position == new Vector3(unit.position.x, unit.position.y, go.transform.position.z))
                {
                    // Debug.LogWarning($"Unit {unit.id} is already in the right position");
                    continue;
                }

                //TODO: Tween move the game object
                DOTween.To(() => go.transform.position, x => go.transform.position = x, new Vector3(unit.position.x, unit.position.y, go.transform.position.z), 0.5f);
                // go.transform.position = new Vector3(unit.position.x, unit.position.y, go.transform.position.z);
            }
            else
            {
                Debug.LogError($"Unit game object not found for unit {unit.id}. When checking the insert hook it returned; Add: '{localPlayer.visuableUnits.OnAdd != null}', Insert: '{localPlayer.visuableUnits.OnInsert != null}', Set: '{localPlayer.visuableUnits.OnSet != null}', Remove: '{localPlayer.visuableUnits.OnRemove != null}', Clear: '{localPlayer.visuableUnits.OnClear != null}'");
            }
        }
    }

    //This is called when a unit is added or inserted into the list, returning the index of the list and the unit itself
    [Client]
    public void UnitListInsert(int index, ClientUnit unit)
    {
        // Debug.Log($"UnitListInsert called with unit id: '{unit.id}', sprite:  '{unit.spriteName}', position : '{unit.position}'");
        if (unitGameObjects.ContainsKey(unit.id))
        {
            Debug.LogError("UnitListInsert called with unit that already exists in the list. Unit id: " + unit.id);
            return;
        }

        //Create a new game object
        GameObject go = new GameObject();
        go.transform.position = new Vector3(unit.position.x, unit.position.y, 0);
        go.AddComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(unit.spriteName.ToString());
        unitGameObjects.Add(unit.id, go);
    }

    //Called when a unit is removed from the list, returning the index of the list and the old unit itself
    [Client]
    public void UnitListRemove(int index, ClientUnit OldUnit)
    {
        //Remove the game object from the list
        if (unitGameObjects.ContainsKey(OldUnit.id))
        {
            Destroy(unitGameObjects[OldUnit.id]);
            unitGameObjects.Remove(OldUnit.id);
        }
        if (lastUnitAttackTimes.ContainsKey(OldUnit.id))
        {
            lastUnitAttackTimes.Remove(OldUnit.id);
        }
    }

    //Called when the enitre list is cleared
    [Client]
    public void UnitListClear()
    {
        //Destroy all the game objects
        foreach (var item in unitGameObjects)
        {
            Destroy(item.Value);
        }
        unitGameObjects.Clear();
        lastUnitAttackTimes.Clear();
    }

    //Called when an item in the list is set to a new value
    //Note: I am not sure whether this will be called if something inside the object is changed, or if the object itself is changed
    [Client]
    public void UnitListSet(int index, ClientUnit oldUnit, ClientUnit newUnit)
    {

        if (oldUnit.id != newUnit.id)
        {
            Debug.LogError("UnitListSet called with different id for old and new unit");
            return;
        }

        //If the unit is not in the list, add it, this should not happen but just in case
        if (!unitGameObjects.ContainsKey(newUnit.id))
        {
            ////UnitListInsert(index, newUnit);
            return;
        }


        //If only the position is changed, tween move the game object
        if (oldUnit.position.x != newUnit.position.x || oldUnit.position.y != newUnit.position.y)
        {
            //TODO: Tween move the game object
            if (unitGameObjects.ContainsKey(oldUnit.id))
            {
                unitGameObjects[oldUnit.id].transform.position = new Vector3(newUnit.position.x, newUnit.position.y, 0);
            }
        }
        //If the sprite is changed, change the sprite
        if (oldUnit.spriteName != newUnit.spriteName)
        {
            if (unitGameObjects.ContainsKey(oldUnit.id))
            {
                unitGameObjects[oldUnit.id].GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(newUnit.spriteName.ToString());
            }
        }

    }




    #region Buildings
    //This is called when a unit is added or inserted into the list, returning the index of the list and the unit itself
    [Client]
    public void BuildingListInsert(int index, BuildingData unit)
    {
        WorldStateManager.Instance.GetTilesBuildingWillCoverCommand(new int2(0, 0), unit.buildingType);
        // Debug.Log($"UnitListInsert called with unit id: '{unit.id}', sprite:  '{unit.spriteName}', position : '{unit.position}'");
        if (buildingGameObjects.ContainsKey(unit.id))
        {
            Debug.LogError("BuildingListInsert called with building that already exists in the list. Building id: " + unit.id);
            return;
        }

        //Create a new game object
        GameObject go = new GameObject();
        go.transform.position = new Vector3(unit.position.x, unit.position.y, 0);

        if (tilesBuildingWillCover % 2 == 1) //if 1^2 or 3^2 or 5^2 (odd number of tiles squared)
        {
            go.transform.position += new Vector3(0.5f, 0.5f, 0);
        }


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
                buildingGameObjects[oldUnit.id].transform.position = new Vector3(newUnit.position.x, newUnit.position.y, 0);
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



    #endregion
}