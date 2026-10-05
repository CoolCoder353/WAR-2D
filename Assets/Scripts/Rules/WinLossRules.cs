using System.Collections.Generic;

public enum OutcomeKind { Continue, Winner, Draw }

public readonly struct PlayerStatus
{
    public readonly int PlayerId;
    public readonly bool HasHQ;
    public readonly bool Eliminated;

    public PlayerStatus(int playerId, bool hasHQ, bool eliminated)
    {
        PlayerId = playerId;
        HasHQ = hasHQ;
        Eliminated = eliminated;
    }
}

public readonly struct MatchOutcome
{
    public readonly OutcomeKind Kind;
    public readonly int WinnerId;
    public readonly List<int> NewlyEliminated;

    public MatchOutcome(OutcomeKind kind, int winnerId, List<int> newlyEliminated)
    {
        Kind = kind;
        WinnerId = winnerId;
        NewlyEliminated = newlyEliminated;
    }
}

/// <summary>Who is eliminated, who won, or whether it is a draw. Pure.</summary>
public static class WinLossRules
{
    public static MatchOutcome Evaluate(IReadOnlyList<PlayerStatus> players, int matchStartPlayerCount)
    {
        var newlyEliminated = new List<int>();
        if (players.Count == 0) return new MatchOutcome(OutcomeKind.Continue, -1, newlyEliminated);

        int alive = 0;
        int lastAlive = -1;
        foreach (PlayerStatus p in players)
        {
            if (p.HasHQ)
            {
                alive++;
                lastAlive = p.PlayerId;
            }
            else if (!p.Eliminated)
            {
                newlyEliminated.Add(p.PlayerId);
            }
        }

        if (alive == 0) return new MatchOutcome(OutcomeKind.Draw, -1, newlyEliminated);
        if (alive == 1 && matchStartPlayerCount > 1) return new MatchOutcome(OutcomeKind.Winner, lastAlive, newlyEliminated);
        return new MatchOutcome(OutcomeKind.Continue, -1, newlyEliminated);
    }
}
