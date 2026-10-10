/// <summary>Spawner production cadence.</summary>
public static class SpawnerRules
{
    /// <summary>Maximum units a single spawner may have queued.</summary>
    public const int MaxQueue = 100;

    /// <summary>The unit a spawner building produces (None for buildings that don't spawn).</summary>
    public static UnitType UnitFor(BuildingType building) => building == BuildingType.SmallUnitSpawner ? UnitType.Tank : UnitType.None;

    /// <summary>True when enough time has passed for the next unit at <paramref name="spawnRate"/> units per second.</summary>
    public static bool CanSpawn(float secondsSinceLastSpawn, float spawnRate) =>
        spawnRate > 0f && secondsSinceLastSpawn >= 1f / spawnRate;

    /// <summary>Adds one unit to a queue below <see cref="MaxQueue"/>; false (unchanged) when it is full or invalid.</summary>
    public static bool TryEnqueue(ref int count)
    {
        if (count < 0 || count >= MaxQueue) return false;
        count++;
        return true;
    }

    /// <summary>
    /// Removes one unit from a non-empty queue; false (unchanged) when it is empty or invalid. Nothing is
    /// refunded: a unit's cost is charged when it spawns.
    /// </summary>
    public static bool TryDequeue(ref int count)
    {
        if (count <= 0 || count > MaxQueue) return false;
        count--;
        return true;
    }
}
