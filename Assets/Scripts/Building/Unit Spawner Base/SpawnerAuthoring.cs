using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Mirror;

public class SpawnerAuthoring : MonoBehaviour
{

    public Vector2 position;
    public int count;

    public UnitType unitType;

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(position, 1);
    }
}

public struct SpawnerData : IComponentData
{
    public float2 position;
    public int count;

    public UnitType unitType;
    public int ownerId;
}


public class SpanwerBaker : Baker<SpawnerAuthoring>
{

    // [ServerCallback]
    public override void Bake(SpawnerAuthoring spawnerAuthoring)
    {
        var entity = GetEntity(spawnerAuthoring, TransformUsageFlags.Dynamic);


        AddComponent(entity, new SpawnerData
        {
            count = spawnerAuthoring.count, //Default to 10 units for debugging purposes
            position = new float2(spawnerAuthoring.position.x, spawnerAuthoring.position.y),
            unitType = spawnerAuthoring.unitType,
        });

    }
}