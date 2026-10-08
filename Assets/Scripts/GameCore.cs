using System.Collections.Generic;
using System.Linq;
using Config;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameState
{
    /// <summary>Players are in the lobby waiting for the game to start.</summary>
    Lobby,
    /// <summary>Players are placing their Headquarters (HQ).</summary>
    PlacingHQ,
    /// <summary>Countdown before the game officially starts.</summary>
    Countdown,
    /// <summary>The main gameplay loop is active.</summary>
    Playing,
    /// <summary>The game has ended.</summary>
    GameOver
}

/// <summary>
/// Server-authoritative match state: players, ownership, the state machine and outcomes.
/// </summary>
public class GameCore : NetworkBehaviour
{
    public static GameCore Instance { get; private set; }

    [SyncVar]
    public GameState CurrentState = GameState.Lobby;

    /// <summary>NetworkTime.time at which Countdown ends (valid while CurrentState == Countdown).</summary>
    [SyncVar]
    public double CountdownEndTime;

    /// <summary>Server-only player records, keyed by player object identity.</summary>
    public Dictionary<NetworkIdentity, ServerPlayer> ServerPlayers = new Dictionary<NetworkIdentity, ServerPlayer>();

    public IEnumerable<ServerPlayer> serverPlayers => ServerPlayers.Values;

    /// <summary>Players present when Playing began (used for the win rule).</summary>
    public int MatchStartPlayerCount { get; private set; }

    /// <summary>Owner ids in match order, synced to clients: an owner's position here picks its colour.</summary>
    public readonly SyncList<int> PlayerOrder = new SyncList<int>();

    private NetworkConnectionToClient serverOwner;

    private const string LobbyScene = "Main_Menu";
    private const float ResourceSyncInterval = 0.1f;
    private float resourceSyncTimer;

    public void Awake()
    {
        if (Instance != null)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(this);
    }

    [Server]
    public override void OnStopServer()
    {
        base.OnStopServer();
        ServerPlayers.Clear();
        serverOwner = null;
        CurrentState = GameState.Lobby;
        MatchStartPlayerCount = 0;
    }

