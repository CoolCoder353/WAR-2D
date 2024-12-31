using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public class UnitCommander : MonoBehaviour
{
    public static UnitCommander Instance { get; private set; }

    public int2 visualAdditionalRange = new int2(5, 5);

    private int2 startcorner;
    private int2 endcorner;

    public GameObject selectionBox;

    private List<ClientUnit> unitsVisable = new List<ClientUnit>();


    private Dictionary<FixedString64Bytes, GameObject> unitGameObjects = new Dictionary<FixedString64Bytes, GameObject>();


    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(this);
        }
    }

    private Vector3 GetMouseWorldPosition()
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
        }

        if (Input.GetMouseButton(0))
        {

            Vector3 worldPosition = GetMouseWorldPosition();
            Vector3 startPosition = new Vector3(startcorner.x, startcorner.y, 0);
            Vector3 center = (worldPosition + startPosition) / 2;
            selectionBox.transform.position = center;
            Vector3 size = new Vector3(Mathf.Abs(worldPosition.x - startPosition.x), Mathf.Abs(worldPosition.y - startPosition.y), 1);
            selectionBox.transform.localScale = size;



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
            Debug.Log($"Moving units in box {startcorner}, {endcorner} units to {goal.x},{goal.y} -> client side");
            WorldStateManager.Instance.CmdMoveUnits(goal, startcorner, endcorner);
        }

        //Get where the camera is looking at in the scene
        Vector3 cameraPosition = Camera.main.transform.position;
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

        TIM.Console.Log($"Updating client view to {corner1} {corner2}", TIM.MessageType.Network);
        TIM.Console.Log($"Using WorldStateManager {WorldStateManager.Instance}", TIM.MessageType.Network);

        WorldStateManager.Instance.UpdateClientView(corner1, corner2);
    }

    [Client]
    private void UpdateUnit(ClientUnit unit)
    {
        Debug.Log($"Updating unit {unit}");
    }
    [Client]
    private void AddUnit(ClientUnit unit)
    {
        Debug.Log($"Adding unit {unit}");
    }
    [Client]
    private void RemoveUnit(ClientUnit unit)
    {
        Debug.Log($"Removing unit {unit}");
    }



    //This is called when a unit is added or inserted into the list, returning the index of the list and the unit itself
    [Client]
    public void UnitListInsert(int index, ClientUnit unit)
    {
        throw new NotImplementedException();
    }

    //Called when a unit is removed from the list, returning the index of the list and the old unit itself
    [Client]
    public void UnitListRemove(int index, ClientUnit OldUnit)
    {
        throw new NotImplementedException();
    }

    //Called when the enitre list is cleared
    [Client]
    public void UnitListClear()
    {
        throw new NotImplementedException();
    }

    //Called when an item in the list is set to a new value
    //Note: I am not sure whether this will be called if something inside the object is changed, or if the object itself is changed
    [Client]
    public void UnitListSet(int index, ClientUnit oldUnit, ClientUnit newUnit)
    {
        throw new NotImplementedException();
    }


}