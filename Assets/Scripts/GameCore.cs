using WAR2D.Dev.Logging;
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

    /// <summary>Every player's server record: humans, then AIs.</summary>
    public IEnumerable<ServerPlayer> serverPlayers => ServerPlayers.Values.Concat(AiPlayers.Values.Select(seat => seat.Player));

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
        AiPlayers.Clear(); // Mirror's shutdown destroys their player objects with every other spawned object
        serverOwner = null;
        CurrentState = GameState.Lobby;
        MatchStartPlayerCount = 0;
        MatchStartTeamCount = 0;
        Teams.Clear();
        ForgetDiplomacyCooldowns();
        ResetPause();
    }

    [ServerCallback]
    public void LateUpdate()
    {
        // Income and upkeep are counted by match second: nothing rolls while the match is paused.
        if (!MatchPause.Paused) rollTimer += Time.deltaTime;
        if (rollTimer >= 1f)
        {
            rollTimer -= 1f;
            foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
            {
                entry.Value.RollSecond();
                if (entry.Value.BecameUnpaid && entry.Key != null)
                    WorldStateManager.Instance?.Alerts?.Add(BuildingData.UIntToInt(entry.Key.netId), new Alert { Kind = AlertKind.UpkeepUnpaid });
            }
            // AIs count income and upkeep by the second like anyone; their alerts go to their view, never to a connection.
            foreach (KeyValuePair<int, AiSeat> seat in AiPlayers)
            {
                seat.Value.Player.RollSecond();
                if (seat.Value.Player.BecameUnpaid) WorldStateManager.Instance?.Alerts?.Add(seat.Key, new Alert { Kind = AlertKind.UpkeepUnpaid });
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
        UpdatePause();
        if (CurrentState == GameState.Countdown && NetworkTime.time >= CountdownEndTime)
        {
            CurrentState = GameState.Playing;
            MatchStartTime = NetworkTime.time;
            SendAllDiplomacy();
        }
        else if (CurrentState == GameState.PlacingHQ && HqPlacementEndTime > 0 && NetworkTime.time >= HqPlacementEndTime && WorldStateManager.Instance != null)
        {
            HqPlacementEndTime = 0;
            PlaceRemainingRandomly(WorldStateManager.Instance);
        }
        ResendDiplomacyWhenSlotted();
    }

    // ---------- Players and ownership ----------

    [Server]
    public void AddPlayer(NetworkConnectionToClient conn, float startingResources)
    {
        ServerPlayers[conn.identity] = new ServerPlayer(conn, startingResources);
        if (WAR2D.Net.Steam.SteamIdentity.TryGet(conn.connectionId, out ulong sid)) ServerPlayers[conn.identity].SteamId = sid;
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
        CommandGate.Forget(conn.connectionId);
        WorldStateManager.Instance?.ReplaySender?.Cancel(conn);
        if (conn.identity == null || !ServerPlayers.Remove(conn.identity)) return;

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
        WarLog.Info(LogCategory.Lobby, $"Player {conn.identity.netId} is now the server owner");
    }

    [Server]
    public bool IsServerOwner(NetworkConnectionToClient conn) => conn != null && serverOwner == conn;

    [Server]
    public ServerPlayer GetServerPlayerById(int ownerId)
    {
        if (AiPlayers.TryGetValue(ownerId, out AiSeat seat)) return seat.Player;
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key != null && entry.Key.netId == (uint)ownerId) return entry.Value;
        }
        return null;
    }

    // ---------- State machine ----------

    /// <summary>Dev only (debug API): applies <paramref name="settings"/> and starts the match as the owner would, skipping the ready checks.</summary>
    [Server]
    internal void DevStartMatch(MatchSettings settings)
    {
        if (!DevApi.Allowed || CurrentState != GameState.Lobby || !ConfigLoader.IsValid) return;
        Settings = settings;
        StartMatchServerSide();
    }

    [Command(requiresAuthority = false)]
    public void Cmd_StartGame(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_StartGame))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby || !ConfigLoader.IsValid) return;
        // Dev only (perf harness, hosted tests): a solo host may start without the lobby's rules.
        if (!DevApi.Allowed && (!LobbyRules.CanStart(LobbyStartState()) || !LobbyRules.FitsMap(SeatCount, Settings, ConfigLoader.LoadConfig().Match.Map, ConfigLoader.LoadConfig().Lobby.SpareStartSites))) return;

        StartMatchServerSide();
    }

    private void StartMatchServerSide()
    {
        DiplomacyEnabled = Settings.Diplomacy;
        ResetPause();
        ForgetDiplomacyCooldowns(); // they count on the match clock, which starts again at 0
        BeginStats();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            entry.Value.state = PlayerState.Playing;
            entry.Value.ResetResources(Settings.StartingResources);
            if (entry.Key != null) entry.Key.GetComponent<ClientPlayer>().hasPlacedHQ = false;
        }
        StartAiSeats();
        // Captured at launch so a departure before Playing can't stop the survivor from winning. AIs count like humans.
        MatchStartPlayerCount = SeatCount;
        // Join order (ascending net id, AIs after the humans), as the lobby lists the players and colours their HQ sites.
        var order = new List<int>();
        foreach (NetworkIdentity identity in ServerPlayers.Keys) if (identity != null) order.Add(BuildingData.UIntToInt(identity.netId));
        order.AddRange(AiPlayers.Keys);
        order.Sort();
        PlayerOrder.Clear();
        foreach (int owner in order) PlayerOrder.Add(owner);
        AssignTeams();
        HqPlacementEndTime = 0;
        foreach (ServerPlayer p in ServerPlayers.Values) { p.StartOptions = null; p.StartPick = -1; }
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
            if (identity != null) choices.Add((BuildingData.UIntToInt(identity.netId), identity.GetComponent<ClientPlayer>().lobbyTeam));
        foreach (AiSeat seat in AiPlayers.Values) choices.Add((seat.OwnerId, seat.LobbyTeam));
        Teams.Clear();
        foreach (KeyValuePair<int, int> pair in TeamRules.Assign(choices)) Teams[pair.Key] = pair.Value;
        MatchStartTeamCount = TeamRules.CountTeams(Teams.Values);
    }

    [Server]
    public void CheckHQPlacementProgress()
    {
        // Scripted seats are placed by whoever drives them (the perf harness, tests), so they never hold up the countdown.
        int remaining = ServerPlayers.Keys.Count(id => id != null && !id.GetComponent<ClientPlayer>().hasPlacedHQ)
                        + AiPlayers.Values.Count(seat => !seat.IsScripted && !HasPlacedHq(seat.OwnerId));
        RpcUpdateHQPlacementProgress(remaining);

        if (remaining == 0 && CurrentState == GameState.PlacingHQ)
        {
            CurrentState = GameState.Countdown;
            HqPlacementEndTime = 0;
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
        if (AiPlayers.TryGetValue(playerId, out AiSeat seat))
        {
            seat.Player.state = PlayerState.Eliminated;
            return;
        }
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
        if (AiPlayers.TryGetValue(playerId, out AiSeat seat))
        {
            if (seat.Player.state == PlayerState.Eliminated) return;
            seat.Player.state = PlayerState.Eliminated;
            WorldStateManager.Instance?.KillAllEntitiesOwnedBy(playerId);
            if (seat.Client != null) seat.Client.hasPlacedHQ = false; // shown eliminated, like a human; no connection to tell
            return;
        }
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key == null || entry.Key.netId != (uint)playerId || entry.Value.state == PlayerState.Eliminated) continue;
            entry.Value.state = PlayerState.Eliminated;
            WorldStateManager.Instance?.KillAllEntitiesOwnedBy(playerId);
            ClientPlayer client = entry.Key.GetComponent<ClientPlayer>();
            client.hasPlacedHQ = false;
            client.RpcOnPlayerLost(entry.Value.connection);
            return;
        }
    }

    /// <summary>Ends the match: every player in <paramref name="winners"/> wins, everyone else loses.</summary>
    [Server]
    public void DeclareWinner(IReadOnlyCollection<int> winners)
    {
        CurrentState = GameState.GameOver;
        SendStats(winners);
        WorldStateManager.Instance?.MatchEnded(Newtonsoft.Json.JsonConvert.SerializeObject(new { winners }));
        WorldStateManager.Instance?.DestroyAllEntities();
        foreach (AiSeat seat in AiPlayers.Values)
            if (!winners.Contains(seat.OwnerId)) seat.Player.state = PlayerState.Eliminated;
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key == null) continue; // left mid-tick; OnPlayerLeave removes them
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
        WorldStateManager.Instance?.MatchEnded(Newtonsoft.Json.JsonConvert.SerializeObject(new { draw = true }));
        WorldStateManager.Instance?.DestroyAllEntities();
        foreach (AiSeat seat in AiPlayers.Values) seat.Player.state = PlayerState.Eliminated;
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            entry.Value.state = PlayerState.Eliminated;
            if (entry.Key == null) continue;
            entry.Key.GetComponent<ClientPlayer>().RpcOnMatchDraw(entry.Value.connection);
        }
    }

    /// <summary>Returns an empty server (e.g. a dedicated server whose players all left) to the lobby.</summary>
    [Server]
    private void ResetToLobby()
    {
        CurrentState = GameState.Lobby;
        ResetPause();
        CountdownEndTime = 0;
        HqPlacementEndTime = 0;
        MatchStartPlayerCount = 0;
        MatchStartTeamCount = 0;
        Teams.Clear();
        ForgetDiplomacyCooldowns();
        DiplomacyEnabled = false;
        ClearAiPlayers();
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
        if (SceneManager.GetActiveScene().name != LobbyScene && NetworkServer.active)
        {
            GameManager.Instance.ServerChangeScene(LobbyScene);
        }
    }
}
