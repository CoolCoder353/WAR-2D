using System.Collections.Generic;

public enum OutcomeKind { Continue, Winner, Draw }

public readonly struct PlayerStatus
{
    public readonly int PlayerId;
    public readonly bool HasHQ;
    public readonly bool Eliminated;
    /// <summary>The player's team; a team wins when it is the only one with an HQ left.</summary>
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
    /// <summary>A surviving player of the winning team, or -1.</summary>
    public readonly int WinnerId;
    /// <summary>The winning team (valid when <see cref="Kind"/> is Winner).</summary>
    public readonly int WinnerTeam;
    public readonly List<int> NewlyEliminated;

    public MatchOutcome(OutcomeKind kind, int winnerId, List<int> newlyEliminated, int winnerTeam = 0)
    {
        Kind = kind;
        WinnerId = winnerId;
        WinnerTeam = winnerTeam;
        NewlyEliminated = newlyEliminated;
    }
}

/// <summary>Who is eliminated, which team won, or whether it is a draw. Pure.</summary>
public static class WinLossRules
{
    /// <param name="matchStartTeamCount">Teams present when the match began; a lone team never wins.</param>
    public static MatchOutcome Evaluate(IReadOnlyList<PlayerStatus> players, int matchStartTeamCount)
    {
        var newlyEliminated = new List<int>();
        if (players.Count == 0) return new MatchOutcome(OutcomeKind.Continue, -1, newlyEliminated);

        var aliveTeams = new HashSet<int>();
        int lastAlive = -1, lastTeam = 0;
        foreach (PlayerStatus p in players)
        {
            if (p.HasHQ)
            {
                aliveTeams.Add(p.Team);
                lastAlive = p.PlayerId;
                lastTeam = p.Team;
            }
            else if (!p.Eliminated)
            {
                newlyEliminated.Add(p.PlayerId);
            }
        }

        if (aliveTeams.Count == 0) return new MatchOutcome(OutcomeKind.Draw, -1, newlyEliminated);
        if (aliveTeams.Count == 1 && matchStartTeamCount > 1) return new MatchOutcome(OutcomeKind.Winner, lastAlive, newlyEliminated, lastTeam);
        return new MatchOutcome(OutcomeKind.Continue, -1, newlyEliminated);
    }
}
