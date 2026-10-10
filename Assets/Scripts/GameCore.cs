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
public partial class GameCore : NetworkBehaviour
{
    public static GameCore Instance { get; private set; }

    [SyncVar]
    public GameState CurrentState = GameState.Lobby;

    /// <summary>NetworkTime.time at which Countdown ends (valid while CurrentState == Countdown).</summary>
    [SyncVar]
    public double CountdownEndTime;

    /// <summary>Server-only player records, keyed by player object identity.</summary>
    public Dictionary<NetworkIdentity, ServerPlayer> ServerPlayers = new Dictionary<NetworkIdentity, ServerPlayer>();

    public IEnumerable<ServerPlayer> serverPlayers => ServerPlayers.Values.Concat(Bots.Values);

    /// <summary>Players present when Playing began (used for the win rule).</summary>
    public int MatchStartPlayerCount { get; private set; }

    /// <summary>Teams present when Playing began (used for the win rule).</summary>
    public int MatchStartTeamCount { get; private set; }

    /// <summary>Team per owner id, fixed when the match starts and synced to clients. Allies share a team.</summary>
    public readonly SyncDictionary<int, int> Teams = new SyncDictionary<int, int>();

    /// <summary>The team of an owner: from <see cref="Teams"/>, or a team of its own.</summary>
    public int TeamOf(int ownerId) => Teams.TryGetValue(ownerId, out int team) ? team : -ownerId - 1;


    /// <summary>Owner ids in match order, synced to clients: an owner's position here picks its colour.</summary>
    public readonly SyncList<int> PlayerOrder = new SyncList<int>();

    private NetworkConnectionToClient serverOwner;

    private const string LobbyScene = "Main_Menu";
    private const float ResourceSyncInterval = 0.1f;
    private float resourceSyncTimer;
    private float rollTimer;

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

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    [Server]
    public override void OnStartServer()
    {
        base.OnStartServer();
        InitSettings();
    }

    [Server]
    public override void OnStopServer()
    {
        base.OnStopServer();
        ServerPlayers.Clear();
        Bots.Clear();
        serverOwner = null;
        CurrentState = GameState.Lobby;
        MatchStartPlayerCount = 0;
        MatchStartTeamCount = 0;
        Teams.Clear();
        ForgetDiplomacyCooldowns();
    }

