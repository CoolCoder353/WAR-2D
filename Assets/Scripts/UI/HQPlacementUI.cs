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
    private bool subscribedToHQEvent = false;

    [Client]
    private void OnEnable()
    {
        if (normalBuildingPanel != null)
        {
            normalBuildingPanel.SetActive(false);
        }
    }

    [Client]
    private void OnDisable()
    {
        UnsubscribeFromHQEvent();
    }



    [Client]
    private void Update()
    {
        if (GameCore.Instance == null || placementPromptPanel == null)
            return;

        // Subscribe to HQ event only once and only when needed
        if (!subscribedToHQEvent && NetworkClient.localPlayer != null)
        {
            SubscribeToHQEvent();
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
            instructionText.text = "Place your Headquarters (HQ) to begin the game!";
        }
    }

    [Client]
    private void SubscribeToHQEvent()
    {
        ClientPlayer localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        if (localPlayer != null)
        {
            Debug.Log("Subscribing to OnHQPlaced event for our local player");
            localPlayer.onHQPlaced.AddListener(OnHQPlacementConfirmed);
            subscribedToHQEvent = true;
        }
    }

    [Client]
    private void UnsubscribeFromHQEvent()
    {
        if (subscribedToHQEvent && NetworkClient.localPlayer != null)
        {
            ClientPlayer localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
            if (localPlayer != null)
            {
                localPlayer.onHQPlaced.RemoveListener(OnHQPlacementConfirmed);
                subscribedToHQEvent = false;
            }
        }
    }

    [Client]
    public void OnHQPlacementConfirmed()
    {
        // Called externally when HQ placement is confirmed by server
        hasPlacedHQ = true;
        if (placementPromptPanel != null)
        {
            placementPromptPanel.SetActive(false);
        }
    }
}
