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
        };
    }

    private static uint NewSeed() => (uint)UnityEngine.Random.Range(1, int.MaxValue);

    /// <summary>Replaces the match settings. Server owner only, in the lobby, and only with values the lobby offers. Clears every ready flag.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetMatchSettings(MatchSettings settings, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetMatchSettings))) return;
        if (!IsServerOwner(sender) || CurrentState != GameState.Lobby) return;
        if (!MatchSettingsRules.IsValid(settings, ConfigLoader.LoadConfig().Lobby)) return;
        Settings = settings;
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
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
    }

    /// <summary>Takes a palette colour nobody else has, in the lobby.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetColour(byte colourIndex, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetColour))) return;
        if (CurrentState != GameState.Lobby || !TryLobbyPlayer(sender, out ClientPlayer me)) return;
        if (LobbyRules.IsColourFree(colourIndex, TakenColours(me))) me.colourIndex = colourIndex;
    }

    /// <summary>Marks the sender ready (or not), in the lobby.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetReady(bool ready, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetReady))) return;
        if (CurrentState != GameState.Lobby || !TryLobbyPlayer(sender, out ClientPlayer me)) return;
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
}
