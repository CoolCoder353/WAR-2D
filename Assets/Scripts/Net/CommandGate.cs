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
        ["CmdAssignSquadChunk"] = (40f, 10f),
        ["CmdOrderSquad"] = (10f, 5f),
        ["TryAddBuilding"] = (10f, 5f),
        ["CanBuildBuildingCommand"] = (20f, 15f),
        ["BuildingClicked"] = (20f, 10f),
        ["CmdSetNickname"] = (5f, 1f),
        ["Cmd_StartGame"] = (3f, 0.5f),
        ["Cmd_SetTeam"] = (10f, 5f),
    }, (10f, 5f));

    private static readonly Dictionary<int, (int count, double windowStart)> Violations = new Dictionary<int, (int, double)>();

    public static bool Allow(NetworkConnectionToClient conn, string command)
    {
        if (conn == null || conn.identity == null) return false;
        double now = NetworkTime.localTime;
        if (Limiter.TryConsume(conn.connectionId, command, now)) return true;

        Violations.TryGetValue(conn.connectionId, out var v);
        if (now - v.windowStart > ViolationWindowSeconds) v = (0, now);
        v.count++;
        Violations[conn.connectionId] = v;
        if (v.count >= ViolationsBeforeKick)
        {
            Debug.LogWarning($"Disconnecting connection {conn.connectionId}: repeatedly exceeded command rate limits ({command}).");
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
