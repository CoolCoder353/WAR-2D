using System.Collections.Generic;
using Config;
using Mirror;

/// <summary>
/// The lobby: the host's match settings (a public SyncVar, the only source of the map and starting
/// resources at start), each player's colour, team and ready flag, and when the match may start.
/// </summary>
public partial class GameCore
{
    /// <summary>The host's match settings (public lobby information).</summary>
    [SyncVar]
    public MatchSettings Settings;

    /// <summary>Starts the lobby from the config's defaults (the map size and seed, and the starting resources).</summary>
    [Server]
    private void InitSettings()
    {
        GameConfigData config = ConfigLoader.LoadConfig();
        Settings = new MatchSettings
        {
            Mode = MatchMode.FreeForAll,
            Diplomacy = false,
            MapSize = config.Match.Map.Size,
            Seed = config.Match.Map.Seed != 0 ? config.Match.Map.Seed : NewSeed(),
            StartingResources = config.Resources.StartingResources,
            StartOptions = config.Lobby.StartOptionsDefault,
            StartPickSeconds = config.Lobby.StartPickDefault,
            Visibility = config.Lobby.Visibility,
        };
    }

    /// <summary>The host's remembered settings and AI seats over the defaults (v0.18), when starting a lobby.</summary>
    [Server]
    private void ApplyRememberedLobby()
    {
        RememberedLobby lobby = LobbyMemory.Load();
        if (lobby == null) return;
        Settings = LobbyMemory.Apply(Settings, lobby, ConfigLoader.LoadConfig().Lobby);
        if (lobby.Ais == null) return;
        foreach (RememberedAi ai in lobby.Ais)
        {
            if (SeatCount >= LobbyRules.MaxPlayers) break;
            if (System.Enum.IsDefined(typeof(AiDifficulty), ai.Difficulty) && System.Enum.IsDefined(typeof(AiStyle), ai.Style)) AddAi(ai.Difficulty, ai.Style, -1, ai.Team);
        }
    }

    /// <summary>What the host just started with, saved on their machine.</summary>
    [Server]
    private void RememberLobby()
    {
        var lobby = new RememberedLobby
        {
            Mode = Settings.Mode, Diplomacy = Settings.Diplomacy, MapSize = Settings.MapSize, StartingResources = Settings.StartingResources,
            StartOptions = Settings.StartOptions, StartPickSeconds = Settings.StartPickSeconds, Visibility = Settings.Visibility,
        };
        foreach (AiSeat seat in AiPlayers.Values)
            lobby.Ais.Add(new RememberedAi { Difficulty = seat.Difficulty, Style = seat.Style, Team = seat.Client != null ? seat.Client.lobbyTeam : TeamRules.NoTeam });
        LobbyMemory.Save(lobby);
    }

