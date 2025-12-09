using UnityEngine;
using UnityEngine.UI;
using Mirror;
using TMPro;
using System.Collections;

public class HQPlacementUI : MonoBehaviour
{
    [Header("UI References")]
    public GameObject placementPromptPanel;
    public GameObject normalBuildingPanel;
    public TMP_Text instructionText;
    public TMP_Text progressText; // Shows "X players still placing HQ"

    [Header("Building Placement")]
    public BuildingButtonManager buildingButtonManager;

    private ClientPlayer localPlayer;

    public bool hideScreen = false;

    [Client]
    private void OnEnable()
    {
        if (normalBuildingPanel != null)
        {
            normalBuildingPanel.SetActive(false);
        }

        localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
    }



    [Client]
    private void Update()
    {
        if (GameCore.Instance == null || placementPromptPanel == null)
            return;

        // Get local player reference
        if (localPlayer == null && NetworkClient.localPlayer != null)
        {
            localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        }

        if (localPlayer == null)
            return;

        // Show UI when in PlacingHQ state and haven't placed yet
        bool shouldShow = !hideScreen;

        if (placementPromptPanel.activeSelf != shouldShow)
        {
            placementPromptPanel.SetActive(shouldShow);
        }

        if (normalBuildingPanel != null)
        {
            normalBuildingPanel.SetActive(!shouldShow);
        }


        //Set the building buttons interactable state
        if (localPlayer.hasPlacedHQ && buildingButtonManager != null)
        {
            foreach (Button btn in buildingButtonManager.buttons)
            {
                btn.interactable = false;
            }
        }
        else
        {
            if (buildingButtonManager != null)
            {
                foreach (Button btn in buildingButtonManager.buttons)
                {
                    btn.interactable = true;
                }
            }
        }

        // Update instruction text
        if (instructionText != null && shouldShow)
        {
            instructionText.text = "Place your Headquarters (HQ) to begin the game!\n";
        }

        // Update progress text (count remaining players)
        if (progressText != null && localPlayer.hasPlacedHQ)
        {
            int remainingPlayers = CountRemainingPlayersPlacingHQ();
            if (remainingPlayers > 0)
            {
                progressText.text = $"Waiting for {remainingPlayers} player(s) to place their HQ...";
            }
            else
            {
                progressText.text = "All players have placed HQ! Starting countdown...";
                StartCoroutine(StartCountdownToStartGame(5f));

            }
        }
    }


    [Client]
    private IEnumerator StartCountdownToStartGame(float countdownTime)
    {
        float timer = countdownTime;

        while (timer > 0)
        {
            if (progressText != null)
            {
                progressText.text = $"All players have placed HQ! Starting game in {Mathf.CeilToInt(timer)} seconds...";
            }

            yield return new WaitForSeconds(1f);
            timer -= 1f;
        }

        // Notify server to start the game
        if (localPlayer != null)
        {
            GameCore.Instance.Cmd_ReadyToStartGame();
            hideScreen = true;
        }
    }

    /// <summary>
    /// Counts how many players still need to place their HQ.
    /// Only works on clients that have access to the local player.
    /// </summary>
    [Client]
    private int CountRemainingPlayersPlacingHQ()
    {
        int remaining = 0;

        // Iterate through all players in ServerPlayers
        foreach (var clientPlayer in FindObjectsByType<ClientPlayer>(FindObjectsSortMode.None))
        {
            if (clientPlayer != null && !clientPlayer.hasPlacedHQ)
            {
                remaining++;
            }
        }

        Debug.Log($"Players remaining to place HQ: {remaining}");

        return remaining;
    }
}

