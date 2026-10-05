using UnityEngine;
using UnityEngine.UI;

/// <summary>Wires main-menu buttons to GameManager through serialized references (no tag lookups).</summary>
public class MainMenuUI : MonoBehaviour
{
    [SerializeField] private Button hostButton;
    [SerializeField] private Button joinButton;
    [SerializeField] private Button leaveButton;
    [SerializeField] private Button quitButton;
    [SerializeField] private LobbySystem lobby;

    private void Start()
    {
        GameManager manager = GameManager.Instance;
        if (hostButton != null) hostButton.onClick.AddListener(manager.HostServer);
        if (joinButton != null) joinButton.onClick.AddListener(Join);
        if (leaveButton != null) leaveButton.onClick.AddListener(manager.LeaveLobby);
        if (quitButton != null) quitButton.onClick.AddListener(manager.QuitGame);
        if (lobby != null && lobby.joinIPButton != null) lobby.joinIPButton.GetComponent<Button>().onClick.AddListener(Join);
    }

    private void Join()
    {
        if (lobby != null && lobby.joinIPInputField != null) GameManager.Instance.ConnectToServer(lobby.joinIPInputField.text);
    }
}
