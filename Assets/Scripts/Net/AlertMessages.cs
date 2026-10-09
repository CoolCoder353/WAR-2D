using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>What an alert reports.</summary>
public enum AlertKind : byte
{
    /// <summary>One of the receiver's own units or buildings took damage (never says by whom).</summary>
    UnderAttack,
    /// <summary>The receiver's upkeep first went unpaid.</summary>
    UpkeepUnpaid,
    /// <summary>Another player gifted the receiver resources.</summary>
    GiftReceived,
    /// <summary>Another player started sharing vision with the receiver.</summary>
    VisionSharedWithYou,
    /// <summary>Another player stopped sharing vision with the receiver.</summary>
    VisionUnshared,
}

/// <summary>
/// One alert for one player. <see cref="Tile"/> is the receiver's own damaged entity (UnderAttack only).
/// <see cref="OtherOwnerId"/> names only a gifter or a sharer, who the receiver learns of anyway; it is
/// always 0 for UnderAttack, because attack masks are private.
/// </summary>
public struct Alert
{
    public AlertKind Kind;
    public int2 Tile;
    public int OtherOwnerId;
    public float Amount;
}

/// <summary>
/// Server-side throttle: one alert per (owner, kind, area) per window, where an area is a square of
/// <c>areaTiles</c> tiles.
/// </summary>
public sealed class AlertThrottle
{
    private const int PruneAbove = 4096;
    private readonly float seconds;
    private readonly int areaTiles;
    private readonly Dictionary<(int owner, AlertKind kind, int2 area), double> last = new Dictionary<(int, AlertKind, int2), double>();
    private readonly List<(int, AlertKind, int2)> stale = new List<(int, AlertKind, int2)>();

    public AlertThrottle(float seconds, int areaTiles)
    {
        this.seconds = seconds;
        this.areaTiles = Math.Max(1, areaTiles);
    }

    /// <summary>True (and starts the window) when no alert of this kind fired in the tile's area within the window.</summary>
    public bool Allow(int ownerId, AlertKind kind, int2 tile, double now)
    {
        var key = (ownerId, kind, (int2)math.floor((float2)tile / areaTiles));
        if (last.TryGetValue(key, out double at) && now - at < seconds) return false;
        last[key] = now;
        if (last.Count > PruneAbove) Prune(now);
        return true;
    }

    /// <summary>Forgets a player's windows (they left).</summary>
    public void Forget(int ownerId)
    {
        stale.Clear();
        foreach (var key in last.Keys) if (key.owner == ownerId) stale.Add(key);
        foreach (var key in stale) last.Remove(key);
    }

    private void Prune(double now)
    {
        stale.Clear();
        foreach (var pair in last) if (now - pair.Value >= seconds) stale.Add(pair.Key);
        foreach (var key in stale) last.Remove(key);
    }
}

/// <summary>Turns one tick's damage into alerts.</summary>
public static class AlertRules
{
    /// <summary>
    /// For each damaged entity (its owner and its own tile), an UnderAttack alert to that owner only, at
    /// that tile, when the throttle allows. The attacker is never part of the input or the alert.
    /// </summary>
    public static void UnderAttack(IReadOnlyList<(int owner, int2 tile)> damaged, AlertThrottle throttle, double now, Action<int, Alert> emit)
    {
        foreach ((int owner, int2 tile) in damaged)
            if (owner != 0 && throttle.Allow(owner, AlertKind.UnderAttack, tile, now))
                emit(owner, new Alert { Kind = AlertKind.UnderAttack, Tile = tile });
    }
}
