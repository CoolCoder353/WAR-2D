// DebugRegistry.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace WAR2D.Dev.DebugApi
{
    public enum DebugRole { Any, Server, Client }

    /// <summary>A refusal the caller should read (bad argument, wrong state).</summary>
    public sealed class DebugError : Exception { public DebugError(string message) : base(message) { } }

    /// <summary>Returned by a handler that finishes later; <see cref="Poll"/> returns null until it has a result.</summary>
    public sealed class DebugWait
    {
        public Func<object> Poll;
        public float TimeoutSeconds = 30f;
    }

    public delegate object DebugHandler(JObject args);

    /// <summary>The API's commands by name.</summary>
    public sealed class DebugRegistry
    {
        public sealed class Entry
        {
            public string Name, Help;
            public DebugRole Role;
            public DebugHandler Handler;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public void Add(string name, DebugRole role, string help, DebugHandler handler) =>
            entries[name] = new Entry { Name = name, Role = role, Help = help, Handler = handler };

        public bool TryGet(string name, out Entry entry) => entries.TryGetValue(name ?? "", out entry);

        public IEnumerable<Entry> All => entries.Values;
    }
}