    /// <summary>The lobby's Reset (v0.18, Figma L1): back to the config's defaults, with no AIs. Server owner only, in the lobby.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_ResetLobby(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_ResetLobby))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        ClearAiPlayers();
        InitSettings();
        LobbyMemory.Forget();
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
    }

    private static uint NewSeed() => (uint)UnityEngine.Random.Range(1, int.MaxValue);

    /// <summary>Replaces the match settings. Server owner only, in the lobby, and only with values the lobby offers. Clears every ready flag.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetMatchSettings(MatchSettings settings, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetMatchSettings))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (!MatchSettingsRules.IsValid(settings, ConfigLoader.LoadConfig().Lobby)) return;
        // Going Private with another human connected is refused (the host's lobby shows "Remove the other players first").
        if (!LobbyRules.MayChangeVisibility(Settings.Visibility, settings.Visibility, ServerPlayers.Count)) return;
        bool reseeded = settings.Seed != Settings.Seed;
        Settings = settings;
        if (reseeded) RedrawCallsigns();
        foreach (ClientPlayer player in LobbyClientPlayers())
        {
            if (settings.Mode == MatchMode.FreeForAll) player.lobbyTeam = TeamRules.NoTeam;
            player.ready = false;
        }
        if (settings.Mode == MatchMode.FreeForAll) SetAiTeams(TeamRules.NoTeam);
    }

    /// <summary>Puts every lobby AI on <paramref name="team"/> (Free-for-all forces Solo).</summary>
    [Server]
    private void SetAiTeams(int team)
    {
        foreach (AiSeat seat in AiPlayers.Values)
        {
            seat.LobbyTeam = team;
            if (seat.Client != null) seat.Client.lobbyTeam = team;
        }
    }

    /// <summary>Picks a new random map seed. Server owner only, in the lobby. Clears every ready flag.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_RerollMap(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_RerollMap))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        MatchSettings settings = Settings;
        settings.Seed = NewSeed();
        Settings = settings;
        RedrawCallsigns();
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
    }

    /// <summary>
    /// Adds an AI seat (v0.14): server owner only, in the lobby, Easy/Normal/Hard and a known style. It takes the first
    /// unused colour and, in Teams mode, the team with the fewest players. Refused past the seat limit. Clears every ready flag.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_AddAi(AiDifficulty difficulty, AiStyle style, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_AddAi))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (!AiLobbyRules.IsValidChoice(difficulty, style) || !AiLobbyRules.HasFreeSeat(SeatCount)) return;
        var teams = new List<int>();
        foreach (ClientPlayer player in LobbyClientPlayers()) teams.Add(player.lobbyTeam);
        foreach (AiSeat seat in AiPlayers.Values) teams.Add(seat.LobbyTeam);
        AddAi(difficulty, style, -1, LobbyRules.FewestPlayersTeam(Settings.Mode, teams));
    }

    /// <summary>
    /// Changes a lobby AI (v0.14): server owner only, in the lobby, for an existing (not Scripted) AI, with a valid
    /// difficulty and style, a colour nobody else has and a team the mode offers. Clears every ready flag on a change.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetAi(int ownerId, AiDifficulty difficulty, AiStyle style, int colour, int team, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetAi))) return;
        if (!IsServerOwner(sender)) return;
        SetAi(ownerId, difficulty, style, colour, team);
    }

    /// <summary>Removes a lobby AI (v0.14): server owner only, in the lobby. Clears every ready flag.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_RemoveAi(int ownerId, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_RemoveAi))) return;
        if (!IsServerOwner(sender)) return;
        RemoveAi(ownerId);
    }

    /// <summary>Takes a palette colour nobody else has, in the lobby.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetColour(byte colourIndex, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetColour))) return;
        if (CurrentState != GameState.Lobby || !TryLobbyPlayer(sender, out ClientPlayer me)) return;
        if (LobbyRules.IsColourFree(colourIndex, TakenColours(me))) me.colourIndex = colourIndex;
        else if (TakeColourFromAi(colourIndex, me)) me.colourIndex = colourIndex;
    }

    /// <summary>Marks the sender ready (or not), in the lobby.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetReady(bool ready, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetReady))) return;
        if (CurrentState != GameState.Lobby || !TryLobbyPlayer(sender, out ClientPlayer me)) return;
        if (sender != null && CommandGate.IsKicked(sender.connectionId)) return; // removed by the host, closing in a moment
        me.ready = ready;
    }

    /// <summary>Picks the sender's own team (Teams mode; Solo is always allowed), in the lobby.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetOwnTeam(int team, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetOwnTeam))) return;
        if (CurrentState != GameState.Lobby || !TryLobbyPlayer(sender, out ClientPlayer me)) return;
        if (LobbyRules.IsTeamChoiceValid(Settings.Mode, team)) me.lobbyTeam = team;
    }

    /// <summary>Every player in the lobby as the start rule sees them.</summary>
    [Server]
    public List<(int owner, bool ready, int team)> LobbyStartState()
    {
        var players = new List<(int, bool, int)>();
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null && identity.TryGetComponent(out ClientPlayer player))
                players.Add((BuildingData.UIntToInt(identity.netId), player.ready, player.lobbyTeam));
        foreach (AiSeat seat in AiPlayers.Values) players.Add((seat.OwnerId, true, seat.LobbyTeam)); // an AI is always ready
        return players;
    }

    /// <summary>A newly joined player gets the lowest free colour.</summary>
    [Server]
    private void AssignFreeColour(ClientPlayer joining)
    {
        int colour = LobbyRules.FirstFreeColour(TakenColours(joining));
        if (colour < 0) // LobbyRules.MayJoin keeps the lobby within the palette, so this means that rule broke
            WAR2D.Dev.Logging.WarLog.Error(WAR2D.Dev.Logging.LogCategory.Lobby, $"no free colour for netId {joining.netId}; sharing colour 0");
        joining.colourIndex = colour < 0 ? 0 : colour;
    }

    /// <summary>The humans' player objects (AIs are always ready and keep their own seats).</summary>
    private IEnumerable<ClientPlayer> LobbyClientPlayers()
    {
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null && identity.TryGetComponent(out ClientPlayer player)) yield return player;
    }

    private bool TryLobbyPlayer(NetworkConnectionToClient sender, out ClientPlayer player)
    {
        player = null;
        return sender?.identity != null && ServerPlayers.ContainsKey(sender.identity) && sender.identity.TryGetComponent(out player);
    }

    // ---------- Host tools (playtest 2026-11-02, Figma K1/K2) ----------

    /// <summary>The server's name as the host set it (empty: "&lt;host&gt;'s server", see <see cref="DisplayServerName"/>).</summary>
    [SyncVar]
    public string ServerName = "";

    /// <summary>The players the host banned while this server is open.</summary>
    public readonly WAR2D.Net.LobbyBans Bans = new WAR2D.Net.LobbyBans();

    /// <summary>The server's name as players see it (server side: from the owner's nickname when none was chosen).</summary>
    [Server]
    public string DisplayServerName()
    {
        string host = null;
        if (serverOwner?.identity != null && serverOwner.identity.TryGetComponent(out ClientPlayer owner)) host = owner.nickname;
        return WAR2D.Net.ServerNames.Display(ServerName, host);
    }

    /// <summary>Names the server (shown in the lobby and friends' join lists). Server owner only, in the lobby; empty restores the default.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetServerName(string name, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetServerName))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (string.IsNullOrWhiteSpace(name)) { ServerName = ""; return; }
        if (CommandValidator.TrySanitizeNickname(name, out string clean)) ServerName = clean;
    }

    /// <summary>Marks another human in the lobby ready (they can still cancel). Server owner only.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_ForceReady(uint target, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_ForceReady))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (TryOtherHuman(target, sender, out _, out ClientPlayer player)) player.ready = true;
    }

    /// <summary>
    /// Removes another human from the lobby: Kick, or Ban (<paramref name="ban"/>: they can't join this server again while it
    /// is open). They are told why, then disconnected. Server owner only, never themselves.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_RemovePlayer(uint target, bool ban, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_RemovePlayer))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (!TryOtherHuman(target, sender, out NetworkConnectionToClient conn, out ClientPlayer player)) return;
        if (ban) Bans.Add(WAR2D.Net.LobbyBans.KeyOf(conn));
        // Out of the lobby at once (code review 2026-11-03): not ready, so the match can't start with them in the moment
        // before the connection closes, and their leaving keeps no seat to rejoin.
        player.ready = false;
        CommandGate.MarkKicked(conn.connectionId);
        WAR2D.Dev.Logging.WarLog.Info(WAR2D.Dev.Logging.LogCategory.Lobby, $"the host {(ban ? "banned" : "kicked")} {player.nickname} (netId {target})");
        conn.Send(new WAR2D.Net.RemovedFromLobby { Banned = ban, ServerName = DisplayServerName() });
        StartCoroutine(WAR2D.Net.Disconnects.CloseSoon(conn, c => c.Disconnect()));
    }

    /// <summary>A human in the lobby other than the sender (and never the host's own local client), with their connection.</summary>
    private bool TryOtherHuman(uint target, NetworkConnectionToClient sender, out NetworkConnectionToClient conn, out ClientPlayer player)
    {
        conn = null;
        player = null;
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
        {
            if (entry.Key == null || entry.Key.netId != target) continue;
            conn = entry.Value.connection;
            if (conn == null || conn == sender || conn is LocalConnectionToClient || !entry.Key.TryGetComponent(out player)) return false;
            return true;
        }
        return false;
    }
}
