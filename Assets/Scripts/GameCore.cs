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
        Rejoin.RevokeAll();
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
            if (!player.ResourcesDirty || entry.Key == null || player.connection == null) continue; // an away player has no connection
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
            IssueRejoinTickets();
        }
        else if (CurrentState == GameState.PlacingHQ && HqPlacementEndTime > 0 && NetworkTime.time >= HqPlacementEndTime && WorldStateManager.Instance != null)
        {
            HqPlacementEndTime = 0;
            PlaceRemainingRandomly(WorldStateManager.Instance);
        }
        ResendDiplomacyWhenSlotted();
        UpdateAway();
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

    /// <summary>
    /// A connection is gone. Returns true when the player's seat was kept for them to come back to (they dropped out of a running
    /// match with a ticket, <see cref="TryMarkAway"/>): the caller then only disowns the player object instead of destroying it.
    /// </summary>
    [Server]
    public bool OnPlayerLeave(NetworkConnectionToClient conn)
    {
        bool kicked = CommandGate.TakeKicked(conn.connectionId); // a kick is not a drop: no seat is kept
        CommandGate.Forget(conn.connectionId);
        if (!kicked && TryMarkAway(conn)) return true;
        if (conn.identity == null || !ServerPlayers.Remove(conn.identity)) return false;

        Rejoin.Revoke(BuildingData.UIntToInt(conn.identity.netId), kicked ? WAR2D.Net.Rejoin.RejoinRefusal.Kicked : WAR2D.Net.Rejoin.RejoinRefusal.Left);
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
                return false;
            }
            HandOverServerOwner();
        }

        if (ServerPlayers.Count == 0)
        {
            ResetToLobby();
        }
        else if (CurrentState == GameState.PlacingHQ)
        {
            CheckHQPlacementProgress();
        }
        return false;
    }

    /// <summary>Gives server ownership to the first player who is connected (not away), if any.</summary>
    [Server]
    private void HandOverServerOwner()
    {
        foreach (ServerPlayer player in ServerPlayers.Values)
            if (player.connection != null && !player.Away) { SetServerOwner(player.connection); return; }
    }

    // ---------- Rejoin (v0.15) ----------

    /// <summary>The rejoin tickets and the players who are away (server only).</summary>
    public readonly WAR2D.Net.Rejoin.AwayRegistry Rejoin = new WAR2D.Net.Rejoin.AwayRegistry();

    /// <summary>
    /// Gives every connected human (the host's own connection excepted: when the host goes the match ends) a ticket and tells them
    /// its token, so that a drop later in the match can be rejoined. Called when play starts.
    /// </summary>
    [Server]
    private void IssueRejoinTickets()
    {
        Rejoin.RevokeAll(); // a new match: no ticket from an earlier one stays good
        float window = ConfigLoader.LoadConfig().Match.ReconnectSeconds;
        if (window <= 0f) return;
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key == null || entry.Value.connection == null || entry.Value.connection is LocalConnectionToClient) continue;
            string token = Rejoin.Issue(BuildingData.UIntToInt(entry.Key.netId));
            entry.Key.GetComponent<ClientPlayer>().TargetRejoinTicket(entry.Value.connection, token, window);
        }
    }

    /// <summary>
    /// A human with a ticket dropped out of the running match: keep their seat for <c>Match/ReconnectSeconds</c>. Their units hold
    /// position, nothing is earned, charged or produced for them, and others see them marked away. False when they are not eligible
    /// (the match isn't being played, they're out, or they have no ticket): the caller removes them as before.
    /// </summary>
    /// <param name="hold">False when the player is coming straight back on a new connection (<see cref="TryTakeOver"/>): their orders stand.</param>
    [Server]
    private bool TryMarkAway(NetworkConnectionToClient conn, bool hold = true)
    {
        if (CurrentState != GameState.Playing || conn.identity == null || conn is LocalConnectionToClient) return false;
        if (!ServerPlayers.TryGetValue(conn.identity, out ServerPlayer player) || player.state != PlayerState.Playing) return false;
        ClientPlayer client = conn.identity.GetComponent<ClientPlayer>();
        if (!client.hasPlacedHQ) return false;
        int owner = BuildingData.UIntToInt(conn.identity.netId);
        if (!Rejoin.MarkAway(owner, NetworkTime.time, ConfigLoader.LoadConfig().Match.ReconnectSeconds)) return false;

        player.connection = null;
        player.Away = true;
        client.isAway = true;
        client.awayUntil = Rejoin.UntilOf(owner);
        if (hold) WorldStateManager.Instance?.HoldAll(owner);
        if (serverOwner == conn)
        {
            serverOwner = null;
            client.isServerOwner = false;
            HandOverServerOwner();
        }
        WarLog.Info(LogCategory.Net, $"Player {owner} dropped out; their seat is kept for {ConfigLoader.LoadConfig().Match.ReconnectSeconds:0} s");
        return true;
    }

    /// <summary>The human player object of <paramref name="owner"/>, or null.</summary>
    [Server]
    private NetworkIdentity IdentityOf(int owner)
    {
        foreach (NetworkIdentity key in ServerPlayers.Keys) if (key != null && key.netId == (uint)owner) return key;
        return null;
    }

    /// <summary>
    /// The away player's window closed, the match ended, or they were eliminated while away: they are removed as a player who
    /// left, and their ticket is torn up.
    /// </summary>
    [Server]
    private void EndAway(int owner, WAR2D.Net.Rejoin.RejoinRefusal why)
    {
        Rejoin.Revoke(owner, why);
        NetworkIdentity identity = IdentityOf(owner);
        if (identity == null) return;
        ClientPlayer leaving = identity.GetComponent<ClientPlayer>();
        ServerPlayers.Remove(identity);
        if (WorldStateManager.Instance != null)
        {
            WorldStateManager.Instance.RemovePlayerView(leaving);
            WorldStateManager.Instance.KillAllEntitiesOwnedBy(owner);
        }
        NetworkServer.Destroy(identity.gameObject);
        WarLog.Info(LogCategory.Net, $"Player {owner} is out while away ({why})");
        if (ServerPlayers.Count == 0) ResetToLobby();
    }

    private readonly List<int> expiredAway = new List<int>();

    /// <summary>Closes the windows that ran out; once the match is no longer being played every away seat closes.</summary>
    [Server]
    private void UpdateAway()
    {
        if (Rejoin.AwayCount == 0) return;
        expiredAway.Clear();
        bool over = CurrentState != GameState.Playing;
        if (over) foreach (int owner in new List<int>(Rejoin.Away)) expiredAway.Add(owner);
        else Rejoin.CollectExpired(NetworkTime.time, expiredAway);
        foreach (int owner in expiredAway) EndAway(owner, over ? WAR2D.Net.Rejoin.RejoinRefusal.MatchOver : WAR2D.Net.Rejoin.RejoinRefusal.TimedOut);
    }

    /// <summary>
    /// A connection presents the token of an away seat: if it is valid (and the window open) the connection becomes that player,
    /// who gets everything the server holds for them sent again. False when the token is unknown or too late.
    /// </summary>
    [Server]
    public bool TryRejoin(NetworkConnectionToClient conn, string token)
    {
        if (CurrentState != GameState.Playing) return false;
        if (!Rejoin.TryClaim(token, NetworkTime.time, out int owner) && !TryTakeOver(conn, token, out owner)) return false;
        NetworkIdentity identity = IdentityOf(owner);
        if (identity == null || !NetworkServer.AddPlayerForConnection(conn, identity.gameObject)) return false;
        ServerPlayer player = ServerPlayers[identity];
        ClientPlayer client = identity.GetComponent<ClientPlayer>();
        player.connection = conn;
        player.Away = false;
        player.MarkDirty();
        client.isAway = false;
        client.awayUntil = 0;
        if (WAR2D.Net.Steam.SteamIdentity.TryGet(conn.connectionId, out ulong sid)) player.SteamId = sid;
        string fresh = Rejoin.Return(owner);
        client.TargetRejoinTicket(conn, fresh, ConfigLoader.LoadConfig().Match.ReconnectSeconds);
        WAR2D.Net.Replication.ReplicationService.Instance?.OnClientJoined(conn, owner);
        WorldStateManager.Instance?.SendOwnProducers(owner); // the new client knows none of its queues or rallies
        if (serverOwner == null) SetServerOwner(conn);
        SendAllDiplomacy();
        WarLog.Info(LogCategory.Net, $"Player {owner} came back");
        return true;
    }

    /// <summary>
    /// A valid token for a seat that still has its old connection: the client noticed the drop before the server did (a short
    /// network blip). The ticket wins: the old connection loses the player (kept, not destroyed) and is closed, and the seat is
    /// claimed as if it had gone away. The player's orders are not cancelled.
    /// </summary>
    [Server]
    private bool TryTakeOver(NetworkConnectionToClient conn, string token, out int owner)
    {
        if (!Rejoin.TryOwnerOf(token, out owner) || Rejoin.IsAway(owner)) return false; // away but past the window: TryClaim said no
        NetworkIdentity identity = IdentityOf(owner);
        NetworkConnectionToClient old = identity != null ? identity.connectionToClient : null;
        if (old == null || old == conn || !TryMarkAway(old, hold: false)) return false;
        WarLog.Info(LogCategory.Net, $"Player {owner} is back on connection {conn.connectionId}; closing their old connection {old.connectionId}");
        WAR2D.Net.Replication.ReplicationService.Instance?.OnClientLeft(old);
        CommandGate.Forget(old.connectionId);
        NetworkServer.RemovePlayerForConnection(old, RemovePlayerOptions.KeepActive);
        old.Disconnect(); // its disconnect finds no player, so it removes nobody
        return Rejoin.TryClaim(token, NetworkTime.time, out owner);
    }

    /// <summary>
    /// A client leaving the running match on purpose (<see cref="WAR2D.Net.Rejoin.LeaveMatch"/>): its ticket is torn up so the
    /// disconnect that follows removes the player at once instead of keeping the seat, and the connection is closed.
    /// </summary>
    [Server]
    public void OnLeaveMatch(NetworkConnectionToClient conn)
    {
        if (conn.identity != null) Rejoin.Revoke(BuildingData.UIntToInt(conn.identity.netId), WAR2D.Net.Rejoin.RejoinRefusal.Left);
        conn.Disconnect();
    }

    /// <summary>Why <see cref="TryRejoin"/> refused a token (sent to the client in <see cref="WAR2D.Net.Rejoin.RejoinRefused"/>).</summary>
    [Server]
    public WAR2D.Net.Rejoin.RejoinRefusal RefusalFor(string token) =>
        CurrentState != GameState.Playing ? WAR2D.Net.Rejoin.RejoinRefusal.MatchOver : Rejoin.RefusalOf(token);

    /// <summary>True when any seat is away and so a new connection might be a player coming back.</summary>
    public bool HasAwayPlayers => Rejoin.AwayCount > 0;

    /// <summary>
    /// True when any human holds a ticket, so a new connection in a running match might be one of them coming back (perhaps
    /// before the server noticed they dropped). Such a connection must present a valid token or it is closed.
    /// </summary>
    public bool MayRejoin => Rejoin.TicketCount > 0;

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
        bool outWhileAway = false;
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key == null || entry.Key.netId != (uint)playerId || entry.Value.state == PlayerState.Eliminated) continue;
            entry.Value.state = PlayerState.Eliminated;
            WorldStateManager.Instance?.KillAllEntitiesOwnedBy(playerId);
            ClientPlayer client = entry.Key.GetComponent<ClientPlayer>();
            client.hasPlacedHQ = false;
            if (entry.Value.connection != null) client.RpcOnPlayerLost(entry.Value.connection);
            else outWhileAway = entry.Value.Away;
            break;
        }
        // Eliminated while away: there is no seat left to come back to (a rejoin would land in a match with no HQ and no
        // Defeat screen), so they are out now, as when their window runs out.
        if (outWhileAway) EndAway(playerId, WAR2D.Net.Rejoin.RejoinRefusal.Eliminated);
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
            if (entry.Value.connection == null)
            {
                // Away: nobody to show a screen to.
                if (!winners.Contains(BuildingData.UIntToInt(entry.Key.netId))) entry.Value.state = PlayerState.Eliminated;
                continue;
            }
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
            if (entry.Value.connection != null) entry.Key.GetComponent<ClientPlayer>().RpcOnMatchDraw(entry.Value.connection);
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
