using UnityEngine;

/// <summary>
/// Marks a Small Unit Spawner's client GameObject. Selecting it (a click on it, which shows its production
/// queue on the command card) is decided by <see cref="UnitCommander"/>, which also does box selection, so the
/// two never race over the same click.
/// </summary>
public class SpawnerClientManager : MonoBehaviour
{
    //NOTE: This could really be replaced with a uid for the building id, but we might want more data later.
    public BuildingData buildingData;
}
