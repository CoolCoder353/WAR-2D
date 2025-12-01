using Unity.Entities;

public struct ResourceCostComponent : IComponentData
{
    public float upfrontCost;
    public float runningCostPerSecond;
    public float timeSinceLastCost;
}
