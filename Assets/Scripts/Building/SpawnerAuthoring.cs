using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Mirror;

public class SpawnerAuthoring : MonoBehaviour
{
    public GameObject prefab;
    public Vector2 position;
    public int count;


    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(position, 1);
    }
}

public struct SpawnerData : IComponentData
{
    public Entity prefab;
    public float2 position;
    public int count;
}


public class SpanwerBaker : Baker<SpawnerAuthoring>
{

    // [ServerCallback]
    public override void Bake(SpawnerAuthoring spawnerAuthoring)
    {
        var entity = GetEntity(spawnerAuthoring, TransformUsageFlags.Dynamic);


        AddComponent(entity, new SpawnerData
        {
            prefab = GetEntity(spawnerAuthoring.prefab, TransformUsageFlags.Dynamic),
            count = 10, //Default to 10 units for debugging purposes
            position = new float2(spawnerAuthoring.position.x, spawnerAuthoring.position.y)
        });

    }
}