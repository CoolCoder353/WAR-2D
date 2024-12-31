using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Mirror;
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



}
public class MovementAuthoring : MonoBehaviour
{
    public float speed;
    public float acceleration;
    public float rotationSpeed;
    public float rotationAcceleration;




    private class Baker : Baker<MovementAuthoring>
    {
        [ServerCallback]
        public override void Bake(MovementAuthoring authoring)
        {
            var entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
            AddComponent(entity, new MovementComponent { speed = authoring.speed, acceleration = authoring.acceleration, rotationSpeed = authoring.rotationSpeed, rotationAcceleration = authoring.rotationAcceleration });
            AddComponent(entity, new ClientUnit { spriteName = "default" });
            AddBuffer<PathPoint>(entity);
        }
    }
}
