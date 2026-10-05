using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Shows the HQ placement prompt, placement progress and the server-owned countdown.</summary>
public class HQPlacementUI : MonoBehaviour
{
    [Header("UI References")]
    public GameObject placementPromptPanel;
    public GameObject normalBuildingPanel;
    public TMP_Text instructionText;
    public TMP_Text progressText;

    [Header("Building Placement")]
    public BuildingButtonManager buildingButtonManager;

    private void Update()
    {
        GameCore core = GameCore.Instance;
        ClientPlayer local = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
        if (core == null || local == null) return;

        bool showPrompt = core.CurrentState == GameState.PlacingHQ || core.CurrentState == GameState.Countdown;
        if (placementPromptPanel.activeSelf != showPrompt) placementPromptPanel.SetActive(showPrompt);
        if (normalBuildingPanel != null && normalBuildingPanel.activeSelf == showPrompt) normalBuildingPanel.SetActive(!showPrompt);
        if (!showPrompt) return;

        if (buildingButtonManager != null)
        {
            bool canPlace = core.CurrentState == GameState.PlacingHQ && !local.hasPlacedHQ;
            foreach (Button button in buildingButtonManager.buttons) button.interactable = canPlace;
        }

        if (instructionText != null) instructionText.text = "Place your Headquarters (HQ) to begin the game!";
        if (progressText == null) return;

        if (core.CurrentState == GameState.Countdown)
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt((float)(core.CountdownEndTime - NetworkTime.time)));
            progressText.text = $"All players have placed their HQ! Starting in {seconds}...";
        }
        else if (local.hasPlacedHQ)
        {
            int remaining = 0;
            foreach (ClientPlayer p in FindObjectsByType<ClientPlayer>(FindObjectsSortMode.None))
            {
                if (!p.hasPlacedHQ) remaining++;
            }
            progressText.text = $"Waiting for {remaining} player(s) to place their HQ...";
        }
        else
        {
            progressText.text = string.Empty;
        }
    }
}
