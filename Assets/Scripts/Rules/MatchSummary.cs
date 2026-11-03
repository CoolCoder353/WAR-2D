using System;
using System.Collections.Generic;

/// <summary>How a player left the match, for the end screen's timeline.</summary>
public enum EliminationCause : byte { HqDestroyed, Surrendered, Left, TimedOut }

/// <summary>One player's graphs: a value per sample in <see cref="SummaryData.Times"/>.</summary>
[Serializable]
public struct SummaryPlayer
{
    public int Owner;
    /// <summary>The summed cost of their living units.</summary>
    public float[] Army;
    /// <summary>Income per minute over the interval (passive plus Miners; gifts don't count).</summary>
    public float[] Income;
    /// <summary>Cumulative units killed and lost.</summary>
    public int[] Killed, Lost;
}

/// <summary>A player leaving the match.</summary>
[Serializable]
public struct SummaryElimination
{
    public float Time;
    public int Owner;
    public EliminationCause Cause;
    /// <summary>Who destroyed the HQ (0 when not known).</summary>
    public int By;
}

/// <summary>A big fight: when and where, and what each player in it lost and killed.</summary>
[Serializable]
public struct SummaryFight
{
    public float Start, End;
    /// <summary>The cost-weighted mean of the deaths, in tiles.</summary>
    public float CentreX, CentreY;
    public int[] Owners;
    /// <summary>By <see cref="Owners"/>.</summary>
    public int[] UnitsLost;
    public float[] WorthLost, WorthKilled;
}

/// <summary>The end screen's graphs and timeline (v0.18). Sent to everyone only at game over, like the statistics.</summary>
[Serializable]
public struct SummaryData
{
    /// <summary>Match seconds of each sample.</summary>
    public float[] Times;
    public SummaryPlayer[] Players;
    public SummaryElimination[] Eliminations;
    public SummaryFight[] Fights;
    /// <summary>The map's side in tiles and each player's HQ tile (x, y pairs by <see cref="HqOwners"/>), for words like "near IronFox's base".</summary>
    public int MapSize;
    public int[] HqOwners;
    public float[] HqX, HqY;
}

/// <summary>
/// Collects the graphs and timeline during Playing, on the match clock so a pause adds nothing (server only). Every
/// <c>Summary/SampleSeconds</c>, one row per player; at <c>Summary/MaxPoints</c> neighbouring pairs merge (averages for income,
/// the later value for the rest) and the interval doubles, so a long match keeps its whole shape. Pure.
/// </summary>
public sealed class MatchSummary
{
    private sealed class Row
    {
        public readonly List<float> Army = new List<float>(), Income = new List<float>();
        public readonly List<int> Killed = new List<int>(), Lost = new List<int>();
        public float IncomeSoFar;
    }

    private readonly List<float> times = new List<float>();
    private readonly Dictionary<int, Row> rows = new Dictionary<int, Row>();
    private readonly List<SummaryElimination> eliminations = new List<SummaryElimination>();
    private FightDetector fights;
    private float interval;
    private int maxPoints;
    private double lastSample;

    /// <summary>Starts a new match.</summary>
    public void Begin(Config.SummaryConfig config)
    {
        times.Clear();
        rows.Clear();
        eliminations.Clear();
        interval = config.SampleSeconds;
        maxPoints = config.MaxPoints;
        lastSample = 0;
        fights = new FightDetector(config.FightCellTiles, config.FightWindowSeconds, config.FightMinWorth, config.MaxFights);
    }

    private Row RowOf(int owner)
    {
        if (!rows.TryGetValue(owner, out Row row))
        {
            row = new Row();
            // A player first seen late (no income yet) still lines up with the times.
            for (int i = 0; i < times.Count; i++) { row.Army.Add(0f); row.Income.Add(0f); row.Killed.Add(0); row.Lost.Add(0); }
            rows[owner] = row;
        }
        return row;
    }

    /// <summary>Resources a player earned (passive income and Miner payouts, not gifts).</summary>
    public void Income(int owner, float amount) => RowOf(owner).IncomeSoFar += amount;

