using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Entities;

/// <summary>Once per second during Playing, applies WinLossRules to the current HQ owners. Server only.</summary>
public partial struct WinLossSystem : ISystem
{
    private float checkTimer;

    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return;

        checkTimer += SystemAPI.Time.DeltaTime;
        if (checkTimer < 1f) return;
        checkTimer = 0f;

        var hqOwners = new NativeHashSet<int>(16, Allocator.Temp);
        foreach (var (hq, health) in SystemAPI.Query<RefRO<HQComponent>, RefRO<HealthComponent>>())
        {
            if (health.ValueRO.currentHealth > 0f) hqOwners.Add(hq.ValueRO.ownerId);
        }

        var statuses = new List<PlayerStatus>();
        foreach (KeyValuePair<NetworkIdentity, ServerPlayer> entry in GameCore.Instance.ServerPlayers)
        {
            int id = (int)entry.Key.netId;
            statuses.Add(new PlayerStatus(id, hqOwners.Contains(id), entry.Value.state == PlayerState.Eliminated, GameCore.Instance.TeamOf(id)));
        }
        foreach (KeyValuePair<int, ServerPlayer> bot in GameCore.Instance.Bots)
            statuses.Add(new PlayerStatus(bot.Key, hqOwners.Contains(bot.Key), bot.Value.state == PlayerState.Eliminated, GameCore.Instance.TeamOf(bot.Key)));
        hqOwners.Dispose();

        GameCore core = GameCore.Instance;
        WAR2D.Sim.SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        bool Attacks(int a, int b)
        {
            if (sim != null && sim.TrySlotOf(a, out int from) && sim.TrySlotOf(b, out int to)) return sim.Diplomacy.Attacks(from, to);
            return core.TeamOf(a) != core.TeamOf(b); // no slot yet: the starting state
        }
        core.ApplyOutcome(WinLossRules.Evaluate(statuses, Attacks, core.MatchStartTeamCount > 1));
    }
}
