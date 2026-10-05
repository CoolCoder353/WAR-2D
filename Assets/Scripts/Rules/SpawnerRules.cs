/// <summary>Spawner production cadence.</summary>
public static class SpawnerRules
{
    /// <summary>Maximum units a single spawner may have queued.</summary>
    public const int MaxQueue = 100;

    /// <summary>True when enough time has passed for the next unit at <paramref name="spawnRate"/> units per second.</summary>
    public static bool CanSpawn(float secondsSinceLastSpawn, float spawnRate) =>
        spawnRate > 0f && secondsSinceLastSpawn >= 1f / spawnRate;
}
