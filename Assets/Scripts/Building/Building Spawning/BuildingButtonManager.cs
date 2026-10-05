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

    private int2 currentAnchor;

    private float currentRotation = 0f; // Current rotation in degrees (0, 90, 180, 270)
    private bool isPlacing = false;

    private int2 lastQueriedAnchor = new int2(int.MinValue, int.MinValue);
    private float lastQueriedRotation = float.MinValue;
    private float queryTimer;

    [ClientCallback]
    public void Start()
    {

        ClientPlayer localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();

        localPlayer.onResponseFromCanBuildBuilding.AddListener(ResultFromCommand);


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
            int2 size = WorldStateManager.GetBuildingSize(selectedBuildingType);
            Vector3 mouse = UnitCommander.GetMouseWorldPosition();
            currentAnchor = Footprint.SnapAnchor(new float2(mouse.x, mouse.y), size);
            float2 centre = Footprint.VisualCenter(currentAnchor, size);
            previewBuilding.transform.position = new Vector3(centre.x, centre.y, 0);
            SetBuildingPreviewColour();

            // Rotate building with R key
            if (Input.GetKeyDown(KeyCode.R))
            {
                currentRotation = (currentRotation + 90f) % 360f;
                previewBuilding.transform.rotation = Quaternion.Euler(0, 0, currentRotation);
                // Re-validate with new rotation
                SetBuildingPreviewColour();
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
    private void SetBuildingPreviewColour()
    {
        //We can guess if the building will be valid or not based on the positions we know of from the ClientPlayer thing

        // Change-gated and throttled: 10 Hz keeps an honest client below the server's 15/s refill,
        // so cursor-speed motion can never trip the command rate-limit kick.
        queryTimer += Time.unscaledDeltaTime;
        // int2's != yields bool2, so reduce it with math.any.
        if ((math.any(currentAnchor != lastQueriedAnchor) || currentRotation != lastQueriedRotation) && queryTimer >= 0.1f)
        {
            WorldStateManager.Instance.CanBuildBuildingCommand(currentAnchor, selectedBuildingType, currentRotation);
            lastQueriedAnchor = currentAnchor;
            lastQueriedRotation = currentRotation;
            queryTimer = 0f;
        }
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
    private void TrySpawnBuilding()
    {
        if (selectedBuildingType == BuildingType.None)
        {
            Debug.LogError("Cannot place building - selectedBuildingType is None!");
            return;
        }

        // Place building through WorldStateManager (works for both HQ and regular buildings)
        WorldStateManager.Instance.TryAddBuilding(currentAnchor, selectedBuildingType, currentRotation);
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
        lastQueriedAnchor = new int2(int.MinValue, int.MinValue);
        lastQueriedRotation = float.MinValue; // Sentinels guarantee the next change check differs.
        queryTimer = 1f; // Mature timer, so the first frame after selecting a building asks immediately.
    }
}