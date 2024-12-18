using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

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

    public override void Bake(SpawnerAuthoring spawnerAuthoring)
    {
        var entity = GetEntity(spawnerAuthoring, TransformUsageFlags.Dynamic);
        SpawnerData spawnerData = new SpawnerData
        {
            prefab = GetEntity(spawnerAuthoring.prefab, TransformUsageFlags.Dynamic),
            count = spawnerAuthoring.count,
            position = new float2(spawnerAuthoring.position.x, spawnerAuthoring.position.y)
        };

        AddComponent(entity, spawnerData);

    }
}