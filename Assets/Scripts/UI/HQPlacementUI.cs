using UnityEngine;
using UnityEngine.UI;
using Mirror;
using TMPro;

public class HQPlacementUI : MonoBehaviour
{
    [Header("UI References")]
    public GameObject placementPromptPanel;
    public GameObject normalBuildingPanel;
    public Button placeHQButton;
    public TMP_Text instructionText;

    [Header("Building Placement")]
    public BuildingButtonManager buildingButtonManager;

    private bool hasPlacedHQ = false;
    private bool isInPlacementMode = false;
    private int previousBuildingCount = 0;
    private bool subscribedToHQEvent = false;

    [Client]
    private void OnEnable()
    {
    //     if (placeHQButton != null)
    //     {
    //         placeHQButton.onClick.AddListener(OnPlaceHQButtonClicked);
    //     }

        if (normalBuildingPanel != null)
        {
            normalBuildingPanel.SetActive(false);
        }
    }

    [Client]
    private void OnDisable()
    {
        // if (placeHQButton != null)
        // {
        //     placeHQButton.onClick.RemoveListener(OnPlaceHQButtonClicked);
        // }
    }

    [Client]
    private void Update()
    {
        if (GameCore.Instance == null || placementPromptPanel == null)
            return;

        if (!subscribedToHQEvent && NetworkClient.localPlayer != null)
        {
            ClientPlayer localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
            if (localPlayer != null)
            {
                localPlayer.onHQPlaced.AddListener(OnHQPlacementConfirmed);
                subscribedToHQEvent = true;
            }
        }

        // Check if a building was placed while in placement mode
        if (isInPlacementMode && buildingButtonManager != null)
        {
            // Check if preview building is no longer active (building was placed)
            if (!buildingButtonManager.previewBuilding.activeInHierarchy)
            {
                hasPlacedHQ = true;
                isInPlacementMode = false;
                Debug.Log("HQ placement confirmed");
            }
        }

        // Show UI when in PlacingHQ state and haven't placed yet
        bool shouldShow = GameCore.Instance.CurrentState == GameState.PlacingHQ && !hasPlacedHQ;
        
        if (placementPromptPanel.activeSelf != shouldShow)
        {
            placementPromptPanel.SetActive(shouldShow);
        }

        if (normalBuildingPanel != null)
        {
            normalBuildingPanel.SetActive(!shouldShow);
        }

        // Update instruction text
        if (instructionText != null && shouldShow)
        {
            if (isInPlacementMode)
            {
                instructionText.text = "Click on the map to place your HQ!";
            }
            else
            {
                instructionText.text = "Place your Headquarters (HQ) to begin the game!";
            }
        }
    }

    // [Client]
    // private void OnPlaceHQButtonClicked()
    // {
    //     if (hasPlacedHQ)
    //     {
    //         Debug.LogWarning("HQ already placed!");
    //         return;
    //     }

    //     if (buildingButtonManager == null)
    //     {
    //         Debug.LogError("BuildingButtonManager reference is missing!");
    //         return;
    //     }

    //     // Find the Base building type button and trigger it
    //     int baseIndex = buildingButtonManager.buildingTypes.IndexOf(BuildingType.Base);
    //     if (baseIndex >= 0 && baseIndex < buildingButtonManager.buttons.Count)
    //     {
    //         // Simulate clicking the Base building button
    //         buildingButtonManager.OnButtonClicked(buildingButtonManager.buttons[baseIndex]);
            
    //         // Enter placement mode (don't mark as placed yet)
    //         isInPlacementMode = true;
            
    //         Debug.Log("HQ placement mode activated - click on map to place");
    //     }
    //     else
    //     {
    //         Debug.LogError("Base building type not found in BuildingButtonManager!");
    //     }
    // }

    [Client]
    public void OnHQPlacementConfirmed()
    {
        // Called externally when HQ placement is confirmed by server
        hasPlacedHQ = true;
        isInPlacementMode = false;
        if (placementPromptPanel != null)
        {
            placementPromptPanel.SetActive(false);
        }
    }
}
