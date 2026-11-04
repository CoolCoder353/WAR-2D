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
        if (WAR2D.Sim.SimContext.Current != null && WAR2D.Sim.SimContext.Current.Pause.Frozen) return;

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
            foreach (int owner in core.PlayerOrder)
                if (core.GetServerPlayerById(owner)?.state == PlayerState.Playing) core.Summary.Income(owner, income);
            passiveTimer = 0f;
        }

        foreach (var (mining, building, transform) in SystemAPI.Query<RefRW<MiningComponent>, RefRO<BuildingData>, RefRO<LocalTransform>>().WithNone<Construction>())
        {
            mining.ValueRW.timeSinceLastMining += dt;
            if (mining.ValueRO.timeSinceLastMining < 1f) continue;

            int2 anchor = (int2)math.round(transform.ValueRO.Position.xy);
            int2 faced = anchor + MinerRules.FacingOffset(MinerRules.ZDegrees(transform.ValueRO.Rotation));
            TileType facedType = WorldStateManager.Instance.Map.Grid.TileAt(faced);
            bool active = MinerRules.Produces(facedType);
            mining.ValueRW.isActive = active;
            if (active)
            {
                ServerPlayer owner = core.GetServerPlayerById(building.ValueRO.ownerId);
                if (owner != null && owner.state == PlayerState.Playing && !owner.Away) // an away player earns nothing, so nothing is counted as mined
                {
                    // v0.17: a rich gem pays RichGemMultiplier times the rate.
                    float mined = config.Resources.MiningRate * MinerRules.RateOf(facedType, config.Resources.RichGemMultiplier) * mining.ValueRO.timeSinceLastMining;
                    owner.AddIncome(mined);
                    core.Stats.Mined(building.ValueRO.ownerId, mined);
                    core.Summary.Income(building.ValueRO.ownerId, mined);
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
            upkeep.ValueRW.unpaid = owner == null || !owner.TryPayUpkeep(cost);
            if (!upkeep.ValueRO.unpaid) owner.AddUpkeep(cost);
            else owner?.MarkUnpaid();
            upkeep.ValueRW.timeSinceLastCharge = 0f;
        }
    }
}
