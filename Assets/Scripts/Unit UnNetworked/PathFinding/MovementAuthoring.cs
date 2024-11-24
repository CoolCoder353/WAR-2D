using UnityEngine;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;

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
        public override void Bake(MovementAuthoring authoring)
        {
            var entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
            AddComponent(entity, new MovementComponent { speed = authoring.speed, acceleration = authoring.acceleration, rotationSpeed = authoring.rotationSpeed, rotationAcceleration = authoring.rotationAcceleration });
        }
    }
}