    [ServerCallback]
    public void LateUpdate()
    {
        resourceSyncTimer += Time.deltaTime;
        if (resourceSyncTimer < ResourceSyncInterval) return;
        resourceSyncTimer = 0f;

        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            ServerPlayer player = entry.Value;
            if (!player.ResourcesDirty || entry.Key == null) continue;
            entry.Key.GetComponent<ClientPlayer>().TargetUpdateResources(player.connection, player.Resources);
            player.MarkSynced();
        }
    }

    [ServerCallback]
    public void Update()
    {
        if (CurrentState == GameState.Countdown && NetworkTime.time >= CountdownEndTime)
        {
            CurrentState = GameState.Playing;
        }
    }

    // ---------- Players and ownership ----------

    [Server]
    public void AddPlayer(NetworkConnectionToClient conn, float startingResources)
    {
        ServerPlayers[conn.identity] = new ServerPlayer(conn, startingResources);
        if (serverOwner == null) SetServerOwner(conn);
    }

    [Server]
    public void OnPlayerLeave(NetworkConnectionToClient conn)
    {
        if (conn.identity == null || !ServerPlayers.Remove(conn.identity)) return;
        CommandGate.Forget(conn.connectionId);

        ClientPlayer leaving = conn.identity.GetComponent<ClientPlayer>();
        if (WorldStateManager.Instance != null)
        {
            WorldStateManager.Instance.RemovePlayerView(leaving);
            WorldStateManager.Instance.KillAllEntitiesOwnedBy((int)conn.identity.netId);
        }

        foreach (NetworkIdentity remaining in ServerPlayers.Keys)
        {
            remaining.GetComponent<ClientPlayer>().RPC_RemoveClientLobbyUI();
        }

        if (serverOwner == conn)
        {
            serverOwner = null;
            if (conn is LocalConnectionToClient)
            {
                // The host is leaving: the server goes with it.
                return;
            }
            if (ServerPlayers.Count > 0) SetServerOwner(ServerPlayers.Values.First().connection);
        }

        if (ServerPlayers.Count == 0)
        {
            ResetToLobby();
        }
        else if (CurrentState == GameState.PlacingHQ)
        {
            CheckHQPlacementProgress();
        }
    }

    [Server]
    public void SetServerOwner(NetworkConnectionToClient conn)
    {
        if (serverOwner != null && serverOwner.identity != null)
        {
            serverOwner.identity.GetComponent<ClientPlayer>().isServerOwner = false;
        }
        serverOwner = conn;
        conn.identity.GetComponent<ClientPlayer>().isServerOwner = true;
        Debug.Log($"Player {conn.identity.netId} is now the server owner");
    }

    [Server]
    public bool IsServerOwner(NetworkConnectionToClient conn) => conn != null && serverOwner == conn;

    [Server]
    public ServerPlayer GetServerPlayerById(int ownerId)
    {
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key != null && entry.Key.netId == (uint)ownerId) return entry.Value;
        }
        return null;
    }

    // ---------- State machine ----------

    [Command(requiresAuthority = false)]
    public void Cmd_StartGame(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_StartGame))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby || !ConfigLoader.IsValid) return;

        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            entry.Value.state = PlayerState.Playing;
            entry.Key.GetComponent<ClientPlayer>().hasPlacedHQ = false;
        }
        // Captured at launch so a departure before Playing can't stop the survivor from winning.
        MatchStartPlayerCount = ServerPlayers.Count;
        PlayerOrder.Clear();
        foreach (NetworkIdentity identity in ServerPlayers.Keys) PlayerOrder.Add(BuildingData.UIntToInt(identity.netId));
        CurrentState = GameState.PlacingHQ;
        GameManager.Instance.ServerChangeScene(ConfigLoader.LoadConfig().Match.Scene);
    }

    [Server]
    public void CheckHQPlacementProgress()
    {
        int remaining = ServerPlayers.Keys.Count(id => !id.GetComponent<ClientPlayer>().hasPlacedHQ);
        RpcUpdateHQPlacementProgress(remaining);

        if (remaining == 0 && CurrentState == GameState.PlacingHQ)
        {
            CurrentState = GameState.Countdown;
            CountdownEndTime = NetworkTime.time + ConfigLoader.LoadConfig().Match.CountdownSeconds;
        }
    }

    [ClientRpc]
    public void RpcUpdateHQPlacementProgress(int remainingPlayersCount)
    {
        // Clients compute their own progress text from ClientPlayer.hasPlacedHQ; kept for future UI events.
    }

    [Server]
    public void ApplyOutcome(MatchOutcome outcome)
    {
        if (outcome.Kind == OutcomeKind.Draw)
        {
            // A draw must not send the per-player loss screen: every draw participant is in
            // NewlyEliminated, and RpcOnPlayerLost latches gameOverDeclared on the client,
            // making RpcOnMatchDraw a no-op (the pre-v0.2 draw bug). DeclareDraw wipes the world.
            foreach (int id in outcome.NewlyEliminated) SetEliminatedState(id);
            DeclareDraw();
            return;
        }

        foreach (int id in outcome.NewlyEliminated) EliminatePlayer(id);
        if (outcome.Kind == OutcomeKind.Winner) DeclareWinner(outcome.WinnerId);
    }

    [Server]
    private void SetEliminatedState(int playerId)
    {
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key != null && entry.Key.netId == (uint)playerId && entry.Value.state != PlayerState.Eliminated)
            {
                entry.Value.state = PlayerState.Eliminated;
                return;
            }
        }
    }

    [Server]
    public void EliminatePlayer(int playerId)
    {
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key.netId != (uint)playerId || entry.Value.state == PlayerState.Eliminated) continue;
            entry.Value.state = PlayerState.Eliminated;
            WorldStateManager.Instance?.KillAllEntitiesOwnedBy(playerId);
            ClientPlayer client = entry.Key.GetComponent<ClientPlayer>();
            client.hasPlacedHQ = false;
            client.RpcOnPlayerLost(entry.Value.connection);
            return;
        }
    }

    [Server]
    public void DeclareWinner(int playerId)
    {
        CurrentState = GameState.GameOver;
        WorldStateManager.Instance?.DestroyAllEntities();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            ClientPlayer client = entry.Key.GetComponent<ClientPlayer>();
            if (entry.Key.netId == (uint)playerId) client.RpcOnPlayerWon(entry.Value.connection);
            else
            {
                entry.Value.state = PlayerState.Eliminated;
                client.RpcOnPlayerLost(entry.Value.connection);
            }
        }
    }

    [Server]
    public void DeclareDraw()
    {
        CurrentState = GameState.GameOver;
        WorldStateManager.Instance?.DestroyAllEntities();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            entry.Value.state = PlayerState.Eliminated;
            entry.Key.GetComponent<ClientPlayer>().RpcOnMatchDraw(entry.Value.connection);
        }
    }

    /// <summary>Returns an empty server (e.g. a dedicated server whose players all left) to the lobby.</summary>
    [Server]
    private void ResetToLobby()
    {
        CurrentState = GameState.Lobby;
        CountdownEndTime = 0;
        MatchStartPlayerCount = 0;
        if (SceneManager.GetActiveScene().name != LobbyScene && NetworkServer.active)
        {
            GameManager.Instance.ServerChangeScene(LobbyScene);
        }
    }
}
