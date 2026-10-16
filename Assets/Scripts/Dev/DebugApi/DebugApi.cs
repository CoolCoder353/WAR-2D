using System;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>
    /// The control API's gate and identity. Open only in the editor or a development build (<see cref="DevUi.Enabled"/>),
    /// and then only in the editor's play mode or with <c>-debugApi [port]</c>. Never in a release build.
    /// </summary>
    public static class DebugApi
    {
        public const int DefaultPort = 7790;

        /// <summary>Test hook.</summary>
        internal static bool AllowForTests;

        public static bool Allowed =>
            AllowForTests || (DevUi.Enabled && (UnityEngine.Application.isEditor || ArgIndex("-debugApi") >= 0));

        /// <summary>The port: the value after <c>-debugApi</c>, else <see cref="DefaultPort"/>.</summary>
        public static int Port
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                int i = ArgIndex("-debugApi");
                return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p > 1024 && p < 65536 ? p : DefaultPort;
            }
        }

        /// <summary>This process's request token (random per run).</summary>
        public static string Token { get; } = Guid.NewGuid().ToString("N");

        /// <summary>True while the server is listening.</summary>
        public static bool Listening { get; internal set; }

        private static int ArgIndex(string flag) => Array.IndexOf(Environment.GetCommandLineArgs(), flag);
    }
}
