using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Server-side unit identity and combat state. Also the struct replicated to clients.
/// </summary>
[System.Serializable]
public struct ClientUnit : IComponentData
{
    public float2 position;
    public UnitType spriteName;
    /// <summary>Network id from NetIdAllocator (never an ECS Entity.Index).</summary>
    public int id;
    /// <summary>Owning player's netId as int.</summary>
    public int ownerId;
    /// <summary>Network id of the current target, or -1.</summary>
    public int targetId;
    public double lastAttackTime;
}

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

public struct DamageComponent : IComponentData
{
    public float damageAmount;
    public float range;
    /// <summary>Seconds between attacks.</summary>
    public float attackSpeed;
}

public struct PathPoint : IBufferElementData
{
    public int2 position;
}

public struct MovementComponent : IComponentData
{
    public float speed;
    public float acceleration;
    public float rotationSpeed;
    public float rotationAcceleration;
    public float currentSpeed;
    public float currentRotationSpeed;
    /// <summary>Seconds the unit has been waiting for a tile claimed by another unit.</summary>
    public float blockedSeconds;
}
