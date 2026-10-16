using System;

/// <summary>
/// The gate for dev-only server APIs (the performance harness's bots, free buildings and units). They
/// work only when the process was started with <c>-perf</c>, during a debug API command, or when a test turns them on.
/// </summary>
public static class DevApi
{
    private static bool? commandLine;

    /// <summary>Test hook: enables the dev APIs without <c>-perf</c>.</summary>
    internal static bool AllowForTests;

    /// <summary>True when the command line contains <c>-perf</c>.</summary>
    public static bool PerfFlag => commandLine ??= Array.IndexOf(Environment.GetCommandLineArgs(), "-perf") >= 0;

    private static int scopes;

    public static bool Allowed => PerfFlag || AllowForTests || scopes > 0;

    /// <summary>Allows the dev APIs until the returned scope is disposed (the debug API wraps each command in one).</summary>
    public static IDisposable Scope() { scopes++; return new ScopeHandle(); }

    private sealed class ScopeHandle : IDisposable
    {
        private bool done;
        public void Dispose() { if (!done) { done = true; scopes--; } }
    }
}
