using Unity.Entities;

public struct BuildingResourceComponent : IComponentData
{
    public float upfrontCost;
    public float runningCostPerSecond;
    public float timeSinceLastCost;
}
