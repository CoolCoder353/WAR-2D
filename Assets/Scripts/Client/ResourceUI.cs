using UnityEngine;
using TMPro;
using Mirror;

/// <summary>
/// Simple UI component to display the local player's current resources.
/// Attach this to a Text or TextMeshPro component in your UI.
/// </summary>
public class ResourceUI : MonoBehaviour
{
    private TMP_Text textComponent;
    private ClientPlayer localPlayer;

    [Client]
    private void Start()
    {
        textComponent = GetComponent<TMP_Text>();
        
        if (textComponent == null)
        {
            Debug.LogError("ResourceUI: No Text component found on this GameObject!");
            enabled = false;
            return;
        }

        // Find the local player
        if (NetworkClient.localPlayer != null)
        {
            localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        }
    }

    [Client]
    private void Update()
    {
        if (localPlayer == null && NetworkClient.localPlayer != null)
        {
            localPlayer = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        }

        if (localPlayer != null && textComponent != null)
        {
            textComponent.text = $"Resources: {localPlayer.currentResources:F0}";
        }
    }
}
