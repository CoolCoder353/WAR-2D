using Config;
using Mirror;
using Unity.Entities;

/// <summary>v0.7 commands: cancel/demolish, wall drag, production changes. Every one is gated and validated.</summary>
public partial class WorldStateManager
{
    /// <summary>
    /// Cancels (blueprint: full refund) or demolishes (finished: <c>Siege.DemolishRefund</c> of its cost back)
    /// each named building the sender owns, except the HQ (plan P1), with the normal explosion. Lists longer than
    /// <see cref="SpawnerRules.MaxSpawnersPerCommand"/> are refused.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdCancelBuildings(int[] buildingIds, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdCancelBuildings))) return;
        if (buildingIds == null || buildingIds.Length > SpawnerRules.MaxSpawnersPerCommand) return;
        if (!TryActingOwner(sender, out int owner)) return;
        ServerPlayer player = GameCore.Instance.GetServerPlayerById(owner);
        if (player == null) return;
        GameConfigData config = ConfigLoader.LoadConfig();
        foreach (int id in buildingIds)
        {
            if (!Buildings.TryGetValue(id, out Entity entity) || !EntityManager.Exists(entity)) continue;
            BuildingData building = EntityManager.GetComponentData<BuildingData>(entity);
            if (building.ownerId != owner || building.buildingType == BuildingType.Base) continue;
            bool blueprint = EntityManager.HasComponent<Construction>(entity);
            HealthComponent health = EntityManager.GetComponentData<HealthComponent>(entity);
            if (health.currentHealth <= 0f) continue; // already removed this frame (or dying): never refund twice
            float refund = ConstructionRules.Refund(blueprint, config.GetBuilding(building.buildingType).UpfrontCost, config.Siege.DemolishRefund);
            if (refund > 0f) player.Refund(refund);
            health.currentHealth = 0f; // DestructionSystem removes it, frees the footprint and records the explosion
            EntityManager.SetComponentData(entity, health);
        }
    }
}
