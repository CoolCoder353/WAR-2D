using System.Collections.Generic;
using Config;
using Mirror;
using UnityEngine;
using WAR2D.Dev.Logging;

/// <summary>
/// Owner ids for AI players (v0.14): a reserved range at the top of the <c>int</c> range. Mirror hands out
/// <c>netId</c>s counting up from 1, so a human's owner id (its <c>netId</c>) would need two billion spawns to
/// reach it (<c>AiSeatTests.AiOwnerIdsNeverMeetMirrorNetIds</c>).
/// </summary>
public static class AiOwnerIds
{
    /// <summary>The first AI owner id.</summary>
    public const int First = 0x7FFF0000;

    /// <summary>How many AI owner ids there are (up to <c>int.MaxValue - 1</c>).</summary>
    public const int Count = 0xFFFF;

    /// <summary>True for an owner id in the AI range.</summary>
    public static bool IsAi(int ownerId) => ownerId >= First && ownerId - First < Count;

    /// <summary>The AI owner id at <paramref name="index"/> (0-based) of the range.</summary>
    public static int For(int index) => First + index;
}

/// <summary>
/// An AI player's brain, implemented in the <c>WAR2D.Ai</c> assembly (which references this one, so this one can't
/// reference it back). Scripted seats have none.
/// </summary>
public interface IAiBrain
{
    /// <summary>Seconds between two of its thinks (its difficulty's <c>ThinkSeconds</c>).</summary>
    float ThinkSeconds { get; }

    /// <summary>
    /// One think: reads its <see cref="WAR2D.AiView.TeamView"/> (everything it may know, built just now) and acts only
    /// through <see cref="PlayerActions"/>. Main thread, on the settled world.
    /// </summary>
    void Think(WAR2D.AiView.TeamView view);
}

/// <summary>A brain that can describe what it is doing (dev only: the control API's <c>aistate</c>).</summary>
public interface IAiBrainStatus
{
    /// <summary>Its plans and counts as plain values (serialised to JSON by the control API).</summary>
    System.Collections.Generic.Dictionary<string, object> Status();
}

/// <summary>Where <c>WAR2D.Ai</c> registers itself at load: the game creates each seat's brain through this, never by naming the AI types.</summary>
public static class AiBrains
{
    /// <summary>Makes a brain for a seat at match start (its style resolved); null until the AI assembly registers, and never called for Scripted seats.</summary>
    public static System.Func<AiSeat, IAiBrain> Factory;

    /// <summary>Makes the director that schedules the brains' thinks (one per match); null until the AI assembly registers.</summary>
    public static System.Func<WAR2D.AiView.IAiDirector> Director;
}

/// <summary>One AI player on the server: its connectionless <see cref="ServerPlayer"/>, its settings and its server-owned player object.</summary>
public sealed class AiSeat
{
    public AiSeat(int ownerId, ServerPlayer player)
    {
        OwnerId = ownerId;
        Player = player;
    }

    /// <summary>Its owner id (from <see cref="AiOwnerIds"/>).</summary>
    public int OwnerId { get; }

    /// <summary>Its resources, state and start options, exactly like a human's (no connection).</summary>
    public ServerPlayer Player { get; }

    public AiDifficulty Difficulty;

    /// <summary>The style the host picked (Random allowed).</summary>
    public AiStyle Style;

    /// <summary>What it plays: <see cref="Style"/>, or what Random resolved to at match start. Server only until GameOver.</summary>
    public AiStyle ResolvedStyle;

    /// <summary>Its callsign (its nickname everywhere a player name appears).</summary>
    public string Callsign;

    /// <summary>Its palette colour (0–7).</summary>
    public int Colour;

    /// <summary>Its lobby team choice (<see cref="TeamRules.NoTeam"/> for its own).</summary>
    public int LobbyTeam = TeamRules.NoTeam;

    /// <summary>Its server-owned player object (no connection), or null when the server has no network running.</summary>
    public ClientPlayer Client;

    /// <summary>Its brain; null for Scripted seats (and until the AI assembly provides one).</summary>
    public IAiBrain Brain;

    /// <summary>True for the dev-only seat the perf harness and tests drive.</summary>
    public bool IsScripted => Difficulty == AiDifficulty.Scripted;
}

/// <summary>Draws AI callsigns: a shuffle of the config's list by the lobby seed, so a reroll changes them. Pure.</summary>
public static class AiCallsigns
{
    /// <summary>The first callsign of the seed's shuffle nobody has, or null when all are taken.</summary>
    public static string Draw(IReadOnlyList<string> callsigns, uint seed, ICollection<string> taken)
    {
        if (callsigns == null || callsigns.Count == 0) return null;
        var order = new List<string>(callsigns);
        var rng = new System.Random(unchecked((int)seed));
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        foreach (string name in order) if (taken == null || !taken.Contains(name)) return name;
        return null;
    }
}

