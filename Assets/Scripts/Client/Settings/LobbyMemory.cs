using System;
using System.Collections.Generic;
using System.IO;
using Config;
using Newtonsoft.Json;
using UnityEngine;
using WAR2D.Dev.Logging;

/// <summary>An AI seat as the host left it: never its callsign, which is drawn from the seed.</summary>
[Serializable]
public struct RememberedAi
{
    public AiDifficulty Difficulty;
    public AiStyle Style;
    public int Team;
}

/// <summary>The match settings and AI seats the host last started with (<c>lobby.json</c> beside <c>settings.json</c>, v0.18).</summary>
[Serializable]
public sealed class RememberedLobby
{
    public MatchMode Mode;
    public bool Diplomacy;
    public int MapSize;
    public float StartingResources;
    public bool InfiniteResources;
    public int StartOptions;
    public int StartPickSeconds;
    public LobbyVisibility Visibility;
    public string ServerName;
    public List<RememberedAi> Ais = new List<RememberedAi>();
}

/// <summary>Remembers the host's lobby between sessions. Joined players' machines save nothing. Every value is checked when read back.</summary>
public static class LobbyMemory
{
    private const string FileName = "lobby.json";

    /// <summary>Test seam: set by the test harness so tests never touch the player's real file.</summary>
    internal static string PathForTests;

    /// <summary>Off for the perf harness and hosted tests, which need the config's defaults.</summary>
    public static bool Disabled;

    private static string PathOf() => PathForTests ?? Path.Combine(Application.persistentDataPath, FileName);

    /// <summary>Saves what the host started with.</summary>
    public static void Save(RememberedLobby lobby)
    {
        if (Disabled) return;
        try { File.WriteAllText(PathOf(), JsonConvert.SerializeObject(lobby, Formatting.Indented)); }
        catch (IOException e) { WarLog.Warn(LogCategory.UI, "could not save the lobby: " + e.Message); }
    }

    /// <summary>What the host last started with, or null (none yet, unreadable, or off).</summary>
    public static RememberedLobby Load()
    {
        if (Disabled) return null;
        string path = PathOf();
        if (!File.Exists(path)) return null;
        try { return JsonConvert.DeserializeObject<RememberedLobby>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException || e is JsonException)
        {
            WarLog.Warn(LogCategory.UI, "could not read the saved lobby: " + e.Message);
            return null;
        }
    }

    /// <summary>Forgets it (the lobby's Reset).</summary>
    public static void Forget()
    {
        if (Disabled) return;
        try { if (File.Exists(PathOf())) File.Delete(PathOf()); }
        catch (IOException e) { WarLog.Warn(LogCategory.UI, "could not remove the saved lobby: " + e.Message); }
    }

    /// <summary>
    /// The defaults with each remembered value that is still allowed by the config's lists; anything else keeps its default.
    /// The seed is always the default's new one. Pure.
    /// </summary>
    public static MatchSettings Apply(MatchSettings defaults, RememberedLobby lobby, LobbyConfig config)
    {
        if (lobby == null) return defaults;
        MatchSettings s = defaults;
        if (Enum.IsDefined(typeof(MatchMode), lobby.Mode)) s.Mode = lobby.Mode;
        s.Diplomacy = lobby.Diplomacy;
        if (Array.IndexOf(config.MapSizes, lobby.MapSize) >= 0) s.MapSize = lobby.MapSize;
        if (Array.IndexOf(config.StartingResources, lobby.StartingResources) >= 0) s.StartingResources = lobby.StartingResources;
        s.InfiniteResources = lobby.InfiniteResources;
        if (StartPickRules.IsValidOptions(lobby.StartOptions, config)) s.StartOptions = lobby.StartOptions;
        if (StartPickRules.IsValidPickSeconds(lobby.StartPickSeconds, config)) s.StartPickSeconds = lobby.StartPickSeconds;
        if (Enum.IsDefined(typeof(LobbyVisibility), lobby.Visibility)) s.Visibility = lobby.Visibility;
        return s;
    }
}

/// <summary>Who may ask for a rematch, and when (v0.18). Pure.</summary>
public static class RematchRules
{
    /// <summary>The server owner, once the match is over and any replay has been saved.</summary>
    public static bool May(GameState state, bool serverOwner, bool replayPending) => state == GameState.GameOver && serverOwner && !replayPending;
}
