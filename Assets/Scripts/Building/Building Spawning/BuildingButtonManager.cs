using System;
using System.Collections.Generic;
using Mirror;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UI;


public class BuildingButtonManager : MonoBehaviour
{
    public List<Button> buttons = new List<Button>();

    public List<BuildingType> buildingTypes = new List<BuildingType>();

    public GameObject previewBuilding;

    public BuildingType selectedBuildingType;

    public Dictionary<BuildingType, float2> buildingSizes = new Dictionary<BuildingType, float2>();

    [ClientCallback]
    public void Start()
    {
        foreach (var button in buttons)
        {
            button.onClick.AddListener(() => OnButtonClicked(button));

            button.GetComponentInChildren<Image>().sprite = Resources.Load<Sprite>(buildingTypes[buttons.IndexOf(button)].ToString());
        }

        if (buildingTypes.Count != buttons.Count)
        {
            Debug.LogError("Building types and buttons count do not match. Did you forget to assign a building type to a button?");
        }
    }
    [ClientCallback]
    public void Update()
    {
        if (previewBuilding.activeInHierarchy)
        {
            Vector3 position = RoundVector3(UnitCommander.GetMouseWorldPosition());

            previewBuilding.transform.position = new Vector3((int)position.x, (int)position.y, 0);

            SetBuildingPreviewColour(position);
        }
        if (Input.GetMouseButtonDown(0) && previewBuilding.activeInHierarchy)
        {
            TrySpawnBuilding();
            previewBuilding.SetActive(false);
            previewBuilding.transform.position = new Vector3(0, 0, 0);
            previewBuilding.GetComponent<SpriteRenderer>().sprite = null;

        }
    }

    private void SetBuildingPreviewColour(Vector3 position)
    {
        //We can guess if the building will be valid or not based on the positions we know of from the ClientPlayer thing

        ClientPlayer clientPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();


        if (clientPlayer != null)
        {
            foreach (BuildingData building in clientPlayer.visuableBuildings)
            {

                float2 size = GetBuildingSizeInUnits(building.buildingType, buildingSizes);

                if (!buildingSizes.ContainsKey(building.buildingType))
                {
                    buildingSizes.Add(building.buildingType, size);
                }

                float2 lowerBounds = new float2(building.position.x - size.x / 2, building.position.y - size.y / 2);
                float2 upperBounds = new float2(building.position.x + size.x / 2, building.position.y + size.y / 2);

                bool isBuildinginWall = CheckIfBuildingInWall(building.buildingType, position, size);

                if (isBuildinginWall || (position.x >= lowerBounds.x && position.x <= upperBounds.x && position.y >= lowerBounds.y && position.y <= upperBounds.y))
                {
                    Debug.Log($"Building {building.buildingType} is in the way of the preview building at {position} (IsBuildingInWall: {isBuildinginWall})");
                    previewBuilding.GetComponent<SpriteRenderer>().color = new Color(1, 0, 0, 0.75f);
                    return;
                }

            }
            previewBuilding.GetComponent<SpriteRenderer>().color = new Color(0.8f, 0.8f, 0.8f, 0.75f);
        }
        else
        {
            Debug.LogError("ClientPlayer is null");
        }
    }

    private bool CheckIfBuildingInWall(BuildingType buildingType, Vector3 position, float2 size)
    {
        Tilemap WalkableTilemap = WorldStateManager.Instance.WalkableTilemap;

        int2 tilesize = new int2(Mathf.CeilToInt(WalkableTilemap.cellSize.x), Mathf.CeilToInt(WalkableTilemap.cellSize.y));

        int2 realSize = new int2(Mathf.CeilToInt(size.x) / tilesize.x, Mathf.CeilToInt(size.y) / tilesize.y);

        int2 realPos = new int2(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.y));
        int2 startPos = realPos - new int2(Mathf.FloorToInt(realSize.x / 2), Mathf.FloorToInt(realSize.y / 2));
        for (int i = 0; i < realSize.x; i++)
        {
            for (int j = 0; j < realSize.y; j++)
            {
                int2 checkPosition = new int2(Mathf.RoundToInt(startPos.x + i), Mathf.RoundToInt(startPos.y + j));
                Debug.Log($"Checking tile at {checkPosition} for building {buildingType}");
                if (!WorldStateManager.Instance.GetTile(checkPosition).isWalkable || WorldStateManager.Instance.GetTile(checkPosition).isUsed)
                {
                    Debug.LogWarning($"Tile at {checkPosition} is not walkable or is used for building {buildingType}, iswalkable: {WorldStateManager.Instance.GetTile(checkPosition).isWalkable}, isused: {WorldStateManager.Instance.GetTile(checkPosition).isUsed}");
                    return true;
                }
            }
        }
        return false;
    }

    private static float2 GetBuildingSizeInUnits(BuildingType buildingType, Dictionary<BuildingType, float2> cachedBuildingSizes = null)
    {
        if (cachedBuildingSizes != null && cachedBuildingSizes.TryGetValue(buildingType, out float2 size))
        {
            return size;
        }
        Sprite sprite = Resources.Load<Sprite>(buildingType.ToString());
        if (sprite == null)
        {
            Debug.LogError($"Could not find sprite for building type {buildingType}");
            return new float2(1, 1);
        }
        return new float2(sprite.rect.width / sprite.pixelsPerUnit, sprite.rect.height / sprite.pixelsPerUnit);

    }


    [Client]
    private void TrySpawnBuilding()
    {
        Vector3 position = RoundVector3(UnitCommander.GetMouseWorldPosition());
        int2 convertedPosition = new int2((int)position.x, (int)position.y);

        //        Debug.Log($"Trying to spawn building at {convertedPosition} of type {selectedBuildingType} where the preview building is at {previewBuilding.transform.position} -> client");

        WorldStateManager.Instance.TryAddBuilding(convertedPosition, selectedBuildingType);
    }

    private static Vector3 RoundVector3(Vector3 vector)
    {
        return new Vector3(Mathf.Round(vector.x), Mathf.Round(vector.y), Mathf.Round(vector.z));
    }

    [Client]
    public void OnButtonClicked(Button button)
    {
        selectedBuildingType = buildingTypes[buttons.IndexOf(button)];
        SetupBuildingPreview();
    }

    [Client]
    private void SetupBuildingPreview()
    {
        previewBuilding.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(selectedBuildingType.ToString());
        previewBuilding.SetActive(true);
    }
}