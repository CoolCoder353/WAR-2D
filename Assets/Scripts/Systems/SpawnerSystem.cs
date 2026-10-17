using Config;
using Mirror;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>
/// Spawner buildings: produce their selected type, one unit every <c>BuildSeconds</c>, while the owner can pay
/// <c>UpfrontCost</c>, on the nearest free walkable tile outside the footprint. The unit itself is
/// created by the simulation at its next boundary (<see cref="SimCommandKind.SpawnUnit"/>); if the
/// simulation refuses it (the owner is at the unit cap), the cost is refunded and the queue kept.
/// Each count change is sent to the owner only (<see cref="WorldStateManager.SendSpawnerQueue"/>). A spawner
/// with a rally point gives each new unit a move order to it.
/// Server only, Playing state only.
/// </summary>
public partial struct SpawnerSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || WorldStateManager.Instance == null || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return;
        SimCommandQueue queue = SimCommandQueue.Instance;
        if (queue == null || WorldStateManager.Instance.Map == null) return;
        if (WAR2D.Sim.SimContext.Current != null && WAR2D.Sim.SimContext.Current.Pause.Frozen) return;

        GameConfigData config = ConfigLoader.LoadConfig();
        float dt = SystemAPI.Time.DeltaTime;
        EntityManager entityManager = state.EntityManager;

        foreach (var (spawner, building, entity) in SystemAPI.Query<RefRW<SpawnerData>, RefRO<BuildingData>>().WithNone<Construction>().WithEntityAccess())
        {
            UnitType type = (UnitType)spawner.ValueRO.unitType;
            if (spawner.ValueRO.count <= 0 || !config.Units.TryGetValue(type, out UnitConfig unitConfig))
            {
                spawner.ValueRW.progress = 0f;
                continue;
            }
            // Progress stops at one unit's build time, so a producer that can't place its unit does not store up a burst.
            float buildSeconds = unitConfig.BuildSeconds;
            spawner.ValueRW.progress = math.min(spawner.ValueRO.progress + dt, buildSeconds);
            if (!SpawnerRules.CanSpawn(spawner.ValueRO.progress, buildSeconds)) continue;

            int2 anchor = (int2)math.round(spawner.ValueRO.position);
            if (!WorldStateManager.Instance.TryFindSpawnTile(anchor, out int2 tile, unitConfig.SizeClass > 0)) continue; // no room; keep the queue

            int ownerId = spawner.ValueRO.ownerId;
            float cost = unitConfig.UpfrontCost;
            ServerPlayer owner = GameCore.Instance.GetServerPlayerById(ownerId);
            if (owner == null || !owner.TrySpend(cost)) continue;

            spawner.ValueRW.count--;
            spawner.ValueRW.progress = 0f;
            int buildingId = building.ValueRO.id;
            WorldStateManager.Instance.SendSpawnerQueue(ownerId, buildingId, spawner.ValueRO.unitType, spawner.ValueRO.count);
            Entity source = entity;
            bool rally = spawner.ValueRO.hasRally;
            int2 rallyTile = spawner.ValueRO.rally;
            queue.Enqueue(new SimCommand
            {
                Kind = SimCommandKind.SpawnUnit,
                OwnerId = ownerId,
                UnitType = type,
                Position = (float2)tile + 0.5f,
                OnSpawned = id =>
                {
                    if (id >= 0)
                    {
                        // A rally point sends the new unit on (applied at the next boundary, once it is settled).
                        if (rally) SimCommandQueue.Instance?.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.Move, OwnerId = ownerId, Tile = rallyTile, Ids = new[] { id } });
                        return;
                    }
                    owner.Refund(cost);
                    if (!entityManager.Exists(source) || !entityManager.HasComponent<SpawnerData>(source)) return;
                    SpawnerData data = entityManager.GetComponentData<SpawnerData>(source);
                    // Put the unit back on the queue, unless the owner switched type meanwhile.
                    if (data.unitType == (byte)type && data.count < SpawnerRules.MaxQueue) data.count++;
                    entityManager.SetComponentData(source, data);
                    WorldStateManager.Instance?.SendSpawnerQueue(ownerId, buildingId, data.unitType, data.count);
                },
            });
        }
    }
}
