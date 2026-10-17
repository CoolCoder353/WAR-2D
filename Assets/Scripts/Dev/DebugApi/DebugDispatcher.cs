// DebugDispatcher.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WAR2D.Dev.Logging;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>
    /// Runs API requests on the main thread: parses a JSON line, checks the token and the command's role, calls
    /// its handler and replies with <c>{id, ok, result}</c> or <c>{id, ok: false, error}</c>. A handler that
    /// returns a <see cref="DebugWait"/> is polled by <see cref="Pump"/> until it answers or times out.
    /// </summary>
    public sealed class DebugDispatcher
    {
        private sealed class Pending { public long Id; public DebugWait Wait; public float Elapsed; public Action<string> Reply; }

        private readonly DebugRegistry registry;
        private readonly string token;
        private readonly Func<bool> isServer, isClient;
        private readonly List<Pending> pending = new List<Pending>();
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };

        public DebugDispatcher(DebugRegistry registry, string token, Func<bool> isServer, Func<bool> isClient)
        {
            this.registry = registry;
            this.token = token;
            this.isServer = isServer;
            this.isClient = isClient;
        }

        public void Handle(string line, Action<string> reply)
        {
            long id = 0;
            try
            {
                JObject request = JObject.Parse(line);
                id = request["id"]?.Type == JTokenType.Integer ? (long)request["id"] : 0;
                if ((string)request["token"] != token) throw new DebugError("bad or missing token");
                string name = (string)request["cmd"];
                if (!registry.TryGet(name, out DebugRegistry.Entry entry)) throw new DebugError($"unknown command '{name}' (try 'help')");
                if (entry.Role == DebugRole.Server && !isServer()) throw new DebugError($"'{name}' needs the server (host) process");
                if (entry.Role == DebugRole.Client && !isClient()) throw new DebugError($"'{name}' needs a client");
                object result;
                using (DevApi.Scope()) result = entry.Handler(request["args"] as JObject ?? new JObject());
                if (result is DebugWait wait) pending.Add(new Pending { Id = id, Wait = wait, Reply = reply });
                else reply(Ok(id, result));
            }
            catch (DebugError e) { reply(Fail(id, e.Message)); }
            catch (JsonException e) { reply(Fail(id, "bad JSON: " + e.Message)); }
            catch (Exception e)
            {
                WarLog.Error(LogCategory.Dev, $"debug API command failed: {e}");
                reply(Fail(id, e.GetType().Name + ": " + e.Message));
            }
        }

        /// <summary>Polls pending waits (call once a frame).</summary>
        public void Pump(float deltaSeconds)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                p.Elapsed += deltaSeconds;
                object result;
                try { result = p.Wait.Poll(); }
                catch (Exception e) { pending.RemoveAt(i); p.Reply(Fail(p.Id, e.Message)); continue; }
                if (result != null) { pending.RemoveAt(i); p.Reply(Ok(p.Id, result)); }
                else if (p.Elapsed >= p.Wait.TimeoutSeconds) { pending.RemoveAt(i); p.Reply(Fail(p.Id, $"timed out after {p.Wait.TimeoutSeconds:0.#} s")); }
            }
        }

        private static string Ok(long id, object result) =>
            JsonConvert.SerializeObject(new { id, ok = true, result }, Formatting.None, Settings);

        private static string Fail(long id, string error) =>
            JsonConvert.SerializeObject(new { id, ok = false, error }, Formatting.None, Settings);
    }
}
