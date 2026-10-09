using System;
using System.Collections.Generic;

public enum OutcomeKind { Continue, Winner, Draw }

public readonly struct PlayerStatus
{
    public readonly int PlayerId;
    public readonly bool HasHQ;
    public readonly bool Eliminated;
    /// <summary>The player's starting team; eliminated players share their team's win.</summary>
    public readonly int Team;

    /// <summary>A player on a team of its own (free-for-all).</summary>
    public PlayerStatus(int playerId, bool hasHQ, bool eliminated) : this(playerId, hasHQ, eliminated, -playerId - 1) { }

    public PlayerStatus(int playerId, bool hasHQ, bool eliminated, int team)
    {
        PlayerId = playerId;
        HasHQ = hasHQ;
        Eliminated = eliminated;
        Team = team;
    }
}

public readonly struct MatchOutcome
{
    public readonly OutcomeKind Kind;
    /// <summary>
    /// Every player who sees the Win screen (valid when <see cref="Kind"/> is Winner): each HQ holder, plus
    /// each player without an HQ whose starting team includes one.
    /// </summary>
    public readonly List<int> Winners;
    public readonly List<int> NewlyEliminated;

    public MatchOutcome(OutcomeKind kind, List<int> winners, List<int> newlyEliminated)
    {
        Kind = kind;
        Winners = winners ?? new List<int>();
        NewlyEliminated = newlyEliminated;
    }
}

/// <summary>Who is eliminated, who won, or whether it is a draw. Pure.</summary>
public static class WinLossRules
{
    /// <summary>
    /// A draw when nobody holds an HQ. A win when the match started with at least one hostile pair and no
    /// remaining HQ holder attacks another (the survivors are at peace, mutually or alone). Otherwise the
    /// match continues.
    /// </summary>
    /// <param name="attacks">Whether player a attacks player b now (diplomacy).</param>
    /// <param name="startedHostile">True when the starting diplomacy had an attacking pair (≥ 2 teams); otherwise nobody ever wins.</param>
    public static MatchOutcome Evaluate(IReadOnlyList<PlayerStatus> players, Func<int, int, bool> attacks, bool startedHostile)
    {
        var newlyEliminated = new List<int>();
        if (players.Count == 0) return new MatchOutcome(OutcomeKind.Continue, null, newlyEliminated);

        var holders = new List<PlayerStatus>();
        foreach (PlayerStatus p in players)
        {
            if (p.HasHQ) holders.Add(p);
            else if (!p.Eliminated) newlyEliminated.Add(p.PlayerId);
        }

        if (holders.Count == 0) return new MatchOutcome(OutcomeKind.Draw, null, newlyEliminated);
        if (!startedHostile) return new MatchOutcome(OutcomeKind.Continue, null, newlyEliminated);
        foreach (PlayerStatus a in holders)
            foreach (PlayerStatus b in holders)
                if (a.PlayerId != b.PlayerId && attacks(a.PlayerId, b.PlayerId))
                    return new MatchOutcome(OutcomeKind.Continue, null, newlyEliminated);

        var winners = new List<int>();
        var winningTeams = new HashSet<int>();
        foreach (PlayerStatus h in holders)
        {
            winners.Add(h.PlayerId);
            winningTeams.Add(h.Team);
        }
        foreach (PlayerStatus p in players)
            if (!p.HasHQ && winningTeams.Contains(p.Team)) winners.Add(p.PlayerId);
        return new MatchOutcome(OutcomeKind.Winner, winners, newlyEliminated);
    }
}
