/// <summary>Spawner production cadence.</summary>
public static class SpawnerRules
{
    /// <summary>Maximum units a single spawner may have queued.</summary>
    public const int MaxQueue = 100;

    /// <summary>The queue buttons' steps: −1, +1, +10 and +50 (the approved v0.8 panel).</summary>
    public static readonly int[] QueueSteps = { -1, 1, 10, 50 };

    /// <summary>Most spawners one queue or rally command may name (a selection bound, checked on the server).</summary>
    public const int MaxSpawnersPerCommand = 64;

    /// <summary>True for a step the queue buttons can send.</summary>
    public static bool IsQueueStep(int delta) => System.Array.IndexOf(QueueSteps, delta) >= 0;

    /// <summary>
    /// Adds <paramref name="delta"/> units (or removes them, when negative) within 0..<see cref="MaxQueue"/>;
    /// returns how much the count changed.
    /// </summary>
    public static int Apply(ref int count, int delta)
    {
        if (count < 0 || count > MaxQueue) return 0;
        int next = System.Math.Clamp(count + delta, 0, MaxQueue);
        int changed = next - count;
        count = next;
        return changed;
    }

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
