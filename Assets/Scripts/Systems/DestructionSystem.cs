using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using Mirror;

[BurstCompile]
public partial struct DestructionSystem : ISystem
{
    [ServerCallback]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (health, entity) in SystemAPI.Query<RefRO<HealthComponent>>().WithEntityAccess())
        {
            if (health.ValueRO.currentHealth <= 0)
            {
                // Debug.Log($"Entity {entity.Index} destroyed due to zero health.");
                ecb.DestroyEntity(entity);
            }
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}
