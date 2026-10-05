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
            statuses.Add(new PlayerStatus(id, hqOwners.Contains(id), entry.Value.state == PlayerState.Eliminated));
        }
        hqOwners.Dispose();

        GameCore.Instance.ApplyOutcome(WinLossRules.Evaluate(statuses, GameCore.Instance.MatchStartPlayerCount));
    }
}