    /// <summary>A unit died at match time <paramref name="now"/>; its worth feeds the fight detector.</summary>
    public void Death(double now, int owner, int killer, float worth, float x, float y) => fights?.Death((float)now, owner, killer, worth, x, y);

    /// <summary>A player left the match.</summary>
    public void Eliminated(double now, int owner, EliminationCause cause, int by = 0) =>
        eliminations.Add(new SummaryElimination { Time = (float)now, Owner = owner, Cause = cause, By = by });

    /// <summary>True when a sample is due at match time <paramref name="now"/>.</summary>
    public bool SampleDue(double now) => now - lastSample >= interval;

    /// <summary>Records one sample, taking each player's numbers from the callback.</summary>
    public void Sample(double now, IReadOnlyList<int> owners, Func<int, (float army, int killed, int lost)> numbers)
    {
        float seconds = (float)Math.Max(0.001, now - lastSample);
        lastSample = now;
        foreach (int owner in owners) RowOf(owner);
        times.Add((float)now);
        foreach (KeyValuePair<int, Row> pair in rows)
        {
            (float army, int killed, int lost) = numbers(pair.Key);
            Row row = pair.Value;
            row.Army.Add(army);
            row.Income.Add(row.IncomeSoFar * 60f / seconds);
            row.Killed.Add(killed);
            row.Lost.Add(lost);
            row.IncomeSoFar = 0f;
        }
        if (times.Count >= maxPoints) Merge();
    }

    private void Merge()
    {
        MergePairs(times, (a, b) => b);
        foreach (Row row in rows.Values)
        {
            MergePairs(row.Army, (a, b) => b);
            MergePairs(row.Income, (a, b) => (a + b) * 0.5f);
            MergePairs(row.Killed, (a, b) => b);
            MergePairs(row.Lost, (a, b) => b);
        }
        interval *= 2f;
    }

    private static void MergePairs<T>(List<T> values, Func<T, T, T> merge)
    {
        int kept = 0;
        for (int i = 0; i + 1 < values.Count; i += 2) values[kept++] = merge(values[i], values[i + 1]);
        if (values.Count % 2 == 1) values[kept++] = values[values.Count - 1];
        values.RemoveRange(kept, values.Count - kept);
    }

    /// <summary>Everything collected, closing any fight still open.</summary>
    public SummaryData Build(int mapSize, int[] hqOwners, float[] hqX, float[] hqY)
    {
        var players = new List<SummaryPlayer>(rows.Count);
        foreach (KeyValuePair<int, Row> pair in rows)
            players.Add(new SummaryPlayer { Owner = pair.Key, Army = pair.Value.Army.ToArray(), Income = pair.Value.Income.ToArray(), Killed = pair.Value.Killed.ToArray(), Lost = pair.Value.Lost.ToArray() });
        return new SummaryData
        {
            Times = times.ToArray(),
            Players = players.ToArray(),
            Eliminations = eliminations.ToArray(),
            Fights = fights != null ? fights.Finish() : new SummaryFight[0],
            MapSize = mapSize,
            HqOwners = hqOwners ?? new int[0],
            HqX = hqX ?? new float[0],
            HqY = hqY ?? new float[0],
        };
    }
}

/// <summary>
/// Finds big fights in the stream of unit deaths. Deaths are binned into square cells. A fight opens when the cost of the units
/// killed in a cell and its 8 neighbours within the window reaches the threshold, and closes after a window with no deaths there.
/// At most <c>Summary/MaxFights</c> are kept, the largest by total worth lost. Pure.
/// </summary>
public sealed class FightDetector
{
    private struct DeathEntry { public float Time, Worth, X, Y; public int Owner, Killer; }

    private sealed class Fight
    {
        public float Start, End, SumX, SumY, Total;
        public readonly HashSet<(int, int)> Cells = new HashSet<(int, int)>();
        public readonly Dictionary<int, (int units, float lost, float killed)> ByOwner = new Dictionary<int, (int, float, float)>();

