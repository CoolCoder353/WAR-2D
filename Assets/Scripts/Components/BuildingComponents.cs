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
    Barracks,
    Base,
    /// <summary>A hidden mine: detonates when an enemy steps on it; visible only to its owner's side.</summary>
    Bomb,
    /// <summary>Sees over walls and pings its owner when an enemy comes into range.</summary>
    Radar,
    /// <summary>Makes Heavy, Artillery and Sniper (3x3).</summary>
    HeavyFactory,
    /// <summary>Makes Digger and Suicide bomber (2x2).</summary>
    SiegeWorkshop,
    // v0.13: appended, so the bytes already on the wire and in replays keep their meaning.
    /// <summary>A static defence (1x1): shoots units, then buildings, then walls, like a unit that can't move.</summary>
    GunTurret,
    /// <summary>Jams its radius (2x2): enemies see in it only up close, and their Radars not at all.</summary>
    Jammer
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
    /// <summary>Its upkeep went unpaid at the last charge (<see cref="UpkeepComponent.unpaid"/>): a turret doesn't fire, a Jammer doesn't jam (v0.13).</summary>
    public const byte Unpaid = 8;
    /// <summary>A completed Jammer (it has the <see cref="Jammer"/> tag): its radius is jammed while it is paid (v0.13).</summary>
    public const byte Jams = 16;
}

/// <summary>
/// The weapon state of a building that attacks (v0.13: the Gun turret; any building whose config has
/// <c>Damage</c> above 0). Building slots are refilled every tick, so the state lives here between ticks: the
/// gather copies it into <c>SimData.BuildingTarget</c>/<c>BuildingTargetKind</c>/<c>BuildingCooldown</c> and the
/// next boundary writes it back (<c>SimCommandSystem.WriteBackBuildingWeapons</c>).
/// </summary>
public struct BuildingWeapon : IComponentData
{
    /// <summary>The target as a stable id: a unit or building id, or a wall tile index (see <see cref="TargetKind"/>); -1 for none.</summary>
    public int TargetId;
    /// <summary>What <see cref="TargetId"/> names (<c>WAR2D.Sim.TargetKinds</c>).</summary>
    public byte TargetKind;
    /// <summary>Seconds until it may fire again.</summary>
    public float Cooldown;
}

/// <summary>Tags a Jammer (v0.13): the gather marks its slot <see cref="BuildingFlags.Jams"/> once it is complete, which the jam rebuild reads.</summary>
public struct Jammer : IComponentData { }

public struct SpawnerData : IComponentData
{
    public float2 position;
    /// <summary>The unit type this producer makes (a <see cref="UnitType"/>); a new producer starts on its first type.</summary>
    public byte unitType;
    /// <summary>Units queued of <see cref="unitType"/>, 0..<see cref="SpawnerRules.MaxQueue"/>.</summary>
    public int count;
    public int ownerId;
    /// <summary>Seconds of build time put into the next unit (reaches the unit's <c>BuildSeconds</c>, then it spawns).</summary>
    public float progress;
    /// <summary>The rally tile new units are sent to, when <see cref="hasRally"/> is set (the owner's choice).</summary>
    public int2 rally;
    public bool hasRally;
}
