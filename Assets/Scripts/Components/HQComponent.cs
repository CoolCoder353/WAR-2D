using Unity.Entities;

/// <summary>
/// Marks an entity as a Headquarters building.
/// The HQ is critical to player survival - destruction means elimination.
/// </summary>
public struct HQComponent : IComponentData
{
    /// <summary>Owner's player ID (netId as int)</summary>
    public int ownerId;
}
