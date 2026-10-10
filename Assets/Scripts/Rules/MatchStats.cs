using System;
using System.Collections.Generic;

/// <summary>One player's match statistics (shown on the end screen; sent only once the match is over).</summary>
[Serializable]
public struct PlayerStats
{
    public int UnitsBuilt, UnitsLost, UnitsKilled, BuildingsBuilt, BuildingsLost;
    public float Mined, Spent, GiftedOut, GiftedIn;
    public int PeakArmy;
}

/// <summary>Counts each player's statistics during a match (server only). Pure.</summary>
public sealed class MatchStats
{
    private readonly Dictionary<int, PlayerStats> byOwner = new Dictionary<int, PlayerStats>();

    /// <summary>Forgets everything (a new match).</summary>
    public void Clear() => byOwner.Clear();

    public void UnitBuilt(int owner) => Edit(owner, (ref PlayerStats s) => s.UnitsBuilt++);

    /// <summary>A unit died; <paramref name="killerOwner"/> (if another player, not 0) gets the kill.</summary>
    public void UnitDied(int owner, int killerOwner)
    {
        Edit(owner, (ref PlayerStats s) => s.UnitsLost++);
        if (killerOwner != 0 && killerOwner != owner) Edit(killerOwner, (ref PlayerStats s) => s.UnitsKilled++);
    }

    public void BuildingBuilt(int owner) => Edit(owner, (ref PlayerStats s) => s.BuildingsBuilt++);
    public void BuildingLost(int owner) => Edit(owner, (ref PlayerStats s) => s.BuildingsLost++);
    public void Mined(int owner, float amount) { if (amount > 0f) Edit(owner, (ref PlayerStats s) => s.Mined += amount); }
    public void Spent(int owner, float amount) { if (amount > 0f) Edit(owner, (ref PlayerStats s) => s.Spent += amount); }

    public void Gift(int from, int to, float amount)
    {
        Edit(from, (ref PlayerStats s) => s.GiftedOut += amount);
        Edit(to, (ref PlayerStats s) => s.GiftedIn += amount);
    }

    /// <summary>Records an army size; keeps the largest.</summary>
    public void Army(int owner, int units) => Edit(owner, (ref PlayerStats s) => s.PeakArmy = Math.Max(s.PeakArmy, units));

    /// <summary>One player's statistics so far.</summary>
    public PlayerStats Of(int owner) => byOwner.TryGetValue(owner, out PlayerStats s) ? s : default;

    /// <summary>The statistics of the given players, in the same order.</summary>
    public PlayerStats[] Snapshot(IReadOnlyList<int> owners)
    {
        var result = new PlayerStats[owners.Count];
        for (int i = 0; i < owners.Count; i++) result[i] = Of(owners[i]);
        return result;
    }

    private delegate void Change(ref PlayerStats stats);

    private void Edit(int owner, Change change)
    {
        byOwner.TryGetValue(owner, out PlayerStats s);
        change(ref s);
        byOwner[owner] = s;
    }
}
