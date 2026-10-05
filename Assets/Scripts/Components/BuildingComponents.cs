using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Server-side building identity. Also the struct replicated to clients.
/// </summary>
public struct BuildingData : IComponentData
{
    /// <summary>Footprint anchor tile (see Footprint).</summary>
    public float2 position;
    /// <summary>Network id from NetIdAllocator.</summary>
    public int id;
    public BuildingType buildingType;
    /// <summary>Owning player's netId as int.</summary>
    public int ownerId;
    /// <summary>Rotation in degrees (0, 90, 180, 270).</summary>
    public float rotation;

    public static uint IntToUInt(int value)
    {
        return unchecked((uint)value);
    }

    public static int UIntToInt(uint value)
    {
        return unchecked((int)value);
    }
}

public enum BuildingType
{
    None,
    Miner,
    SmallUnitSpawner,
    Base
}

public struct SpawnerData : IComponentData
{
    public float2 position;
    /// <summary>Number of units queued.</summary>
    public int count;
    public UnitType unitType;
    public int ownerId;
}