        public void Add(in DeathEntry d)
        {
            End = Math.Max(End, d.Time);
            SumX += d.X * d.Worth;
            SumY += d.Y * d.Worth;
            Total += d.Worth;
            ByOwner.TryGetValue(d.Owner, out var o);
            ByOwner[d.Owner] = (o.units + 1, o.lost + d.Worth, o.killed);
            if (d.Killer != 0 && d.Killer != d.Owner)
            {
                ByOwner.TryGetValue(d.Killer, out var k);
                ByOwner[d.Killer] = (k.units, k.lost, k.killed + d.Worth);
            }
        }
    }

    private readonly int cellTiles, maxFights;
    private readonly float window, minWorth;
    private readonly Dictionary<(int, int), List<DeathEntry>> recent = new Dictionary<(int, int), List<DeathEntry>>();
    private readonly List<Fight> open = new List<Fight>();
    private readonly List<Fight> closed = new List<Fight>();

    public FightDetector(int cellTiles, float windowSeconds, float minWorth, int maxFights)
    {
        this.cellTiles = Math.Max(1, cellTiles);
        window = windowSeconds;
        this.minWorth = minWorth;
        this.maxFights = maxFights;
    }

    /// <summary>A unit died.</summary>
    public void Death(float now, int owner, int killer, float worth, float x, float y)
    {
        CloseQuiet(now);
        var cell = ((int)Math.Floor(x / cellTiles), (int)Math.Floor(y / cellTiles));
        var entry = new DeathEntry { Time = now, Worth = worth, X = x, Y = y, Owner = owner, Killer = killer };
        Fight fight = OpenFightAt(cell);
        if (fight != null)
        {
            fight.Cells.Add(cell);
            fight.Add(entry);
            return;
        }
        if (!recent.TryGetValue(cell, out List<DeathEntry> list)) recent[cell] = list = new List<DeathEntry>();
        list.Add(entry);

        float sum = 0f;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!recent.TryGetValue((cell.Item1 + dx, cell.Item2 + dy), out List<DeathEntry> near)) continue;
            near.RemoveAll(d => now - d.Time > window);
            foreach (DeathEntry d in near) sum += d.Worth;
        }
        if (sum < minWorth) return;

        fight = new Fight { Start = now, End = now };
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            var c = (cell.Item1 + dx, cell.Item2 + dy);
            if (!recent.TryGetValue(c, out List<DeathEntry> near) || near.Count == 0) continue;
            fight.Cells.Add(c);
            foreach (DeathEntry d in near) { fight.Start = Math.Min(fight.Start, d.Time); fight.Add(d); }
            near.Clear();
        }
        open.Add(fight);
    }

    private Fight OpenFightAt((int, int) cell)
    {
        foreach (Fight fight in open)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (fight.Cells.Contains((cell.Item1 + dx, cell.Item2 + dy))) return fight;
        return null;
    }

    private void CloseQuiet(float now)
    {
        for (int i = open.Count - 1; i >= 0; i--)
        {
            if (now - open[i].End <= window) continue;
            closed.Add(open[i]);
            open.RemoveAt(i);
        }
    }

    /// <summary>Closes what is open and returns the fights in time order, the largest <c>MaxFights</c>.</summary>
    public SummaryFight[] Finish()
    {
        closed.AddRange(open);
        open.Clear();
        closed.Sort((a, b) => b.Total.CompareTo(a.Total));
        if (closed.Count > maxFights) closed.RemoveRange(maxFights, closed.Count - maxFights);
        closed.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new SummaryFight[closed.Count];
        for (int i = 0; i < result.Length; i++)
        {
            Fight f = closed[i];
            int n = f.ByOwner.Count, k = 0;
            var fight = new SummaryFight { Start = f.Start, End = f.End, CentreX = f.SumX / Math.Max(1e-3f, f.Total), CentreY = f.SumY / Math.Max(1e-3f, f.Total), Owners = new int[n], UnitsLost = new int[n], WorthLost = new float[n], WorthKilled = new float[n] };
            foreach (KeyValuePair<int, (int units, float lost, float killed)> o in f.ByOwner)
            {
                fight.Owners[k] = o.Key;
                fight.UnitsLost[k] = o.Value.units;
                fight.WorthLost[k] = o.Value.lost;
                fight.WorthKilled[k] = o.Value.killed;
                k++;
            }
            result[i] = fight;
        }
        return result;
    }
}
