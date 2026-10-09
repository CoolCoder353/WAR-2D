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

    /// <summary>Resources the local player earned during the last second (local player only).</summary>
    public float incomePerSecond { get; private set; }

    /// <summary>Upkeep the local player paid during the last second (local player only).</summary>
    public float upkeepPerSecond { get; private set; }

    /// <summary>The owning player's own resources and last second's income and upkeep. Private: never a SyncVar.</summary>
    [TargetRpc]
    public void TargetUpdateResources(NetworkConnection target, float newResources, float income, float upkeep)
    {
        currentResources = newResources;
        incomePerSecond = income;
        upkeepPerSecond = upkeep;
    }

    private readonly System.Collections.Generic.Dictionary<int, int> spawnerQueues = new System.Collections.Generic.Dictionary<int, int>();

    /// <summary>Units queued on one of the local player's spawners, as last told by the server (0 when unknown).</summary>
    public int SpawnerQueue(int buildingId) => spawnerQueues.TryGetValue(buildingId, out int count) ? count : 0;

    /// <summary>Raised when a spawner's queue count arrives: (building id, count).</summary>
    public event System.Action<int, int> SpawnerQueueChanged;

    /// <summary>The number of the local player's live units in each squad (0–9), as last told by the server.</summary>
    public int[] SquadCounts { get; private set; } = new int[WAR2D.Sim.Squads.Count];

    /// <summary>One of the owner's spawners changed its queue. Private: only the owner learns it.</summary>
    [TargetRpc]
    public void TargetSpawnerQueue(NetworkConnection target, int buildingId, int count)
    {
        if (count <= 0) spawnerQueues.Remove(buildingId);
        else spawnerQueues[buildingId] = count;
        SpawnerQueueChanged?.Invoke(buildingId, count);
    }

    /// <summary>The owner's live units per squad. Private: only the owner learns it.</summary>
    [TargetRpc]
    public void TargetSquadCounts(NetworkConnection target, int[] counts)
    {
        if (counts == null || counts.Length != WAR2D.Sim.Squads.Count) return;
        SquadCounts = counts;
    }

    /// <summary>Raised on the owning client when alerts arrive.</summary>
    public event System.Action<Alert[]> AlertsReceived;

    /// <summary>The owner's own alerts. Private: each player is sent only alerts about their own entities and choices made towards them.</summary>
    [TargetRpc]
    public void TargetAlerts(NetworkConnection target, Alert[] alerts)
    {
        if (alerts != null && alerts.Length > 0) AlertsReceived?.Invoke(alerts);
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

    /// <summary>Hides the match HUD under an end screen, so it can't be used after the match.</summary>
    private static void HideHud()
    {
        WAR2D.UI.HudController hud = FindAnyObjectByType<WAR2D.UI.HudController>();
        if (hud != null) hud.gameObject.SetActive(false);
    }

    /// <summary>Shown to every player when all HQs were destroyed at the same time.</summary>
    [TargetRpc]
    public void RpcOnMatchDraw(NetworkConnectionToClient target)
    {
        if (gameOverDeclared) return;
        drawDeclared = true;
        GameObject prefab = Resources.Load<GameObject>("UI/LoseScreenUI");
        HideHud();
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
        HideHud();
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

        HideHud();
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