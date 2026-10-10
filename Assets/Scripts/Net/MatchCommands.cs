using System.Collections.Generic;
using Config;
using Mirror;
using UnityEngine;

/// <summary>
/// In-match player actions and the end of a match: surrendering, gifting resources to any other live
/// player, and the match statistics (counted on the server, sent to everyone only once the match is over).
/// </summary>
public partial class GameCore
{
    /// <summary>Every player's statistics for the current match (server only).</summary>
    public MatchStats Stats { get; } = new MatchStats();

    /// <summary>Server time the match started playing (for the end screen's duration).</summary>
    [SyncVar]
    public double MatchStartTime;

    private readonly Dictionary<int, double> lastGift = new Dictionary<int, double>();

    /// <summary>Leaves the match as if the sender's HQ fell: everything they own is destroyed and they are out.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_Surrender(NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_Surrender))) return;
        if (CurrentState != GameState.Playing || sender?.identity == null) return;
        int owner = BuildingData.UIntToInt(sender.identity.netId);
        ServerPlayer player = GetServerPlayerById(owner);
        if (player == null || player.state != PlayerState.Playing) return;
        EliminatePlayer(owner);
    }

    /// <summary>
    /// Gives resources to any other live player (ally or enemy). Taken from the sender at once and added to
    /// the target; one gift per <c>Gifting/CooldownSeconds</c>. Only the two of them are told.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void Cmd_GiftResources(uint targetNetId, float amount, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_GiftResources))) return;
        if (sender?.identity == null) return;
        int from = BuildingData.UIntToInt(sender.identity.netId), to = BuildingData.UIntToInt(targetNetId);
        ServerPlayer giver = GetServerPlayerById(from), receiver = GetServerPlayerById(to);
        if (giver == null || receiver == null) return;
        if (!lastGift.TryGetValue(from, out double last)) last = double.NegativeInfinity;
        double now = NetworkTime.localTime;
        if (!GiftRules.CanGift(CurrentState, giver.state, receiver.state, from, to, now, last, ConfigLoader.LoadConfig().Gifting.CooldownSeconds)) return;
        if (!GiftRules.IsValid(amount, giver.Resources) || !giver.TrySpend(amount, countAsSpent: false)) return;
        receiver.Add(amount);
        lastGift[from] = now;
        Stats.Gift(from, to, amount);
        foreach (int owner in GiftRules.Recipients(from, to)) SendGiftNotice(owner, from, to, amount);
        WorldStateManager.Instance?.Alerts?.Add(to, new Alert { Kind = AlertKind.GiftReceived, OtherOwnerId = from, Amount = amount });
    }

    /// <summary>Tells one party of a gift (sender or recipient only).</summary>
    [Server]
    private void SendGiftNotice(int ownerId, int from, int to, float amount)
    {
        NetworkConnectionToClient connection = GetServerPlayerById(ownerId)?.connection;
        if (connection?.identity != null && connection.identity.TryGetComponent(out ClientPlayer player))
            player.TargetGift(connection, from, to, amount);
    }

    /// <summary>Starts counting a new match.</summary>
    [Server]
    private void BeginStats()
    {
        Stats.Clear();
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
        RpcMatchStats(owners, Stats.Snapshot(owners), won.ToArray(), (float)(NetworkTime.time - MatchStartTime));
    }

    /// <summary>The end screen's statistics, for every player (public once the match is over).</summary>
    [ClientRpc]
    public void RpcMatchStats(int[] owners, PlayerStats[] stats, int[] winners, float durationSeconds)
    {
        LastMatchStats = new MatchResult { Owners = owners, Stats = stats, Winners = winners, DurationSeconds = durationSeconds };
        MatchStatsReceived?.Invoke(LastMatchStats);
    }

    /// <summary>The last match's result on this client (null until the match is over).</summary>
    public static MatchResult LastMatchStats { get; private set; }

    /// <summary>Raised on clients when the final statistics arrive.</summary>
    public static event System.Action<MatchResult> MatchStatsReceived;

    /// <summary>Forgets the last result (a new match starts).</summary>
    public static void ClearMatchStats() => LastMatchStats = null;
}

/// <summary>The final result of a match as clients receive it.</summary>
public sealed class MatchResult
{
    public int[] Owners;
    public PlayerStats[] Stats;
    public int[] Winners;
    public float DurationSeconds;
}

/// <summary>When match statistics may leave the server. Pure.</summary>
public static class StatsRules
{
    /// <summary>Only once the match is over, so nobody learns another player's numbers during play.</summary>
    public static bool MaySend(GameState state) => state == GameState.GameOver;
}