/// <summary>
/// AI players (v0.14): connectionless players with a seat, counted in the player order, teams, diplomacy, upkeep,
/// income, win/loss and statistics like a human. Each has a server-owned <see cref="ClientPlayer"/> so every HUD
/// path shows it as a player (one networked object per player, not per entity).
/// </summary>
public partial class GameCore
{
    /// <summary>AI players by owner id (server only). Lobby AIs and, in dev runs, Scripted seats.</summary>
    public readonly Dictionary<int, AiSeat> AiPlayers = new Dictionary<int, AiSeat>();

    /// <summary>Humans and AIs, as owner ids with their server records.</summary>
    public IEnumerable<(int owner, ServerPlayer player)> AllPlayers()
    {
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
            if (entry.Key != null) yield return (BuildingData.UIntToInt(entry.Key.netId), entry.Value);
        foreach (KeyValuePair<int, AiSeat> seat in AiPlayers) yield return (seat.Key, seat.Value.Player);
    }

    /// <summary>An owner's player object: a human's identity's, or an AI's server-owned one. Null when gone.</summary>
    [Server]
    public ClientPlayer ClientPlayerOf(int ownerId)
    {
        if (AiPlayers.TryGetValue(ownerId, out AiSeat seat)) return seat.Client;
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null && identity.netId == (uint)ownerId) return identity.GetComponent<ClientPlayer>();
        return null;
    }

    /// <summary>True when the owner's HQ is placed (from its player object).</summary>
    [Server]
    public bool HasPlacedHq(int ownerId)
    {
        ClientPlayer player = ClientPlayerOf(ownerId);
        return player != null && player.hasPlacedHQ;
    }

    /// <summary>Humans plus AIs in the lobby (the seat limit counts both).</summary>
    public int SeatCount => ServerPlayers.Count + AiPlayers.Count;

    /// <summary>
    /// Adds an AI seat in the lobby (host only: the lobby Commands check that, plan Task 3). Refused outside the lobby,
    /// for Scripted (dev only), past <see cref="LobbyRules.MaxPlayers"/> seats, for a taken colour or a team the mode
    /// doesn't offer. <paramref name="colour"/> -1 takes the first free one. Clears everyone's ready. Returns the seat, or null.
    /// </summary>
    [Server]
    public AiSeat AddAi(AiDifficulty difficulty, AiStyle style, int colour = -1, int lobbyTeam = TeamRules.NoTeam)
    {
        if (CurrentState != GameState.Lobby || !ConfigLoader.IsValid) return null;
        if (difficulty == AiDifficulty.Scripted || !System.Enum.IsDefined(typeof(AiDifficulty), difficulty) || !System.Enum.IsDefined(typeof(AiStyle), style)) return null;
        if (SeatCount >= LobbyRules.MaxPlayers) return null;
        List<int> taken = TakenColours(null);
        if (colour < 0) colour = LobbyRules.FirstFreeColour(taken);
        if (colour < 0 || !LobbyRules.IsColourFree(colour, taken)) return null;
        if (Settings.Mode == MatchMode.FreeForAll) lobbyTeam = TeamRules.NoTeam;
        if (!LobbyRules.IsTeamChoiceValid(Settings.Mode, lobbyTeam)) return null;
        int owner = NextAiOwnerId();
        string callsign = DrawCallsign();
        if (owner == 0 || callsign == null) return null;

        var seat = new AiSeat(owner, new ServerPlayer(null, Settings.StartingResources))
        {
            Difficulty = difficulty, Style = style, ResolvedStyle = style, Callsign = callsign, Colour = colour, LobbyTeam = lobbyTeam,
        };
        AiPlayers[owner] = seat;
        SpawnAiClient(seat);
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
        WarLog.Info(LogCategory.Lobby, $"AI {callsign} ({difficulty}, {style}) added as owner {owner}");
        return seat;
    }

    /// <summary>
    /// Changes a lobby AI's difficulty, style, colour and team (host only: <see cref="Cmd_SetAi"/> checks that). Refused
    /// outside the lobby, for an unknown or Scripted seat, Scripted or unknown enums, a colour another player or AI holds,
    /// or a team the mode doesn't offer (Free-for-all is Solo only). A change clears everyone's ready. True when applied.
    /// </summary>
    [Server]
    public bool SetAi(int ownerId, AiDifficulty difficulty, AiStyle style, int colour, int lobbyTeam)
    {
        if (CurrentState != GameState.Lobby || !AiPlayers.TryGetValue(ownerId, out AiSeat seat) || seat.IsScripted) return false;
        if (!AiLobbyRules.IsValidChoice(difficulty, style)) return false;
        var taken = new List<int>();
        foreach (ClientPlayer player in LobbyClientPlayers()) taken.Add(player.colourIndex);
        foreach (AiSeat other in AiPlayers.Values) if (other != seat) taken.Add(other.Colour);
        if (!LobbyRules.IsColourFree(colour, taken)) return false;
        if (Settings.Mode == MatchMode.FreeForAll && lobbyTeam != TeamRules.NoTeam) return false;
        if (!LobbyRules.IsTeamChoiceValid(Settings.Mode, lobbyTeam)) return false;
        if (seat.Difficulty == difficulty && seat.Style == style && seat.Colour == colour && seat.LobbyTeam == lobbyTeam) return true;

        seat.Difficulty = difficulty;
        seat.Style = style;
        seat.ResolvedStyle = style; // Random is resolved again at match start
        seat.Colour = colour;
        seat.LobbyTeam = lobbyTeam;
        if (seat.Client != null)
        {
            seat.Client.aiDifficulty = difficulty;
            seat.Client.aiStyle = style; // the picked style: Random stays Random on the wire
            seat.Client.colourIndex = colour;
            seat.Client.lobbyTeam = lobbyTeam;
        }
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
        return true;
    }

    /// <summary>
    /// Draws every lobby AI's callsign again from the lobby seed (a reroll or a new seed), in owner-id order, without
    /// repeats. Scripted seats keep theirs.
    /// </summary>
    [Server]
    private void RedrawCallsigns()
    {
        if (!ConfigLoader.IsValid || AiPlayers.Count == 0) return;
        var owners = new List<int>(AiPlayers.Keys);
        owners.Sort();
        var taken = new HashSet<string>();
        foreach (int owner in owners) if (AiPlayers[owner].IsScripted) taken.Add(AiPlayers[owner].Callsign);
        foreach (int owner in owners)
        {
            AiSeat seat = AiPlayers[owner];
            if (seat.IsScripted) continue;
            string callsign = AiCallsigns.Draw(ConfigLoader.LoadConfig().Ai.Callsigns, Settings.Seed, taken);
            if (callsign == null) continue;
            taken.Add(callsign);
            seat.Callsign = callsign;
            if (seat.Client != null)
            {
                seat.Client.nickname = callsign;
                seat.Client.gameObject.name = $"AI {callsign}";
            }
        }
    }

    /// <summary>Removes an AI seat in the lobby (host only, checked by the lobby Command). Clears everyone's ready.</summary>
    [Server]
    public bool RemoveAi(int ownerId)
    {
        if (CurrentState != GameState.Lobby || !AiPlayers.TryGetValue(ownerId, out AiSeat seat) || seat.IsScripted) return false;
        AiPlayers.Remove(ownerId);
        DestroyAiClient(seat);
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
        return true;
    }

    /// <summary>
    /// Dev only (perf harness, control API, tests): adds a Scripted seat (no brain) mid-match, with its own team,
    /// counted in the player order and the match's player count. Refused unless dev APIs are enabled, for an owner id
    /// outside <see cref="AiOwnerIds"/>, or one already used.
    /// </summary>
    [Server]
    internal AiSeat DevAddScriptedAi(int ownerId, float startingResources)
    {
        if (!DevApi.Allowed || !AiOwnerIds.IsAi(ownerId) || AiPlayers.ContainsKey(ownerId)) return null;
        var seat = new AiSeat(ownerId, new ServerPlayer(null, startingResources))
        {
            Difficulty = AiDifficulty.Scripted, Style = AiStyle.Random, ResolvedStyle = AiStyle.Random,
            Callsign = DrawCallsign() ?? $"Bot {ownerId - AiOwnerIds.First}",
        };
        int colour = LobbyRules.FirstFreeColour(TakenColours(null));
        seat.Colour = colour < 0 ? PlayerOrder.Count % LobbyRules.Colours : colour;
        AiPlayers[ownerId] = seat;
        PlayerOrder.Add(ownerId);
        MatchStartPlayerCount++;
        int team = 0;
        foreach (int t in Teams.Values) team = Mathf.Max(team, t + 1);
        Teams[ownerId] = team; // Scripted seats are free-for-all
        MatchStartTeamCount = TeamRules.CountTeams(Teams.Values);
        SpawnAiClient(seat);
        // Humans learn the new seat's diplomacy bit once it has a simulation slot (its first entity).
        if (CurrentState == GameState.Playing) diplomacyResendPending = true;
        return seat;
    }

    private bool diplomacyResendPending;

    /// <summary>Resends every row once each AI added mid-match has a simulation slot (main thread, from Update).</summary>
    [Server]
    private void ResendDiplomacyWhenSlotted()
    {
        if (!diplomacyResendPending || CurrentState != GameState.Playing) return;
        WAR2D.Sim.SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim == null) return;
        foreach (int owner in AiPlayers.Keys) if (!sim.TrySlotOf(owner, out _)) return;
        diplomacyResendPending = false;
        SendAllDiplomacy();
    }

    /// <summary>The lowest unused AI owner id, or 0 when the range is full.</summary>
    private int NextAiOwnerId()
    {
        for (int i = 0; i < AiOwnerIds.Count; i++)
            if (!AiPlayers.ContainsKey(AiOwnerIds.For(i))) return AiOwnerIds.For(i);
        return 0;
    }

    /// <summary>A callsign no AI in this server has, drawn with the lobby seed.</summary>
    private string DrawCallsign()
    {
        if (!ConfigLoader.IsValid) return null;
        var taken = new HashSet<string>();
        foreach (AiSeat seat in AiPlayers.Values) taken.Add(seat.Callsign);
        return AiCallsigns.Draw(ConfigLoader.LoadConfig().Ai.Callsigns, Settings.Seed, taken);
    }

    /// <summary>Colours humans and AIs hold, except <paramref name="except"/>'s.</summary>
    private List<int> TakenColours(ClientPlayer except)
    {
        var taken = new List<int>();
        foreach (ClientPlayer player in LobbyClientPlayers()) if (player != except) taken.Add(player.colourIndex);
        foreach (AiSeat seat in AiPlayers.Values) if (seat.Client == null || seat.Client != except) taken.Add(seat.Colour);
        return taken;
    }

    /// <summary>Spawns the seat's server-owned player object (no connection), when the server's network runs.</summary>
    [Server]
    private void SpawnAiClient(AiSeat seat)
    {
        GameObject prefab = GameManager.Instance != null ? GameManager.Instance.playerPrefab : null;
        if (!NetworkServer.active || prefab == null) return;
        GameObject go = Instantiate(prefab);
        go.name = $"AI {seat.Callsign}";
        ClientPlayer client = go.GetComponent<ClientPlayer>();
        client.isAi = true;
        client.aiOwnerId = seat.OwnerId;
        client.aiDifficulty = seat.Difficulty;
        client.aiStyle = seat.Style;
        client.nickname = seat.Callsign;
        client.colourIndex = seat.Colour;
        client.lobbyTeam = seat.LobbyTeam;
        client.ready = true; // an AI is always ready
        client.hasPlacedHQ = false;
        NetworkServer.Spawn(go);
        seat.Client = client;
    }

    [Server]
    private static void DestroyAiClient(AiSeat seat)
    {
        if (seat.Client != null && NetworkServer.active) NetworkServer.Destroy(seat.Client.gameObject);
        seat.Client = null;
    }

    /// <summary>Drops every AI seat and its player object (the server stops, or returns to an empty lobby).</summary>
    [Server]
    private void ClearAiPlayers()
    {
        foreach (AiSeat seat in AiPlayers.Values) DestroyAiClient(seat);
        AiPlayers.Clear();
        diplomacyResendPending = false;
    }

    /// <summary>Match start: every AI plays, from the lobby's resources, its Random style resolved with server randomness, its brain made.</summary>
    [Server]
    private void StartAiSeats()
    {
        AiStyle[] styles = { AiStyle.Rusher, AiStyle.Turtle, AiStyle.Expander, AiStyle.Balanced };
        foreach (AiSeat seat in AiPlayers.Values)
        {
            seat.Player.state = PlayerState.Playing;
            seat.Player.ResetResources(Settings.StartingResources);
            seat.Player.StartOptions = null;
            seat.Player.StartPick = -1;
            if (seat.Client != null) seat.Client.hasPlacedHQ = false;
            seat.ResolvedStyle = seat.Style != AiStyle.Random ? seat.Style
                : styles[System.Security.Cryptography.RandomNumberGenerator.GetInt32(styles.Length)];
            seat.Brain = seat.IsScripted ? null : AiBrains.Factory?.Invoke(seat);
        }
    }

    /// <summary>An AI's HQ went up (any path: its actions, a random placement, or the dev harness): its object shows it alive.</summary>
    [Server]
    internal void OnAiHqCreated(int ownerId)
    {
        if (AiPlayers.TryGetValue(ownerId, out AiSeat seat) && seat.Client != null && seat.Player.state == PlayerState.Playing) seat.Client.hasPlacedHQ = true;
    }
}
