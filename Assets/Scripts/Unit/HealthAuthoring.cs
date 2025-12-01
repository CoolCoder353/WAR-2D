using Unity.Entities;
using UnityEngine;

public struct HealthComponent : IComponentData
{
    public float currentHealth;
    public float maxHealth;
}

public class HealthAuthoring : MonoBehaviour
{
    public float maxHealth = 100f;

    private class Baker : Baker<HealthAuthoring>
    {
        public override void Bake(HealthAuthoring authoring)
        {
            var entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
            AddComponent(entity, new HealthComponent
            {
                currentHealth = authoring.maxHealth,
                maxHealth = authoring.maxHealth
            });
        }
    }
}
