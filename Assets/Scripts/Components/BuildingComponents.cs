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
    Base,
    /// <summary>A hidden mine: detonates when an enemy steps on it; visible only to its owner's side.</summary>
    Bomb,
    /// <summary>Sees over walls and pings its owner when an enemy comes into range.</summary>
    Radar
}

/// <summary>A building under construction (v0.7). Removed when <see cref="Progress"/> reaches 1.</summary>
public struct Construction : IComponentData
{
    /// <summary>Work done over max health, 0..1.</summary>
    public float Progress;
}

/// <summary>Per building slot flags in <c>SimData.BuildingFlags</c>.</summary>
public static class BuildingFlags
{
    public const byte UnderConstruction = 1;
    /// <summary>A hidden bomb: never a target for enemy units and never sent to players who may not know it.</summary>
    public const byte Hidden = 2;
    /// <summary>A completed Radar: its sight ignores line of sight.</summary>
    public const byte SeesThroughWalls = 4;
}

public struct SpawnerData : IComponentData
{
    public float2 position;
    /// <summary>Number of units queued.</summary>
    public int count;
    public UnitType unitType;
    public int ownerId;
    /// <summary>Units per second (from BuildingConfig.SpawnRate).</summary>
    public float spawnRate;
    public float timeSinceLastSpawn;
    /// <summary>The rally tile new units are sent to, when <see cref="hasRally"/> is set (the owner's choice).</summary>
    public int2 rally;
    public bool hasRally;
}
