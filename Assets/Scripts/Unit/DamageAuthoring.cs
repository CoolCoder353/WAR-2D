using Unity.Entities;
using UnityEngine;

public struct DamageComponent : IComponentData
{
    public float damageAmount;
    public float range;
    public float attackSpeed;
}

public class DamageAuthoring : MonoBehaviour
{
    public float damageAmount = 10f;
    public float range = 5f;
    public float attackSpeed = 1f;

    private class Baker : Baker<DamageAuthoring>
    {
        public override void Bake(DamageAuthoring authoring)
        {
            var entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
            AddComponent(entity, new DamageComponent
            {
                damageAmount = authoring.damageAmount,
                range = authoring.range,
                attackSpeed = authoring.attackSpeed
            });
        }
    }
}
