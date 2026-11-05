using Unity.Mathematics;
using System.Collections.Generic;
using Config;
using Mirror;
using UnityEngine;

/// <summary>
/// In-match player actions and the end of a match: surrendering, gifting resources to any other live
/// player, and the match statistics (counted on the server, sent to everyone only once the match is over).
/// </summary>
/// <summary>A match's length from its ticks.</summary>
public static class MatchLength
{
    /// <summary>Seconds of match for <paramref name="ticks"/> ticks.</summary>
    public static float Seconds(int ticks, float tickSeconds) => ticks * tickSeconds;
}

public partial class GameCore
{
    /// <summary>Every player's statistics for the current match (server only).</summary>
    public MatchStats Stats { get; } = new MatchStats();

    /// <summary>The end screen's graphs and timeline, collected during Playing (v0.18, server only).</summary>
    public MatchSummary Summary { get; } = new MatchSummary();

    /// <summary>Server time the match started playing (for the end screen's duration).</summary>
    [SyncVar]
    public double MatchStartTime;

    private readonly Dictionary<int, double> lastGift = new Dictionary<int, double>();

    /// <summary>Leaves the match as if the sender's HQ fell (<see cref="PlayerActions.Surrender"/>).</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_Surrender(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_Surrender)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        PlayerActions.Surrender(owner);
    }

    /// <summary>Chooses whose sight a spectator watches (<see cref="PlayerActions.SetSpectateView"/>).</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetSpectateView(int view, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetSpectateView)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        PlayerActions.SetSpectateView(owner, view);
    }

    /// <summary>Marks a map tile for the sender and everyone the sender shares vision with (<see cref="PlayerActions.Ping"/>).</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_Ping(int2 tile, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_Ping)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        PlayerActions.Ping(owner, tile);
    }

    /// <summary>Gives resources to any other live player, ally or enemy (<see cref="PlayerActions.Gift"/>).</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_GiftResources(uint targetNetId, float amount, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_GiftResources)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        PlayerActions.Gift(owner, BuildingData.UIntToInt(targetNetId), amount);
    }

    /// <summary>
    /// Gives resources from <paramref name="from"/> to any other live player. Taken from the giver at once and added to
    /// the target; one gift per <c>Gifting/CooldownSeconds</c>. Only the two of them are told. Returns whether it was given.
    /// </summary>
    [Server]
    internal bool TryGift(int from, int to, float amount)
    {
        ServerPlayer giver = GetServerPlayerById(from), receiver = GetServerPlayerById(to);
        if (giver == null || receiver == null || giver.Away || receiver.Away) return false; // an away player neither gives nor takes
        if (!lastGift.TryGetValue(from, out double last)) last = double.NegativeInfinity;
        double now = MatchClock.Now; // match time: a pause stops the cooldown
        if (!GiftRules.CanGift(CurrentState, giver.state, receiver.state, from, to, now, last, ConfigLoader.LoadConfig().Gifting.CooldownSeconds)) return false;
        if (!GiftRules.IsValid(amount, giver.Resources) || !giver.TrySpend(amount, countAsSpent: false)) return false;
        receiver.Add(amount);
        lastGift[from] = now;
        Stats.Gift(from, to, amount);
        foreach (int owner in GiftRules.Recipients(from, to)) SendGiftNotice(owner, from, to, amount);
        WorldStateManager.Instance?.Alerts?.Add(to, new Alert { Kind = AlertKind.GiftReceived, OtherOwnerId = from, Amount = amount });
        return true;
    }

    /// <summary>Eliminates the owner as if their HQ fell, while Playing and still in the match. Returns whether they surrendered.</summary>
    [Server]
    internal bool TrySurrender(int owner)
    {
        if (CurrentState != GameState.Playing) return false;
        ServerPlayer player = GetServerPlayerById(owner);
        if (player == null || player.state != PlayerState.Playing) return false;
        EliminatePlayer(owner, EliminationCause.Surrendered);
        return true;
    }

    /// <summary>Tells one party of a gift (sender or recipient only).</summary>
    [Server]
    private void SendGiftNotice(int ownerId, int from, int to, float amount)
    {
        NetworkConnectionToClient connection = GetServerPlayerById(ownerId)?.connection;
        if (connection?.identity != null && connection.identity.TryGetComponent(out ClientPlayer player))
            player.TargetGift(connection, from, to, amount);
    }

    /// <summary>
    /// The host's Rematch (v0.18, Figma E2): everyone still connected goes back to this lobby with their seat, colour and team,
    /// ready cleared; AI seats stay; the seed is new; everything else of the match is gone. Server owner only, once the match is
    /// over and the replay is saved.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_Rematch(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_Rematch))) return;
        WorldStateManager world = WorldStateManager.Instance;
        bool replayPending = world != null && world.Recorder != null && !world.Recorder.Finished;
        if (!RematchRules.May(CurrentState, IsServerOwner(sender), replayPending)) return;
        RpcRematchStarting();
        ResetForRematch();
    }

    /// <summary>The end screen says "Returning to the lobby" until the scene changes, and a rejoin ticket means nothing now.</summary>
    [ClientRpc]
    private void RpcRematchStarting()
    {
        RematchStarting?.Invoke();
        WAR2D.Net.Rejoin.RejoinTicket.Clear();
        if (NetworkClient.localPlayer != null && NetworkClient.localPlayer.TryGetComponent(out ClientPlayer local)) local.ResetForNewMatch();
    }

    /// <summary>Raised on clients when the host presses Rematch.</summary>
    public static event System.Action RematchStarting;

    /// <summary>Back to the lobby with the seats kept. Leaving the match's scene disposes its ECS world, as leaving does.</summary>
    [Server]
    private void ResetForRematch()
    {
        Rejoin.RevokeAll();
        var away = new List<int>();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in ServerPlayers)
            if (entry.Key != null && entry.Value.Away) away.Add(BuildingData.UIntToInt(entry.Key.netId));
        foreach (int owner in away) EndAway(owner, WAR2D.Net.Rejoin.RejoinRefusal.MatchOver);
        ResetMatchState();
        foreach (ClientPlayer player in LobbyClientPlayers()) player.ready = false;
        MatchSettings settings = Settings;
        settings.Seed = NewRematchSeed();
        Settings = settings;
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != LobbyScene) GameManager.Instance.ServerChangeScene(LobbyScene);
    }

    private static uint NewRematchSeed() => (uint)UnityEngine.Random.Range(1, int.MaxValue);

    /// <summary>Starts counting a new match.</summary>
    [Server]
    private void BeginStats()
    {
        Stats.Clear();
        Summary.Begin(Config.ConfigLoader.LoadConfig().Summary);
        summaryEliminated.Clear();
        hqLastHit.Clear();
        hqTiles.Clear();
        lastGift.Clear();
    }

    /// <summary>Sends every player the final statistics. Only once the match is over.</summary>
    [Server]
    private void SendStats(IReadOnlyCollection<int> winners)
    {
        if (!StatsRules.MaySend(CurrentState)) return;
        var owners = new int[PlayerOrder.Count];
        for (int i = 0; i < owners.Length; i++) owners[i] = PlayerOrder[i];
        foreach (int owner in owners)
        {
            ServerPlayer player = GetServerPlayerById(owner);
            if (player != null) Stats.Spent(owner, player.SpentThisMatch);
        }
        var won = new List<int>(winners ?? new int[0]);
        RpcMatchStats(owners, Stats.Snapshot(owners), won.ToArray(), PlayedSeconds(), ResolvedStyles(owners));
        SummaryData summary = BuildSummary();
        WorldStateManager.Instance?.Recorder?.Summary(summary);
        RpcMatchSummary(summary);
    }

    /// <summary>The match's length in simulated time (<see cref="WorldStateManager.PlayedSeconds"/>), whatever a dev <c>step</c> or <c>timescale</c> did to the wall clock.</summary>
    [Server]
    private float PlayedSeconds() =>
        WorldStateManager.Instance != null ? WorldStateManager.Instance.PlayedSeconds : (float)(NetworkTime.time - MatchStartTime);

    /// <summary>The summary with the map's size and each player's HQ, so a client can name places. Public at game over only.</summary>
    [Server]
    private SummaryData BuildSummary()
    {
        WorldStateManager world = WorldStateManager.Instance;
        var owners = new List<int>();
        var xs = new List<float>();
        var ys = new List<float>();
        if (world != null)
            foreach (KeyValuePair<int, Unity.Mathematics.int2> hq in hqTiles)
            {
                owners.Add(hq.Key);
                xs.Add(hq.Value.x);
                ys.Add(hq.Value.y);
            }
        return Summary.Build(world != null && world.Map != null ? world.Map.Grid.Width : 0, owners.ToArray(), xs.ToArray(), ys.ToArray());
    }

    private readonly Dictionary<int, Unity.Mathematics.int2> hqTiles = new Dictionary<int, Unity.Mathematics.int2>();

    /// <summary>Remembers where a player's HQ was placed, so the timeline can say "near their base" after it falls.</summary>
    [Server]
    public void NoteHq(int owner, Unity.Mathematics.int2 tile) => hqTiles[owner] = tile;

    /// <summary>The end screen's graphs and timeline (v0.18), for every player, once the match is over.</summary>
    [ClientRpc]
    public void RpcMatchSummary(SummaryData summary)
    {
        LastMatchSummary = summary;
        MatchSummaryReceived?.Invoke(summary);
    }

    /// <summary>The last match's graphs and timeline on this client.</summary>
    public static SummaryData? LastMatchSummary { get; private set; }

    /// <summary>Raised on clients when the graphs and timeline arrive.</summary>
    public static event System.Action<SummaryData> MatchSummaryReceived;

    /// <summary>
    /// Each owner's played AI style (v0.14; <see cref="AiStyle.Random"/> for humans), by <paramref name="owners"/>. This
    /// is the only message that carries what a Random style became, sent once the match is over.
    /// </summary>
    [Server]
    private AiStyle[] ResolvedStyles(int[] owners)
    {
        var styles = new AiStyle[owners.Length];
        for (int i = 0; i < owners.Length; i++)
            if (AiPlayers.TryGetValue(owners[i], out AiSeat seat)) styles[i] = seat.ResolvedStyle;
        return styles;
    }

    /// <summary>The end screen's statistics, for every player (public once the match is over), with each AI's played style.</summary>
    [ClientRpc]
    public void RpcMatchStats(int[] owners, PlayerStats[] stats, int[] winners, float durationSeconds, AiStyle[] aiStyles)
    {
        LastMatchStats = new MatchResult { Owners = owners, Stats = stats, Winners = winners, DurationSeconds = durationSeconds, AiStyles = aiStyles };
        MatchStatsReceived?.Invoke(LastMatchStats);
    }

    /// <summary>The last match's result on this client (null until the match is over).</summary>
    public static MatchResult LastMatchStats { get; private set; }

    /// <summary>Raised on clients when the final statistics arrive.</summary>
    public static event System.Action<MatchResult> MatchStatsReceived;

    /// <summary>Forgets the last result (a new match starts).</summary>
    public static void ClearMatchStats()
    {
        LastMatchStats = null;
        LastMatchSummary = null;
    }
}

/// <summary>The final result of a match as clients receive it.</summary>
public sealed class MatchResult
{
    public int[] Owners;
    public PlayerStats[] Stats;
    public int[] Winners;
    public float DurationSeconds;
    /// <summary>Each owner's played AI style (by <see cref="Owners"/>; Random for humans), revealed at GameOver.</summary>
    public AiStyle[] AiStyles;

    /// <summary>What <paramref name="owner"/>'s AI played; Random for a human or when unknown.</summary>
    public AiStyle PlayedStyle(int owner)
    {
        if (Owners == null || AiStyles == null) return AiStyle.Random;
        int i = System.Array.IndexOf(Owners, owner);
        return i >= 0 && i < AiStyles.Length ? AiStyles[i] : AiStyle.Random;
    }
}

/// <summary>When match statistics may leave the server. Pure.</summary>
public static class StatsRules
{
    /// <summary>Only once the match is over, so nobody learns another player's numbers during play.</summary>
    public static bool MaySend(GameState state) => state == GameState.GameOver;
}
