using UnityEngine;
using UnityEngine.UI;
using Mirror;
using TMPro;
using System.Collections;

public class LossScreenUI : MonoBehaviour
{
    [Header("UI References")]
    public GameObject lossPanel;
    public TMP_Text lossMessageText;
    public Button spectateButton;
    public Button leaveMatchButton;

    [Client]
    private void OnEnable()
    {
        if (GameCore.Instance != null)
        {
            GameCore.Instance.OnLocalPlayerLost += ShowLossScreen;
        }

        if (spectateButton != null)
        {
            spectateButton.onClick.AddListener(OnSpectateClicked);
        }

        if (leaveMatchButton != null)
        {
            leaveMatchButton.onClick.AddListener(OnLeaveMatchClicked);
        }
    }

    [Client]
    private void OnDisable()
    {
        if (GameCore.Instance != null)
        {
            GameCore.Instance.OnLocalPlayerLost -= ShowLossScreen;
        }

        if (spectateButton != null)
        {
            spectateButton.onClick.RemoveListener(OnSpectateClicked);
        }

        if (leaveMatchButton != null)
        {
            leaveMatchButton.onClick.RemoveListener(OnLeaveMatchClicked);
        }
    }

    [Client]
    private void Start()
    {
        if (lossPanel != null)
        {
            lossPanel.SetActive(false);
        }
    }

    [Client]
    private void ShowLossScreen()
    {
        if (lossPanel != null)
        {
            lossPanel.SetActive(true);
        }

        if (lossMessageText != null)
        {
            lossMessageText.text = "DEFEATED!\n\nYour headquarters has been destroyed.";
        }

        Debug.Log("Loss screen displayed");
    }

    [Client]
    private void OnSpectateClicked()
    {
        // Just hide the panel and allow player to watch
        if (lossPanel != null)
        {
            lossPanel.SetActive(false);
        }

        Debug.Log("Entering spectator mode");
    }

    [Client]
    private void OnLeaveMatchClicked()
    {
        // Disconnect from server and return to main menu with a delay to ensure cleanup
        StartCoroutine(DisconnectAndLoadLobby());
    }

    [Client]
    private IEnumerator DisconnectAndLoadLobby()
    {
        if (NetworkClient.isConnected)
        {
            NetworkClient.Shutdown();
            // Wait a frame to ensure network cleanup completes
            yield return new WaitForSeconds(0.5f);
        }

        // Load lobby scene (adjust scene name as needed)
        UnityEngine.SceneManagement.SceneManager.LoadScene("Lobby");
    }
}
