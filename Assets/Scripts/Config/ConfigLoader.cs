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

        public static GameConfigData LoadConfig()
        {
            if (_cached != null) return _cached;

            var errors = new List<string>();
            TextAsset xml = UnityEngine.Resources.Load<TextAsset>("GameConfig");
            if (xml == null)
            {
                errors.Add("Resources/GameConfig.xml not found.");
                _cached = new GameConfigData();
            }
            else
            {
                _cached = ConfigParser.Parse(xml.text, errors);
            }

            Errors = errors;
            foreach (string error in errors)
            {
                Debug.LogError($"[GameConfig] {error}");
            }
            return _cached;
        }

        internal static void OverrideForTests(GameConfigData config)
        {
            _cached = config;
            Errors = Array.Empty<string>();
        }

        internal static void ResetForTests()
        {
            _cached = null;
            Errors = Array.Empty<string>();
        }
    }
}
