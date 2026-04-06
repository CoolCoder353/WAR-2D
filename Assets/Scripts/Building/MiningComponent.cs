using Unity.Entities;
using Unity.Mathematics;

public struct MiningComponent : IComponentData
{
    public float miningRate;
    public float timeSinceLastMining;
    public bool isActive;
    // Direction is calculated from LocalTransform rotation, no need to store it
}
