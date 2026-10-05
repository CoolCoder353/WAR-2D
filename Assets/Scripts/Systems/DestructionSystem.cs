using Mirror;
using Unity.Collections;
using Unity.Entities;

/// <summary>
/// Destroys every entity at or below 0 health, after telling WorldStateManager so it can
/// unregister the entity, free its tiles and record a death explosion. Server only.
/// </summary>
public partial struct DestructionSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!NetworkServer.active || WorldStateManager.Instance == null) return;

        var dead = new NativeList<Entity>(Allocator.Temp);
        foreach (var (health, entity) in SystemAPI.Query<RefRO<HealthComponent>>().WithEntityAccess())
        {
            if (health.ValueRO.currentHealth <= 0f) dead.Add(entity);
        }

        foreach (Entity entity in dead)
        {
            WorldStateManager.Instance.OnEntityDestroyed(entity);
            state.EntityManager.DestroyEntity(entity);
        }
        dead.Dispose();
    }
}
