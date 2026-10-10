using Mirror;
using UnityEngine;


[System.Serializable]
public class ClientPlayer : NetworkBehaviour
{
    [SyncVar]
    public string nickname;

    /// <summary>The player's palette colour (0–7), unique in the lobby (<see cref="GameCore.Cmd_SetColour"/>).</summary>
    [SyncVar]
    public int colourIndex;

    /// <summary>True once the player readied up in the lobby; any settings change clears it.</summary>
    [SyncVar]
    public bool ready;


    [SyncVar(hook = nameof(OnHasPlacedHQ))]
    public bool hasPlacedHQ = false;

    /// <summary>A new HQ means a new match: its end screen may show again.</summary>
    private void OnHasPlacedHQ(bool before, bool now)
    {
        if (!now) return;
        gameOverDeclared = false;
        drawDeclared = false;
    }

    /// <summary>The team this player picked in the lobby, or <see cref="TeamRules.NoTeam"/> (Solo). Set by the player (Teams mode) or the server owner.</summary>
    [SyncVar]
    public int lobbyTeam = TeamRules.NoTeam;

    /// <summary>True for the current server owner (the host: lobby settings and Start).</summary>
    [SyncVar]
    public bool isServerOwner;

    public UnityEngine.Events.UnityEvent<bool> onResponseFromCanBuildBuilding = new UnityEngine.Events.UnityEvent<bool>();

    private bool gameOverDeclared = false; // Flag to ensure game over is only declared once
    public bool drawDeclared = false; // Flag set when the draw screen RPC is received

    [Client]
    public override void OnStartClient()
    {
        base.OnStartClient();

        DontDestroyOnLoad(this);
    }

    /// <summary>Sends the nickname chosen on the Play screen.</summary>
    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        string wanted = WAR2D.UI.MenuModel.Nickname;
        if (!string.IsNullOrWhiteSpace(wanted)) CmdSetNickname(wanted);
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

    /// <summary>Raised on the client of the sender or the recipient of a gift: (from, to, amount).</summary>
    public event System.Action<int, int, float> GiftNoticed;

    /// <summary>A gift between this player and another. Only the sender and the recipient are told.</summary>
    [TargetRpc]
    public void TargetGift(NetworkConnection target, int fromOwner, int toOwner, float amount) => GiftNoticed?.Invoke(fromOwner, toOwner, amount);

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

    /// <summary>Raised on this client when its player's match ends: shown by the HUD's end screen.</summary>
    public static event System.Action<WAR2D.UI.EndResult> MatchEnded;

    /// <summary>Shows the local end screen once per match.</summary>
    private void EndMatch(WAR2D.UI.EndResult result)
    {
        if (gameOverDeclared) return;
        gameOverDeclared = true;
        MatchEnded?.Invoke(result);
    }

    /// <summary>Shown to every player when all HQs were destroyed at the same time.</summary>
    [TargetRpc]
    public void RpcOnMatchDraw(NetworkConnectionToClient target)
    {
        drawDeclared = true;
        EndMatch(WAR2D.UI.EndResult.Draw);
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

    /// <summary>
    /// ClientRpc called when a player wins.
    /// </summary>
    /// <param name="winner">The NetworkIdentity of the winner.</param>
    [TargetRpc]
    public void RpcOnPlayerWon(NetworkConnectionToClient winner) => EndMatch(WAR2D.UI.EndResult.Victory);

    /// <summary>
    /// ClientRpc called when a player loses.
    /// </summary>
    /// <param name="loser">The NetworkIdentity of the loser.</param>
    [TargetRpc]
    public void RpcOnPlayerLost(NetworkConnectionToClient loser) => EndMatch(WAR2D.UI.EndResult.Defeat);

}