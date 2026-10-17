using Unity.Entities;
using Unity.Mathematics;

public enum UnitType
{
    None,
    Tank,
    /// <summary>Builds from blueprints, finishes walls and repairs buildings; does not attack.</summary>
    Builder,
    /// <summary>Tunnels through rock, gems and walls.</summary>
    Digger,
    /// <summary>Small, fast, widest sight: lights targets for long-range units.</summary>
    Scout,
    /// <summary>Strong and slow; large pathing class, Heavy armour.</summary>
    Heavy,
    /// <summary>Direct-fire splash with a long range and a minimum range; large pathing class.</summary>
    Artillery,
    /// <summary>Very long range, one big hit, minimum range.</summary>
    Sniper,
    /// <summary>Short range with splash.</summary>
    Flamethrower,
    /// <summary>Runs at an enemy and detonates on contact or on a Detonate order; killed first, it does not explode.</summary>
    SuicideBomber
}

public struct HealthComponent : IComponentData
{
    /// <summary>Network id of the entity this health belongs to.</summary>
    public int entityId;
    public float currentHealth;
    public float maxHealth;
}
