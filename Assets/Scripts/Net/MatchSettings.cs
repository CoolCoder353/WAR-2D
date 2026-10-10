using System;
using System.Collections.Generic;
using Config;

/// <summary>How the lobby forms teams.</summary>
public enum MatchMode : byte
{
    /// <summary>Everyone for themselves: every lobby team is forced to <see cref="TeamRules.NoTeam"/>.</summary>
    FreeForAll,
    /// <summary>Players pick a team (or Solo).</summary>
    Teams,
}

/// <summary>The host's match settings: public lobby information, the only source of the map and starting resources at start.</summary>
[Serializable]
public struct MatchSettings
{
    public MatchMode Mode;
    /// <summary>Players may change whom they attack and share vision with during the match.</summary>
    public bool Diplomacy;
    /// <summary>Tiles per side (0 = the scene's own tilemaps; tests only).</summary>
    public int MapSize;
    /// <summary>The map generator's seed (never 0 once the lobby has one).</summary>
    public uint Seed;
    public float StartingResources;

    public bool Equals(MatchSettings other) =>
        Mode == other.Mode && Diplomacy == other.Diplomacy && MapSize == other.MapSize && Seed == other.Seed && StartingResources.Equals(other.StartingResources);
}

/// <summary>Validates settings a client sends. Pure.</summary>
public static class MatchSettingsRules
{
    /// <summary>A known mode, a non-zero seed, and a map size and starting resources from the lobby's lists.</summary>
    public static bool IsValid(in MatchSettings s, LobbyConfig lobby)
    {
        if (lobby == null || !Enum.IsDefined(typeof(MatchMode), s.Mode) || s.Seed == 0) return false;
        if (Array.IndexOf(lobby.MapSizes, s.MapSize) < 0) return false;
        foreach (float r in lobby.StartingResources) if (r.Equals(s.StartingResources)) return true;
        return false;
    }
}

/// <summary>Lobby rules: colours, readiness and when a match may start. Pure.</summary>
public static class LobbyRules
{
    /// <summary>Player colours to pick from (the palette's size).</summary>
    public const int Colours = 8;

    /// <summary>Most players in a match: one per colour and HQ clearing.</summary>
    public const int MaxPlayers = Colours;

    /// <summary>Teams a player may pick in Teams mode (shown as Team 1–4, plus Solo).</summary>
    public const int PickableTeams = 4;

    /// <summary>
    /// True when a new connection may join: only in the lobby, and only while the connections (the new one
    /// included) number at most <see cref="MaxPlayers"/>.
    /// </summary>
    public static bool MayJoin(GameState state, int connectionsIncludingNew) =>
        state == GameState.Lobby && connectionsIncludingNew <= MaxPlayers;

    /// <summary>The lowest colour nobody has taken, or -1 when all are taken.</summary>
    public static int FirstFreeColour(IEnumerable<int> taken)
    {
        var used = new HashSet<int>(taken);
        for (int c = 0; c < Colours; c++) if (!used.Contains(c)) return c;
        return -1;
    }

    /// <summary>True for a palette colour nobody else has taken.</summary>
    public static bool IsColourFree(int colour, IEnumerable<int> takenByOthers)
    {
        if (colour < 0 || colour >= Colours) return false;
        foreach (int c in takenByOthers) if (c == colour) return false;
        return true;
    }

    /// <summary>True for a team a player may pick for themselves in this mode.</summary>
    public static bool IsTeamChoiceValid(MatchMode mode, int team) =>
        team == TeamRules.NoTeam || (mode == MatchMode.Teams && team >= 0 && team < PickableTeams);

    /// <summary>The match can start: there are players, every one is ready, and they form at least two teams (so someone attacks someone).</summary>
    public static bool CanStart(IReadOnlyList<(int owner, bool ready, int team)> players) =>
        AllReady(players) && Teams(players) >= 2;

    /// <summary>True when every player (and at least one) is ready.</summary>
    public static bool AllReady(IReadOnlyList<(int owner, bool ready, int team)> players)
    {
        if (players.Count == 0) return false;
        foreach (var p in players) if (!p.ready) return false;
        return true;
    }

    /// <summary>The number of teams the players would start in.</summary>
    public static int Teams(IReadOnlyList<(int owner, bool ready, int team)> players)
    {
        var choices = new List<(int Owner, int Choice)>(players.Count);
        foreach (var p in players) choices.Add((p.owner, p.team));
        return TeamRules.CountTeams(TeamRules.Assign(choices).Values);
    }
}
