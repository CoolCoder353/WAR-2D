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
        int next = (int)System.Math.Clamp((long)count + delta, 0L, MaxQueue); // long: a huge delta must not wrap
        int changed = next - count;
        count = next;
        return changed;
    }

    private static readonly UnitType[] NoUnits = System.Array.Empty<UnitType>();
    private static readonly UnitType[] HqUnits = { UnitType.Builder };
    private static readonly UnitType[] BarracksUnits = { UnitType.Scout, UnitType.Tank, UnitType.Flamethrower };
    private static readonly UnitType[] HeavyFactoryUnits = { UnitType.Heavy, UnitType.Artillery, UnitType.Sniper };
    private static readonly UnitType[] SiegeWorkshopUnits = { UnitType.Digger, UnitType.SuicideBomber };

    /// <summary>The units a building may produce, in button order (empty for non-producers).</summary>
    public static UnitType[] Produces(BuildingType building) => building switch
    {
        BuildingType.Base => HqUnits,
        BuildingType.Barracks => BarracksUnits,
        BuildingType.HeavyFactory => HeavyFactoryUnits,
        BuildingType.SiegeWorkshop => SiegeWorkshopUnits,
        _ => NoUnits,
    };

    /// <summary>True when the building produces that unit.</summary>
    public static bool MayProduce(BuildingType building, UnitType unit) => System.Array.IndexOf(Produces(building), unit) >= 0;

    /// <summary>True when the building has a production queue.</summary>
    public static bool IsProducer(BuildingType building) => Produces(building).Length > 0;

    /// <summary>The type a new producer starts on (None for non-producers).</summary>
    public static UnitType DefaultUnit(BuildingType building) => Produces(building) is { Length: > 0 } list ? list[0] : UnitType.None;

    /// <summary>True when a unit that takes <paramref name="buildSeconds"/> is done after <paramref name="progress"/> seconds.</summary>
    public static bool CanSpawn(float progress, float buildSeconds) => buildSeconds > 0f && progress >= buildSeconds;
}
