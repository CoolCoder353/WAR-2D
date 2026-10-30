using WAR2D.Dev.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Config
{
    /// <summary>
    /// Loads and caches Resources/GameConfig.xml. Check <see cref="IsValid"/> before starting a server.
    /// </summary>
    public static class ConfigLoader
    {
        private static GameConfigData _cached;

        /// <summary>Validation errors from the last load. Empty when the config is valid.</summary>
        public static IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();

        public static bool IsValid => Errors.Count == 0;

        private static Action<GameConfigData> _testOverride;

        public static GameConfigData LoadConfig()
        {
            if (_cached != null) return _cached;

            var errors = new List<string>();
            TextAsset xml = UnityEngine.Resources.Load<TextAsset>("GameConfig");
            string devFile = DevConfigFile();
            if (devFile != null)
            {
                // Dev only (-perf): tuning runs (tools/ai-bench.sh) try a GameConfig.xml from disk without a rebuild.
                _cached = ConfigParser.Parse(System.IO.File.ReadAllText(devFile), errors);
                WarLog.Info(LogCategory.Sim, $"GameConfig from {devFile} (-configFile)");
            }
            else if (xml == null)
            {
                errors.Add("Resources/GameConfig.xml not found.");
                _cached = new GameConfigData();
            }
            else
            {
                _cached = ConfigParser.Parse(xml.text, errors);
            }

            _testOverride?.Invoke(_cached);
            Errors = errors;
            foreach (string error in errors)
            {
                WarLog.Error(LogCategory.Sim, $"{error}");
            }
            return _cached;
        }

        /// <summary>The <c>-configFile &lt;path&gt;</c> argument when the process runs with <c>-perf</c> and the file exists; else null.</summary>
        private static string DevConfigFile()
        {
            if (!DevApi.PerfFlag) return null;
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-configFile");
            return i >= 0 && i + 1 < args.Length && System.IO.File.Exists(args[i + 1]) ? args[i + 1] : null;
        }

        internal static void OverrideForTests(GameConfigData config)
        {
            _cached = config;
            Errors = Array.Empty<string>();
        }

        /// <summary>Applies <paramref name="edit"/> to the loaded config (now and after every reload) until <see cref="ResetForTests"/>.</summary>
        internal static void OverrideForTests(Action<GameConfigData> edit)
        {
            _testOverride = edit;
            if (_cached != null) edit(_cached);
        }

        internal static void ResetForTests()
        {
            _testOverride = null;
            _cached = null;
            Errors = Array.Empty<string>();
        }
    }
}
