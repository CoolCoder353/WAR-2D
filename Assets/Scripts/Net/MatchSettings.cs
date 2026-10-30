using System;
using System.Collections.Generic;
using Config;
using WAR2D.World;

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
    /// <summary>Start points dealt to each player (the config's range).</summary>
    public int StartOptions;
    /// <summary>HQ pick time in seconds: 0 = random, &gt; 0 = a timed pick, <see cref="StartPickRules.Unlimited"/> = wait for everyone.</summary>
    public int StartPickSeconds;
    /// <summary>
    /// Who may join (v0.14): a Public or Friends Steam lobby, or Private (no Steam lobby; the server refuses every
    /// connection but the host's own). Default from <c>&lt;Lobby&gt;&lt;Visibility&gt;</c>.
    /// </summary>
    public LobbyVisibility Visibility;

    public bool Equals(MatchSettings other) =>
        Mode == other.Mode && Diplomacy == other.Diplomacy && MapSize == other.MapSize && Seed == other.Seed && StartingResources.Equals(other.StartingResources)
        && StartOptions == other.StartOptions && StartPickSeconds == other.StartPickSeconds && Visibility == other.Visibility;
}

/// <summary>Validates settings a client sends. Pure.</summary>
public static class MatchSettingsRules
{
    /// <summary>A known mode, a non-zero seed, and a map size and starting resources from the lobby's lists.</summary>
    public static bool IsValid(in MatchSettings s, LobbyConfig lobby)
    {
        if (lobby == null || !Enum.IsDefined(typeof(MatchMode), s.Mode) || !Enum.IsDefined(typeof(LobbyVisibility), s.Visibility) || s.Seed == 0) return false;
        if (Array.IndexOf(lobby.MapSizes, s.MapSize) < 0) return false;
        if (!StartPickRules.IsValidOptions(s.StartOptions, lobby) || !StartPickRules.IsValidPickSeconds(s.StartPickSeconds, lobby)) return false;
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

    /// <summary>
    /// True when a connection may stay: a Private lobby (v0.14) takes only the host's own connection; otherwise
    /// <see cref="MayJoin"/> decides with the seats (humans and AIs).
    /// </summary>
    public static bool MayConnect(LobbyVisibility visibility, bool hostsOwn, GameState state, int seatsIncludingNew, out string refusal)
    {
        refusal = null;
        if (hostsOwn) return true;
        if (visibility == LobbyVisibility.Private) { refusal = PrivateRefusal; return false; }
        return MayJoin(state, seatsIncludingNew);
    }

    /// <summary>What a player refused by a Private lobby is told.</summary>
    public const string PrivateRefusal = "Private match: the host is playing single-player and takes no players.";

    /// <summary>
    /// True when the host may switch <paramref name="from"/> → <paramref name="to"/>: going Private is refused while
    /// another human is connected (AIs never block it). Every other change is allowed.
    /// </summary>
    public static bool MayChangeVisibility(LobbyVisibility from, LobbyVisibility to, int humans) =>
        to != LobbyVisibility.Private || from == LobbyVisibility.Private || humans <= 1;

    /// <summary>
    /// The team an added AI joins (v0.14): <see cref="TeamRules.NoTeam"/> in Free-for-all; in Teams mode the pickable
    /// team with the fewest players (the lowest on a tie), counting each player's lobby team.
    /// </summary>
    public static int FewestPlayersTeam(MatchMode mode, IEnumerable<int> lobbyTeams)
    {
        if (mode != MatchMode.Teams) return TeamRules.NoTeam;
        var counts = new int[PickableTeams];
        if (lobbyTeams != null)
            foreach (int t in lobbyTeams) if (t >= 0 && t < PickableTeams) counts[t]++;
        int best = 0;
        for (int t = 1; t < PickableTeams; t++) if (counts[t] < counts[best]) best = t;
        return best;
    }

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

    /// <summary>True when the map has the start sites for every player's options plus the spare ones.</summary>
    public static bool FitsMap(int players, in MatchSettings settings, MapConfig map, int spare)
    {
        if (settings.MapSize <= 0 || map == null) return true; // the scene's own map has no sites to deal
        return StartPickRules.Fits(players, settings.StartOptions, RegionMapGenerator.Counts(map, settings.MapSize).starts, spare);
    }

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

/// <summary>What the host may pick for a lobby AI (v0.14). Pure.</summary>
public static class AiLobbyRules
{
    /// <summary>Easy, Normal or Hard (never Scripted, which is dev only) and a known style (Random allowed).</summary>
    public static bool IsValidChoice(AiDifficulty difficulty, AiStyle style) =>
        difficulty != AiDifficulty.Scripted && Enum.IsDefined(typeof(AiDifficulty), difficulty) && Enum.IsDefined(typeof(AiStyle), style);

    /// <summary>True while another seat fits: humans and AIs together are at most <see cref="LobbyRules.MaxPlayers"/>.</summary>
    public static bool HasFreeSeat(int seats) => seats >= 0 && seats < LobbyRules.MaxPlayers;

    /// <summary>The lobby's text for a difficulty ("Hard").</summary>
    public static string DifficultyName(AiDifficulty difficulty) => difficulty.ToString();

    /// <summary>The lobby's text for a style ("Random", "Turtle").</summary>
    public static string StyleName(AiStyle style) => style.ToString();
}

/// <summary>Private start points: how many each player is dealt, the pick time and the deal itself. Pure.</summary>
public static class StartPickRules
{
    /// <summary><see cref="MatchSettings.StartPickSeconds"/> value for "the match waits until everyone has placed".</summary>
    public const int Unlimited = -1;

    /// <summary>True for a number of options in the lobby's range.</summary>
    public static bool IsValidOptions(int options, LobbyConfig lobby) =>
        lobby != null && options >= lobby.StartOptionsMin && options <= lobby.StartOptionsMax;

    /// <summary>True for 0, a timed value from the lobby's list, or <see cref="Unlimited"/>; nothing else.</summary>
    public static bool IsValidPickSeconds(int seconds, LobbyConfig lobby) =>
        lobby != null && (seconds == Unlimited || Array.IndexOf(lobby.StartPickSeconds, seconds) >= 0);

    /// <summary>True when every player can be dealt their options with the spare sites still left over.</summary>
    public static bool Fits(int players, int options, int sites, int spare) => players >= 0 && options >= 1 && players * options + spare <= sites;

    /// <summary>
    /// Deals each owner <paramref name="options"/> distinct sites, unique across owners, uniformly at random.
    /// <paramref name="nextInt"/> returns a value in [0, exclusiveMax) and must not be derived from the map seed.
    /// Returns an empty result when the sites don't suffice.
    /// </summary>
    public static Dictionary<int, int[]> Deal(int siteCount, IReadOnlyList<int> owners, int options, Func<int, int> nextInt)
    {
        var result = new Dictionary<int, int[]>();
        if (owners.Count * options > siteCount || options < 1) return result;
        var sites = new int[siteCount];
        for (int i = 0; i < siteCount; i++) sites[i] = i;
        for (int i = siteCount - 1; i > 0; i--)
        {
            int j = nextInt(i + 1);
            (sites[i], sites[j]) = (sites[j], sites[i]);
        }
        for (int o = 0; o < owners.Count; o++)
        {
            var mine = new int[options];
            Array.Copy(sites, o * options, mine, 0, options);
            result[owners[o]] = mine;
        }
        return result;
    }

    /// <summary>
    /// What <paramref name="owner"/> may learn of the other players' starts: each teammate's options and pick (in
    /// <paramref name="starts"/> order), and nobody else's. <paramref name="teammates"/> is true only for two different
    /// owners on the same team. The server sends exactly this (<c>GameCore.SendStartOptions</c>).
    /// </summary>
    public static List<(int owner, int[] options, int pick)> TeamView(int owner, IEnumerable<(int owner, int[] options, int pick)> starts, Func<int, int, bool> teammates)
    {
        var view = new List<(int owner, int[] options, int pick)>();
        foreach ((int other, int[] options, int pick) in starts)
            if (other != owner && options != null && teammates(owner, other)) view.Add((other, options, pick));
        return view;
    }
}
