using Config;

/// <summary>
/// The Radar and Jammer families: a Large Radar and a Large Jammer do what the small ones do, with a longer reach
/// (their sight is the per-building <c>Sight</c>; their jam radius is <see cref="JamRadius"/>). Pure.
/// </summary>
public static class SensorRules
{
    /// <summary>True for a building that sees over walls and pings on contact.</summary>
    public static bool IsRadar(BuildingType type) => type == BuildingType.Radar || type == BuildingType.LargeRadar;

    /// <summary>True for a building that jams.</summary>
    public static bool IsJammer(BuildingType type) => type == BuildingType.Jammer || type == BuildingType.LargeJammer;

    /// <summary>The tiles a jammer of <paramref name="type"/> jams.</summary>
    public static float JamRadius(BuildingType type, SiegeConfig siege) => type == BuildingType.LargeJammer ? siege.LargeJamRadius : siege.JamRadius;
}
