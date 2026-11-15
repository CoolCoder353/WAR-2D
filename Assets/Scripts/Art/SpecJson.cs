using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>A parameter file that can't be used: the message names the file and, where known, the field.</summary>
    public sealed class SpecException : Exception
    {
        public SpecException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Reads the generators' parameter files (sound and song specs under <c>Assets/Art/Audio/</c>) strictly:
    /// enums by name only, unknown fields refused, and numbers checked against the <see cref="RangeAttribute"/>
    /// or <see cref="MinAttribute"/> on their field. <c>JsonUtility</c> does none of that: it reads an enum only
    /// from a number, so <c>"wave": "Noise"</c> silently stayed <c>Square</c> (known-issues, 2026-11-15).
    /// </summary>
    public static class SpecJson
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            Converters = { new StringEnumConverter { AllowIntegerValues = false } },
        };

        /// <summary>The spec in the file at <paramref name="path"/>.</summary>
        public static T ReadFile<T>(string path) where T : class => Read<T>(File.ReadAllText(path), path);

        /// <summary>The spec in <paramref name="json"/>; <paramref name="file"/> is only named in errors.</summary>
        public static T Read<T>(string json, string file) where T : class
        {
            T spec;
            try
            {
                spec = JsonConvert.DeserializeObject<T>(json, Settings);
            }
            catch (JsonException e)
            {
                throw new SpecException($"{file}: {e.Message}", e);
            }
            if (spec == null) throw new SpecException($"{file}: empty spec");
            CheckRanges(spec, file);
            return spec;
        }

        /// <summary>Throws when a number field of <paramref name="spec"/> is outside the range its attributes give.</summary>
        public static void CheckRanges(object spec, string file)
        {
            foreach (FieldInfo field in spec.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(float) && field.FieldType != typeof(int)) continue;
                float value = Convert.ToSingle(field.GetValue(spec));
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new SpecException($"{file}: '{field.Name}' is not a number");
                var range = field.GetCustomAttribute<RangeAttribute>();
                if (range != null && (value < range.min || value > range.max))
                    throw new SpecException($"{file}: '{field.Name}' is {value}, outside {range.min} to {range.max}");
                var min = field.GetCustomAttribute<MinAttribute>();
                if (min != null && value < min.min) throw new SpecException($"{file}: '{field.Name}' is {value}, below {min.min}");
            }
        }
    }
}
