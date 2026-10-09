using System;
using System.Collections.Generic;
using Mirror;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>
/// Server side of the alert feed: collects each tick's damage to owned entities (from the settled
/// attack events), upkeep and vision-sharing changes, and sends every player only their own alerts
/// (<see cref="ClientPlayer.TargetAlerts"/>), once per tick.
/// </summary>
public sealed class AlertService
{
    private readonly AlertThrottle throttle;
    private readonly Dictionary<int, List<Alert>> pending = new Dictionary<int, List<Alert>>();
    private readonly List<(int owner, int2 tile)> damaged = new List<(int, int2)>();
    private readonly HashSet<int> seenTargets = new HashSet<int>();
    private readonly Action<int, Alert> add;
    private readonly Action<int, Alert[]> send;

    /// <param name="send">Delivers one player's alerts; by default <see cref="ClientPlayer.TargetAlerts"/> to that player only (tests pass a spy).</param>
    public AlertService(float throttleSeconds, int areaTiles, Action<int, Alert[]> send = null)
    {
        throttle = new AlertThrottle(throttleSeconds, areaTiles);
        add = Add;
        this.send = send ?? SendToPlayer;
    }

    /// <summary>Queues an alert for one player.</summary>
    public void Add(int ownerId, Alert alert)
    {
        if (!pending.TryGetValue(ownerId, out List<Alert> list)) pending[ownerId] = list = new List<Alert>();
        list.Add(alert);
    }

    /// <summary>
    /// Reads the settled tick's attack events: every damaged target's own owner and tile (units from the
    /// SoA, buildings through <paramref name="building"/>), then the throttled UnderAttack alerts.
    /// </summary>
    public void CollectDamage(SimData data, int unitCount, Func<int, (bool found, int owner, int2 tile)> building, double now)
    {
        if (data.AttackEvents.Length == 0) return;
        damaged.Clear();
        seenTargets.Clear();
        foreach (int2 attack in data.AttackEvents)
        {
            int target = attack.y;
            if (!seenTargets.Add(target)) continue;
            int slot = data.SlotOf(target, unitCount);
            if (slot >= 0) damaged.Add((data.OwnerId[slot], (int2)math.floor(data.Positions[slot])));
            else
            {
                (bool found, int owner, int2 tile) = building(target);
                if (found) damaged.Add((owner, tile));
            }
        }
        AlertRules.UnderAttack(damaged, throttle, now, add);
    }

    /// <summary>Sends each player their queued alerts.</summary>
    public void Flush()
    {
        if (pending.Count == 0) return;
        foreach (KeyValuePair<int, List<Alert>> pair in pending)
        {
            if (pair.Value.Count == 0) continue;
            send(pair.Key, pair.Value.ToArray());
            pair.Value.Clear();
        }
    }

    private static void SendToPlayer(int ownerId, Alert[] alerts)
    {
        NetworkConnectionToClient connection = GameCore.Instance?.GetServerPlayerById(ownerId)?.connection;
        if (connection?.identity != null && connection.identity.TryGetComponent(out ClientPlayer player))
            player.TargetAlerts(connection, alerts);
    }

    /// <summary>Forgets a player (they left).</summary>
    public void Forget(int ownerId)
    {
        pending.Remove(ownerId);
        throttle.Forget(ownerId);
    }
}
