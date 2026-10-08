using Unity.Entities;
using Unity.Mathematics;

public enum UnitType
{
    None,
    Tank
}

public struct HealthComponent : IComponentData
{
    /// <summary>Network id of the entity this health belongs to.</summary>
    public int entityId;
    public float currentHealth;
    public float maxHealth;
}
