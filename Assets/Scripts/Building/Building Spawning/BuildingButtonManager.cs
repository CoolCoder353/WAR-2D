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

    public int tilesBuildingWillCover = 1;

    private float currentRotation = 0f; // Current rotation in degrees (0, 90, 180, 270)
    private bool isPlacing = false;

    [ClientCallback]
    public void Start()
    {

        ClientPlayer localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();

        localPlayer.onResponseFromCanBuildBuilding.AddListener(ResultFromCommand);

        localPlayer.onResponseFromTilesCovered.AddListener(ResponseFromTilesCovered);


        foreach (var button in buttons)
        {
            button.onClick.AddListener(() => OnButtonClicked(button));

            BuildingType buildingType = buildingTypes[buttons.IndexOf(button)];
            if (buildingType != BuildingType.None)
            {
                Sprite sprite = Resources.Load<Sprite>(buildingType.ToString());
                if (sprite != null)
                {
                    button.GetComponentInChildren<Image>().sprite = sprite;
                }
                else
                {
                    Debug.LogWarning($"Could not find sprite for building type {buildingType}");
                }
            }
        }

        if (buildingTypes.Count != buttons.Count)
        {
            Debug.LogError("Building types and buttons count do not match. Did you forget to assign a building type to a button?");
        }
    }
    [ClientCallback]
    public void Update()
    {
        if (!isPlacing) return;

        if (previewBuilding.activeInHierarchy)
        {
            Vector3 position = RoundVector3(UnitCommander.GetMouseWorldPosition());

            previewBuilding.transform.position = new Vector3((int)position.x, (int)position.y, 0);
            if (tilesBuildingWillCover % 2 == 1) //if 1^2 or 3^2 or 5^2 (odd number of tiles squared)
            {
                previewBuilding.transform.position += new Vector3(0.5f, 0.5f, 0);
            }

            SetBuildingPreviewColour(position);

            // Rotate building with R key
            if (Input.GetKeyDown(KeyCode.R))
            {
                currentRotation = (currentRotation + 90f) % 360f;
                previewBuilding.transform.rotation = Quaternion.Euler(0, 0, currentRotation);
                // Re-validate with new rotation
                SetBuildingPreviewColour(position);
            }
        }
        if (Input.GetMouseButtonDown(0) && previewBuilding.activeInHierarchy)
        {
            TrySpawnBuilding();
            previewBuilding.SetActive(false);
            previewBuilding.transform.position = new Vector3(0, 0, 0);
            previewBuilding.GetComponent<SpriteRenderer>().sprite = null;
            currentRotation = 0f;
            isPlacing = false;
        }
    }
    [Client]
    private void SetBuildingPreviewColour(Vector3 position)
    {
        //We can guess if the building will be valid or not based on the positions we know of from the ClientPlayer thing

        WorldStateManager.Instance.CanBuildBuildingCommand(new int2((int)position.x, (int)position.y), selectedBuildingType, currentRotation);
    }

    [Client]

    public void ResultFromCommand(bool result)
    {
        if (result)
        {
            previewBuilding.GetComponent<SpriteRenderer>().color = new Color(0.8f, 0.8f, 0.8f, 0.75f);
        }
        else
        {
            previewBuilding.GetComponent<SpriteRenderer>().color = new Color(1, 0, 0, 0.75f);
        }
    }

    [Client]
    public void ResponseFromTilesCovered(int tiles)
    {
        tilesBuildingWillCover = tiles;
    }

    [Client]
    private void TrySpawnBuilding()
    {
        Vector3 position = RoundVector3(UnitCommander.GetMouseWorldPosition());

        int2 convertedPosition = new int2((int)position.x, (int)position.y);

        if (selectedBuildingType == BuildingType.None)
        {
            Debug.LogError("Cannot place building - selectedBuildingType is None!");
            return;
        }

        // Place building through WorldStateManager (works for both HQ and regular buildings)
        WorldStateManager.Instance.TryAddBuilding(convertedPosition, selectedBuildingType, currentRotation);
    }
    [Client]
    private Vector3 RoundVector3(Vector3 vector)
    {
        if (tilesBuildingWillCover % 2 == 1) //if 1^2 or 3^2 or 5^2 (odd number of tiles squared)
        {
            vector -= new Vector3(0.5f, 0.5f, 0);
        }
        return new Vector3(Mathf.Round(vector.x), Mathf.Round(vector.y), Mathf.Round(vector.z));
    }

    [Client]
    public void OnButtonClicked(Button button)
    {
        int index = buttons.IndexOf(button);

        if (index < 0 || index >= buildingTypes.Count)
        {
            Debug.LogError($"Button not found in buttons list or index out of range. Index: {index}, BuildingTypes count: {buildingTypes.Count}");
            return;
        }

        selectedBuildingType = buildingTypes[index];

        if (selectedBuildingType == BuildingType.None)
        {
            Debug.LogError($"Selected building type is None! Index: {index}, Button: {button.name}");
            return;
        }

        Debug.Log($"Building button clicked: {selectedBuildingType} (index: {index})");
        WorldStateManager.Instance.GetTilesBuildingWillCoverCommand(new int2(0, 0), selectedBuildingType);
        SetupBuildingPreview();
        isPlacing = true;
    }

    [Client]
    private void SetupBuildingPreview()
    {
        previewBuilding.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(selectedBuildingType.ToString());
        previewBuilding.SetActive(true);
        currentRotation = 0f; // Reset rotation when selecting new building
        previewBuilding.transform.rotation = Quaternion.identity;
    }
}