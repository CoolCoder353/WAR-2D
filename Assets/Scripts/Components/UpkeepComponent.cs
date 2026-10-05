using Unity.Entities;

/// <summary>Per-second running cost for a unit or building. Unpaid entities decay.</summary>
public struct UpkeepComponent : IComponentData
{
    public int ownerId;
    public float runningCostPerSecond;
    public float timeSinceLastCharge;
    /// <summary>True while the last payment attempt failed.</summary>
    public bool unpaid;
}
