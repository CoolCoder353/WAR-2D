using Config;
using Mirror;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>
/// Spawner buildings: produce from their queue at <c>SpawnRate</c> while the owner can pay
/// <c>UpfrontCost</c>, on the nearest free walkable tile outside the footprint. The unit itself is
/// created by the simulation at its next boundary (<see cref="SimCommandKind.SpawnUnit"/>); if the
/// simulation refuses it (the owner is at the unit cap), the cost is refunded and the queue kept.
/// Each count change is sent to the owner only (<see cref="WorldStateManager.SendSpawnerQueue"/>).
/// Server only, Playing state only.
/// </summary>
public partial struct SpawnerSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || WorldStateManager.Instance == null || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return;
        SimCommandQueue queue = SimCommandQueue.Instance;
        if (queue == null || WorldStateManager.Instance.Map == null) return;

        GameConfigData config = ConfigLoader.LoadConfig();
        float dt = SystemAPI.Time.DeltaTime;
        EntityManager entityManager = state.EntityManager;

        foreach (var (spawner, building, entity) in SystemAPI.Query<RefRW<SpawnerData>, RefRO<BuildingData>>().WithEntityAccess())
        {
            float spawnRate = spawner.ValueRO.spawnRate;
            // Clamp the timer to one unit's worth so a spawner that sat idle does not burst out its whole queue.
            float elapsed = spawner.ValueRO.timeSinceLastSpawn + dt;
            spawner.ValueRW.timeSinceLastSpawn = math.min(elapsed, 1f / spawnRate);
            if (spawner.ValueRO.count <= 0 || !SpawnerRules.CanSpawn(spawner.ValueRO.timeSinceLastSpawn, spawnRate)) continue;

            int2 anchor = (int2)math.round(spawner.ValueRO.position);
            if (!WorldStateManager.Instance.TryFindSpawnTile(anchor, out int2 tile)) continue; // no room; keep the queue

            int ownerId = spawner.ValueRO.ownerId;
            UnitType type = spawner.ValueRO.unitType;
            float cost = config.GetUnit(type).UpfrontCost;
            ServerPlayer owner = GameCore.Instance.GetServerPlayerById(ownerId);
            if (owner == null || !owner.TrySpend(cost)) continue;

            spawner.ValueRW.count--;
            spawner.ValueRW.timeSinceLastSpawn = 0f;
            int buildingId = building.ValueRO.id;
            WorldStateManager.Instance.SendSpawnerQueue(ownerId, buildingId, spawner.ValueRO.count);
            Entity source = entity;
            queue.Enqueue(new SimCommand
            {
                Kind = SimCommandKind.SpawnUnit,
                OwnerId = ownerId,
                UnitType = type,
                Position = (float2)tile + 0.5f,
                OnSpawned = id =>
                {
                    if (id >= 0) return;
                    owner.Add(cost);
                    if (!entityManager.Exists(source) || !entityManager.HasComponent<SpawnerData>(source)) return;
                    SpawnerData data = entityManager.GetComponentData<SpawnerData>(source);
                    data.count++;
                    entityManager.SetComponentData(source, data);
                    WorldStateManager.Instance?.SendSpawnerQueue(ownerId, buildingId, data.count);
                },
            });
        }
    }
}
