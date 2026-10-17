// DebugArgs.cs
using System;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>Typed reads of a request's <c>args</c>; a missing or mistyped value throws <see cref="DebugError"/>.</summary>
    public static class DebugArgs
    {
        public static bool Has(JObject a, string name) => a != null && a[name] != null && a[name].Type != JTokenType.Null;

        public static int Int(JObject a, string name, int? fallback = null)
        {
            if (!Has(a, name)) return fallback ?? throw new DebugError($"missing '{name}'");
            if (a[name].Type != JTokenType.Integer) throw new DebugError($"'{name}' must be a whole number");
            return (int)a[name];
        }

        public static float Float(JObject a, string name, float? fallback = null)
        {
            if (!Has(a, name)) return fallback ?? throw new DebugError($"missing '{name}'");
            if (a[name].Type != JTokenType.Integer && a[name].Type != JTokenType.Float) throw new DebugError($"'{name}' must be a number");
            float v = (float)a[name];
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new DebugError($"'{name}' must be finite");
            return v;
        }

        public static string Str(JObject a, string name, string fallback = null)
        {
            if (!Has(a, name)) return fallback ?? throw new DebugError($"missing '{name}'");
            return (string)a[name];
        }

        public static bool Bool(JObject a, string name, bool? fallback = null)
        {
            if (!Has(a, name)) return fallback ?? throw new DebugError($"missing '{name}'");
            if (a[name].Type != JTokenType.Boolean) throw new DebugError($"'{name}' must be true or false");
            return (bool)a[name];
        }

        public static int2 Int2(JObject a, string name)
        {
            if (!(a?[name] is JArray arr) || arr.Count != 2) throw new DebugError($"'{name}' must be [x, y]");
            return new int2((int)arr[0], (int)arr[1]);
        }

        /// <summary>A box as [x0, y0, x1, y1] (inclusive tiles, any corner order).</summary>
        public static (int2 min, int2 max) Box(JObject a, string name)
        {
            if (!(a?[name] is JArray arr) || arr.Count != 4) throw new DebugError($"'{name}' must be [x0, y0, x1, y1]");
            int2 p = new int2((int)arr[0], (int)arr[1]), q = new int2((int)arr[2], (int)arr[3]);
            return (math.min(p, q), math.max(p, q));
        }

        public static T Enum<T>(JObject a, string name, T? fallback = null) where T : struct
        {
            if (!Has(a, name)) return fallback ?? throw new DebugError($"missing '{name}'");
            if (System.Enum.TryParse((string)a[name], true, out T v) && System.Enum.IsDefined(typeof(T), v)) return v;
            throw new DebugError($"'{name}' must be one of {string.Join(", ", System.Enum.GetNames(typeof(T)))}");
        }
    }
}