    [ServerCallback]
    public void LateUpdate()
    {
        rollTimer += Time.deltaTime;
        if (rollTimer >= 1f)
        {
            rollTimer -= 1f;
            foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
            {
                entry.Value.RollSecond();
                if (entry.Value.BecameUnpaid && entry.Key != null)
                    WorldStateManager.Instance?.Alerts?.Add(BuildingData.UIntToInt(entry.Key.netId), new Alert { Kind = AlertKind.UpkeepUnpaid });
            }
        }

        resourceSyncTimer += Time.deltaTime;
        if (resourceSyncTimer < ResourceSyncInterval) return;
        resourceSyncTimer = 0f;

        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            ServerPlayer player = entry.Value;
            if (!player.ResourcesDirty || entry.Key == null) continue;
            entry.Key.GetComponent<ClientPlayer>().TargetUpdateResources(player.connection, player.Resources, player.IncomeLastSecond, player.UpkeepLastSecond);
            player.MarkSynced();
        }
    }

    [ServerCallback]
    public void Update()
    {
        if (CurrentState == GameState.Countdown && NetworkTime.time >= CountdownEndTime)
        {
            CurrentState = GameState.Playing;
            MatchStartTime = NetworkTime.time;
            SendAllDiplomacy();
        }
    }

    // ---------- Players and ownership ----------

    [Server]
    public void AddPlayer(NetworkConnectionToClient conn, float startingResources)
    {
        ServerPlayers[conn.identity] = new ServerPlayer(conn, startingResources);
        if (serverOwner == null) SetServerOwner(conn);
        if (conn.identity.TryGetComponent(out ClientPlayer joining))
        {
            AssignFreeColour(joining);
            joining.ready = false;
            if (Settings.Mode == MatchMode.FreeForAll) joining.lobbyTeam = TeamRules.NoTeam;
        }
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

    /// <summary>Connectionless players (the performance harness's bots), by owner id.</summary>
    public readonly Dictionary<int, ServerPlayer> Bots = new Dictionary<int, ServerPlayer>();

    /// <summary>
    /// Adds a bot: a player with no connection, counted in the player order and the match's player count.
    /// Dev only (the performance harness); refused unless dev APIs are enabled.
    /// </summary>
    [Server]
    internal void AddBot(int ownerId, float startingResources)
    {
        if (!DevApi.Allowed || Bots.ContainsKey(ownerId)) return;
        Bots[ownerId] = new ServerPlayer(null, startingResources);
        PlayerOrder.Add(ownerId);
        MatchStartPlayerCount++;
        int team = 0;
        foreach (int t in Teams.Values) team = Mathf.Max(team, t + 1);
        Teams[ownerId] = team; // bots are free-for-all
        MatchStartTeamCount = TeamRules.CountTeams(Teams.Values);
    }

    [Server]
    public ServerPlayer GetServerPlayerById(int ownerId)
    {
        if (Bots.TryGetValue(ownerId, out ServerPlayer bot)) return bot;
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
        // Dev only (perf harness, hosted tests): a solo host may start without the lobby's rules.
        if (!DevApi.Allowed && !LobbyRules.CanStart(LobbyStartState())) return;

        DiplomacyEnabled = Settings.Diplomacy;
        BeginStats();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            entry.Value.state = PlayerState.Playing;
            entry.Value.ResetResources(Settings.StartingResources);
            entry.Key.GetComponent<ClientPlayer>().hasPlacedHQ = false;
        }
        // Captured at launch so a departure before Playing can't stop the survivor from winning.
        MatchStartPlayerCount = ServerPlayers.Count;
        // Join order (ascending net id), as the lobby lists the players and colours their HQ sites.
        var order = new List<int>();
        foreach (NetworkIdentity identity in ServerPlayers.Keys) order.Add(BuildingData.UIntToInt(identity.netId));
        order.Sort();
        PlayerOrder.Clear();
        foreach (int owner in order) PlayerOrder.Add(owner);
        AssignTeams();
        CurrentState = GameState.PlacingHQ;
        GameManager.Instance.ServerChangeScene(ConfigLoader.LoadConfig().Match.Scene);
    }

    /// <summary>
    /// Sets a player's lobby team choice (<see cref="TeamRules.NoTeam"/> for a team of their own). Server
    /// owner only, in the lobby, with the choices a player could make for themselves (Free-for-all is Solo only).
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetTeam(uint playerNetId, int team, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetTeam))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (!LobbyRules.IsTeamChoiceValid(Settings.Mode, team)) return;
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
        {
            if (identity == null || identity.netId != playerNetId) continue;
            identity.GetComponent<ClientPlayer>().lobbyTeam = team;
            return;
        }
    }

    /// <summary>Fixes the match's teams from the players' lobby choices.</summary>
    [Server]
    private void AssignTeams()
    {
        var choices = new List<(int Owner, int Choice)>();
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            choices.Add((BuildingData.UIntToInt(identity.netId), identity.GetComponent<ClientPlayer>().lobbyTeam));
        Teams.Clear();
        foreach (KeyValuePair<int, int> pair in TeamRules.Assign(choices)) Teams[pair.Key] = pair.Value;
        MatchStartTeamCount = TeamRules.CountTeams(Teams.Values);
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
        if (outcome.Kind == OutcomeKind.Winner) DeclareWinner(outcome.Winners);
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
        if (Bots.TryGetValue(playerId, out ServerPlayer bot))
        {
            if (bot.state == PlayerState.Eliminated) return;
            bot.state = PlayerState.Eliminated;
            WorldStateManager.Instance?.KillAllEntitiesOwnedBy(playerId);
            return;
        }
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
    /// <summary>Ends the match: every player in <paramref name="winners"/> wins, everyone else loses.</summary>
    public void DeclareWinner(IReadOnlyCollection<int> winners)
    {
        CurrentState = GameState.GameOver;
        SendStats(winners);
        WorldStateManager.Instance?.DestroyAllEntities();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            ClientPlayer client = entry.Key.GetComponent<ClientPlayer>();
            if (winners.Contains(BuildingData.UIntToInt(entry.Key.netId))) client.RpcOnPlayerWon(entry.Value.connection);
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
        SendStats(null);
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
        MatchStartTeamCount = 0;
        Teams.Clear();
        ForgetDiplomacyCooldowns();
        DiplomacyEnabled = false;
        Bots.Clear();
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
        if (SceneManager.GetActiveScene().name != LobbyScene && NetworkServer.active)
        {
            GameManager.Instance.ServerChangeScene(LobbyScene);
        }
    }
}
