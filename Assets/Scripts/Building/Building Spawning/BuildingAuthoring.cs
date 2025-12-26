using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

public class BuildingAuthoring : MonoBehaviour
{
    public float2 position;

    public BuildingType buildingType;
    public int id;
}

public struct BuildingData : IComponentData
{
    public float2 position;

    public int id;

    public BuildingType buildingType;

    public int ownerId;

    public float rotation; // Rotation in degrees (0, 90, 180, 270)


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


//Note: We do not use this in an entity system yet, but we will still store these as entites for memory management
public class BuildingBaker : Baker<BuildingAuthoring>
{

    // [ServerCallback]
    public override void Bake(BuildingAuthoring buildingAuthoring)
    {
        var entity = GetEntity(buildingAuthoring, TransformUsageFlags.Dynamic);
        AddComponent(entity, new BuildingData
        {
            position = buildingAuthoring.position,
            id = buildingAuthoring.id,
            buildingType = buildingAuthoring.buildingType
        });

        // Add default health component, will be overwritten by config at runtime if needed
        AddComponent(entity, new HealthComponent
        {
            currentHealth = 100,
            maxHealth = 100
        });

    }
}