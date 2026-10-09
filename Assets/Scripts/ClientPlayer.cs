using Mirror;
using UnityEngine;


[System.Serializable]
public class ClientPlayer : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnNicknameChangedEvent))]
    public string nickname;

    public LobbySystem lobbySystem;


    [SyncVar]
    public bool hasPlacedHQ = false;

    /// <summary>The team this player picked in the lobby, or <see cref="TeamRules.NoTeam"/>. Set by the server owner.</summary>
    [SyncVar(hook = nameof(OnLobbyTeamChanged))]
    public int lobbyTeam = TeamRules.NoTeam;

    private void OnLobbyTeamChanged(int oldTeam, int newTeam)
    {
        if (lobbySystem != null) lobbySystem.UpdateTeamLabel(this);
    }

    /// <summary>True on the client whose player is the current server owner (lobby start button).</summary>
    [SyncVar(hook = nameof(OnServerOwnerChanged))]
    public bool isServerOwner;

    public UnityEngine.Events.UnityEvent<bool> onResponseFromCanBuildBuilding = new UnityEngine.Events.UnityEvent<bool>();

    private bool gameOverDeclared = false; // Flag to ensure game over is only declared once
    public bool drawDeclared = false; // Flag set when the draw screen RPC is received

    [Client]
    public override void OnStartClient()
    {
        base.OnStartClient();

        DontDestroyOnLoad(this);
        //Find the lobby system
        lobbySystem = FindAnyObjectByType<LobbySystem>();
        if (lobbySystem != null)
        {
            lobbySystem.AddClientPlayer(this, addNicknameListener: isLocalPlayer);
            if (isLocalPlayer) lobbySystem.SetStartButtonVisible(isServerOwner);
        }

        //Add the hook to the scene change event
        if (!isLocalPlayer) return;

    }

    [Client]
    public override void OnStopClient()
    {
        if (!isLocalPlayer) return;

        //TODO: Need to make sure we remove handles when the player disconnects, or the scene changes

        // RemoveUnitHandles();

        Destroy(this.gameObject);

    }



    [TargetRpc]
    public void TargetReceiveCanBuildBuildingResponse(NetworkConnection target, bool result)
    {
        onResponseFromCanBuildBuilding?.Invoke(result);
    }

    /// <summary>
    /// Current resources for this player (only updated for local player)
    /// </summary>
    public float currentResources { get; private set; } = 0f;

    /// <summary>
    /// TargetRpc to update the local player's resource count
    /// </summary>
    [TargetRpc]
    public void TargetUpdateResources(NetworkConnection target, float newResources)
    {
        currentResources = newResources;
        // You can add UI update logic here or use an event
        // For example: onResourcesChanged?.Invoke(newResources);
    }

    /// <summary>Whom this player attacks; bit i is the owner at <c>GameCore.PlayerOrder[i]</c> (local player only).</summary>
    public ushort AttackMask { get; private set; }

    /// <summary>Whom this player shares vision with (bits as <see cref="AttackMask"/>).</summary>
    public ushort ShareVisionMask { get; private set; }

    /// <summary>Who shares vision with this player (bits as <see cref="AttackMask"/>).</summary>
    public ushort SharedWithMe { get; private set; }

    /// <summary>Raised on the owning client when its diplomacy row arrives.</summary>
    public event System.Action DiplomacyChanged;

    /// <summary>The owner's own diplomacy row; sent at match start and whenever it changes.</summary>
    [TargetRpc]
    public void TargetDiplomacy(NetworkConnection target, ushort attackMask, ushort shareMask, ushort sharedWithMe)
    {
        AttackMask = attackMask;
        ShareVisionMask = shareMask;
        SharedWithMe = sharedWithMe;
        DiplomacyChanged?.Invoke();
    }

    /// <summary>Plays death explosions the server has filtered to this player's view.</summary>
    [TargetRpc]
    public void TargetPlayExplosions(NetworkConnection target, Vector2[] positions)
    {
        foreach (Vector2 p in positions) Effects.Explosion(p);
    }



    [Server]
    public NetworkConnectionToClient GetConnectionToClient()
    {
        return connectionToClient;
    }

    /// <summary>Enables or hides the start button on the owning client when ownership moves.</summary>
    private void OnServerOwnerChanged(bool oldValue, bool newValue)
    {
        if (isLocalPlayer && lobbySystem != null) lobbySystem.SetStartButtonVisible(newValue);
    }

    /// <summary>Shown to every player when all HQs were destroyed at the same time.</summary>
    [TargetRpc]
    public void RpcOnMatchDraw(NetworkConnectionToClient target)
    {
        if (gameOverDeclared) return;
        drawDeclared = true;
        GameObject prefab = Resources.Load<GameObject>("UI/LoseScreenUI");
        Canvas hud = FindAnyObjectByType<Canvas>();
        if (hud != null) hud.enabled = false;
        if (prefab != null)
        {
            GameObject screen = Instantiate(prefab);
            foreach (TMPro.TMP_Text text in screen.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (text.gameObject.name == "You Lost") text.text = "Draw";
            }
        }
        gameOverDeclared = true;
    }

    [ClientRpc]
    public void RPC_RemoveClientLobbyUI()
    {

        if (lobbySystem != null)
        {
            lobbySystem.CheckForLostPlayers();
        }
        else
        {
            Debug.LogError("Could not find lobby system to remove client player from.");
        }
    }

    [Server]
    public override void OnStartServer()
    {
        base.OnStartServer();
        nickname = $"Player {netId}";

        DontDestroyOnLoad(this);
    }

    [Command]
    public void CmdSetNickname(string requested)
    {
        if (!CommandGate.Allow(connectionToClient, nameof(CmdSetNickname))) return;
        if (GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Lobby) return;
        if (!CommandValidator.TrySanitizeNickname(requested, out string clean)) return;
        nickname = clean;
    }

    [Client]
    private void OnNicknameChangedEvent(string old, string newNickname)
    {

        if (newNickname == null || newNickname == string.Empty)
        {
            ////Debug.LogWarning("Nickname is null or empty");
            return;
        }
        if (lobbySystem == null)
        {
            ////Debug.LogWarning("LobbySystem not found when trying to update nickname.");
            return;
        }
        lobbySystem.UpdateClientPlayerNickname(this, newNickname);

    }




    /// <summary>
    /// ClientRpc called when a player wins.
    /// </summary>
    /// <param name="winner">The NetworkIdentity of the winner.</param>
    [TargetRpc]
    public void RpcOnPlayerWon(NetworkConnectionToClient winner)
    {
        // UI Implementation to handle this

        Debug.Log("Victory!");

        if (gameOverDeclared) return; // Prevent multiple win screens if multiple players are declared winners (shouldn't happen but just in case)



        GameObject winScreenPrefab = Resources.Load<GameObject>("UI/WinScreenUI");
        FindAnyObjectByType<Canvas>().enabled = false; // Disable the main game canvas to prevent interaction with it after losing
        if (winScreenPrefab != null)
        {
            Instantiate(winScreenPrefab);
        }
        else
        {
            Debug.LogError("Win screen prefab not found in Resources/UI/WinScreenUI");
        }
        gameOverDeclared = true;

    }

    /// <summary>
    /// ClientRpc called when a player loses.
    /// </summary>
    /// <param name="loser">The NetworkIdentity of the loser.</param>
    [TargetRpc]
    public void RpcOnPlayerLost(NetworkConnectionToClient loser)
    {
        // UI Implementation to handle this

        Debug.Log("Defeat!");

        if (gameOverDeclared) return; // Prevent multiple loss screens if multiple players are declared losers (shouldn't happen but just in case)

        GameObject lossScreenPrefab = Resources.Load<GameObject>("UI/LoseScreenUI");

        FindAnyObjectByType<Canvas>().enabled = false; // Disable the main game canvas to prevent interaction with it after losing
        if (lossScreenPrefab != null)
        {
            Instantiate(lossScreenPrefab);
        }
        else
        {
            Debug.LogError("Loss screen prefab not found in Resources/UI/LoseScreenUI");
        }
        gameOverDeclared = true;
    }

}