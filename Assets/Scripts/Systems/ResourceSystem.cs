using Config;
using Mirror;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// Passive income, Miner income, and upkeep for units and buildings. Unpaid entities lose
/// DecayPercentPerSecond of max health per second until paid. Server only, Playing state only.
/// </summary>
public partial struct ResourceSystem : ISystem
{
    private const float PassiveTickSeconds = 0.1f;
    private float passiveTimer;

    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || GameCore.Instance == null || WorldStateManager.Instance == null) return;
        if (GameCore.Instance.CurrentState != GameState.Playing) return;

        GameCore core = GameCore.Instance;
        GameConfigData config = ConfigLoader.LoadConfig();
        float dt = SystemAPI.Time.DeltaTime;

        passiveTimer += dt;
        if (passiveTimer >= PassiveTickSeconds)
        {
            float income = config.Resources.PassiveGenerationRate * passiveTimer;
            foreach (ServerPlayer player in core.serverPlayers)
            {
                if (player.state == PlayerState.Playing) player.AddIncome(income);
            }
            passiveTimer = 0f;
        }

        foreach (var (mining, building, transform) in SystemAPI.Query<RefRW<MiningComponent>, RefRO<BuildingData>, RefRO<LocalTransform>>())
        {
            mining.ValueRW.timeSinceLastMining += dt;
            if (mining.ValueRO.timeSinceLastMining < 1f) continue;

            int2 anchor = (int2)math.round(transform.ValueRO.Position.xy);
            int2 faced = anchor + MinerRules.FacingOffset(MinerRules.ZDegrees(transform.ValueRO.Rotation));
            bool active = WorldStateManager.Instance.Map.Grid.TileAt(faced) == TileType.Gem;
            mining.ValueRW.isActive = active;
            if (active)
            {
                ServerPlayer owner = core.GetServerPlayerById(building.ValueRO.ownerId);
                if (owner != null && owner.state == PlayerState.Playing)
                {
                    owner.AddIncome(config.Resources.MiningRate * mining.ValueRO.timeSinceLastMining);
                }
            }
            mining.ValueRW.timeSinceLastMining = 0f;
        }

        foreach (var (upkeep, health) in SystemAPI.Query<RefRW<UpkeepComponent>, RefRW<HealthComponent>>())
        {
            if (upkeep.ValueRO.unpaid)
            {
                health.ValueRW.currentHealth -= UpkeepRules.DecayDamage(health.ValueRO.maxHealth, config.Resources.DecayPercentPerSecond, dt);
            }

            upkeep.ValueRW.timeSinceLastCharge += dt;
            if (upkeep.ValueRO.timeSinceLastCharge < 1f) continue;

            float cost = upkeep.ValueRO.runningCostPerSecond * upkeep.ValueRO.timeSinceLastCharge;
            ServerPlayer owner = core.GetServerPlayerById(upkeep.ValueRO.ownerId);
            upkeep.ValueRW.unpaid = owner == null || !owner.TrySpend(cost);
            if (!upkeep.ValueRO.unpaid) owner.AddUpkeep(cost);
            upkeep.ValueRW.timeSinceLastCharge = 0f;
        }
    }
}
