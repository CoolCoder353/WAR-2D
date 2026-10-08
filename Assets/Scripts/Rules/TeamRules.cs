using System.Collections.Generic;

/// <summary>Turns lobby team choices into compact team numbers. Pure.</summary>
public static class TeamRules
{
    /// <summary>A lobby choice meaning "a team of my own".</summary>
    public const int NoTeam = -1;

    /// <summary>
    /// Assigns every owner a team in <c>[0, owners.Count)</c>. Owners who chose the same team share
    /// one (numbered in ascending choice order); owners with <see cref="NoTeam"/> each get their own.
    /// </summary>
    public static Dictionary<int, int> Assign(IReadOnlyList<(int Owner, int Choice)> owners)
    {
        var teams = new Dictionary<int, int>();
        var choices = new SortedSet<int>();
        foreach (var o in owners) if (o.Choice != NoTeam) choices.Add(o.Choice);
        var teamOfChoice = new Dictionary<int, int>();
        int next = 0;
        foreach (int choice in choices) teamOfChoice[choice] = next++;
        foreach (var o in owners)
            teams[o.Owner] = o.Choice != NoTeam ? teamOfChoice[o.Choice] : next++;
        return teams;
    }

    /// <summary>Distinct teams among <paramref name="teams"/>' values.</summary>
    public static int CountTeams(IEnumerable<int> teams) => new HashSet<int>(teams).Count;
}
