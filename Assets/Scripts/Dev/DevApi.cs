using System;

/// <summary>
/// The gate for dev-only server APIs (the performance harness's bots, free buildings and units). They
/// work only when the process was started with <c>-perf</c>, or when a test turns them on.
/// </summary>
public static class DevApi
{
    private static bool? commandLine;

    /// <summary>Test hook: enables the dev APIs without <c>-perf</c>.</summary>
    internal static bool AllowForTests;

    /// <summary>True when the command line contains <c>-perf</c>.</summary>
    public static bool PerfFlag => commandLine ??= Array.IndexOf(Environment.GetCommandLineArgs(), "-perf") >= 0;

    public static bool Allowed => PerfFlag || AllowForTests;
}
