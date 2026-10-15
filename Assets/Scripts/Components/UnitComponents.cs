using Unity.Entities;
using Unity.Mathematics;

public enum UnitType
{
    None,
    Tank,
    /// <summary>Builds from blueprints, finishes walls and repairs buildings; does not attack.</summary>
    Builder,
    /// <summary>Tunnels through rock, gems and walls.</summary>
    Digger
}

public struct HealthComponent : IComponentData
{
    /// <summary>Network id of the entity this health belongs to.</summary>
    public int entityId;
    public float currentHealth;
    public float maxHealth;
}
