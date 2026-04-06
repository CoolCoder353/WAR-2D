using UnityEngine;
using UnityEngine.UI;
using Mirror;
using TMPro;
using System.Collections;

public class WinScreenUI : MonoBehaviour
{
    [Header("UI References")]
    public GameObject winPanel;
    public TMP_Text winMessageText;
    public Button returnToLobbyButton;

    [Client]
    private void OnEnable()
    {
        if (GameCore.Instance != null)
        {
            GameCore.Instance.OnLocalPlayerWon += ShowWinScreen;
        }

        if (returnToLobbyButton != null)
        {
            returnToLobbyButton.onClick.AddListener(OnReturnToLobbyClicked);
        }
    }

    [Client]
    private void OnDisable()
    {
        if (GameCore.Instance != null)
        {
            GameCore.Instance.OnLocalPlayerWon -= ShowWinScreen;
        }

        if (returnToLobbyButton != null)
        {
            returnToLobbyButton.onClick.RemoveListener(OnReturnToLobbyClicked);
        }
    }

    [Client]
    private void Start()
    {
        if (winPanel != null)
        {
            winPanel.SetActive(false);
        }
    }

    [Client]
    private void ShowWinScreen()
    {
        if (winPanel != null)
        {
            winPanel.SetActive(true);
        }

        if (winMessageText != null)
        {
            winMessageText.text = "VICTORY!\n\nYou are the last commander standing!";
        }

        Debug.Log("Win screen displayed");
    }

    [Client]
    private void OnReturnToLobbyClicked()
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
