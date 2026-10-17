using WAR2D.Dev.Logging;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// Server-side gate every client Command passes through: per-command rate limits, plus disconnecting
/// connections that keep exceeding them.
/// </summary>
public static class CommandGate
{
    private const int ViolationsBeforeKick = 200;
    private const double ViolationWindowSeconds = 10.0;

    private static readonly RateLimiter Limiter = new RateLimiter(new Dictionary<string, (float, float)>
    {
        ["UpdateClientView"] = (20f, 15f),
        ["CmdOrderChunk"] = (40f, 20f),
        ["CmdReplayWanted"] = (3f, 1f),
        ["CmdAssignSquadChunk"] = (40f, 10f),
        ["CmdOrderSquad"] = (10f, 5f),
        ["TryAddBuilding"] = (10f, 5f),
        ["CanBuildBuildingCommand"] = (20f, 15f),
        ["CmdSetProduction"] = (20f, 10f),
        ["CmdChangeQueues"] = (20f, 10f),
        ["CmdSetRally"] = (10f, 5f),
        ["CmdCancelBuildings"] = (10f, 5f),
        ["CmdPlaceWall"] = (10f, 5f),
        ["CmdSetNickname"] = (5f, 1f),
        ["Cmd_StartGame"] = (3f, 0.5f),
        ["Cmd_SetTeam"] = (10f, 5f),
        ["Cmd_SetMatchSettings"] = (10f, 4f),
        ["Cmd_RerollMap"] = (5f, 2f),
        ["Cmd_SetColour"] = (10f, 4f),
        ["Cmd_SetReady"] = (10f, 4f),
        ["Cmd_SetOwnTeam"] = (10f, 4f),
        ["Cmd_Surrender"] = (3f, 0.5f),
        ["Cmd_GiftResources"] = (5f, 1f),
        ["Cmd_SetAttack"] = (10f, 2f),
        ["Cmd_SetShareVision"] = (10f, 2f),
    }, (10f, 5f));

    private static readonly Dictionary<int, (int count, double windowStart)> Violations = new Dictionary<int, (int, double)>();

    /// <summary>True when the connection has a player and is within the command's rate limit; repeated excess disconnects it.</summary>
    public static bool Allow(NetworkConnectionToClient conn, string command)
    {
        if (conn == null) return false;
        double now = NetworkTime.localTime;
        // A connection without a player is limited and kicked like any other, but never allowed.
        if (Limiter.TryConsume(conn.connectionId, command, now)) return conn.identity != null;

        Violations.TryGetValue(conn.connectionId, out var v);
        if (now - v.windowStart > ViolationWindowSeconds) v = (0, now);
        v.count++;
        Violations[conn.connectionId] = v;
        if (v.count >= ViolationsBeforeKick)
        {
            WarLog.Warn(LogCategory.Net, $"Disconnecting connection {conn.connectionId}: repeatedly exceeded command rate limits ({command}){(WAR2D.Net.Steam.SteamIdentity.TryGet(conn.connectionId, out ulong sid) ? $" (Steam {sid})" : "")}.");
            Forget(conn.connectionId);
            conn.Disconnect();
        }
        return false;
    }

    public static void Forget(int connectionId)
    {
        Limiter.Forget(connectionId);
        Violations.Remove(connectionId);
    }
}
