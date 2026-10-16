using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Mirror;
using UnityEngine;
using WAR2D.Dev.Logging;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>
    /// The control API's socket: a 127.0.0.1 TCP listener whose threads only read and write lines. Requests are
    /// queued and run by <see cref="DebugDispatcher"/> on the main thread in <see cref="Update"/>.
    /// </summary>
    public sealed class DebugServer : MonoBehaviour
    {
        public static DebugServer Instance { get; private set; }

        /// <summary>The command table (shared with the developer console's <c>dev</c> command).</summary>
        public static DebugRegistry Registry { get; } = CreateRegistry();

        public int BoundPort { get; private set; }

        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool stopping;
        private DebugDispatcher dispatcher;
        private readonly ConcurrentQueue<(string line, Action<string> reply)> inbox = new ConcurrentQueue<(string, Action<string>)>();
        private string apiFile;

        private static DebugRegistry CreateRegistry()
        {
            var r = new DebugRegistry();
            DebugCommands.RegisterAll(r);
            return r;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (DebugApi.Allowed) EnsureRunning();
        }

        /// <summary>Starts the server if it isn't running (no-op when the API isn't allowed).</summary>
        public static void EnsureRunning()
        {
            if (Instance != null || !DebugApi.Allowed) return;
            var go = new GameObject("DebugServer");
            DontDestroyOnLoad(go);
            go.AddComponent<DebugServer>();
        }

        /// <summary>Stops the server (tests).</summary>
        public static void Shutdown()
        {
            if (Instance != null) DestroyImmediate(Instance.gameObject);
        }

        private void Awake()
        {
            Instance = this;
            dispatcher = new DebugDispatcher(Registry, DebugApi.Token, () => NetworkServer.active, () => NetworkClient.active);
            try
            {
                listener = new TcpListener(IPAddress.Loopback, DebugApi.Port);
                listener.Start();
            }
            catch (SocketException e)
            {
                WarLog.Error(LogCategory.Dev, $"debug API could not listen on 127.0.0.1:{DebugApi.Port}: {e.Message}");
                Destroy(gameObject);
                return;
            }
            BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            WriteApiFile();
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DebugApi accept" };
            acceptThread.Start();
            DebugApi.Listening = true;
            WarLog.Info(LogCategory.Dev, $"debug API listening on 127.0.0.1:{BoundPort}");
        }

        private void WriteApiFile()
        {
            string dir = Path.Combine(Application.persistentDataPath, "debugapi");
            Directory.CreateDirectory(dir);
            apiFile = Path.Combine(dir, BoundPort + ".json");
            string role = Application.isEditor ? "editor" : "player";
            File.WriteAllText(apiFile, $"{{\"port\":{BoundPort},\"pid\":{System.Diagnostics.Process.GetCurrentProcess().Id},\"token\":\"{DebugApi.Token}\",\"role\":\"{role}\"}}");
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                new Thread(() => Serve(client)) { IsBackground = true, Name = "DebugApi client" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                var gate = new object();
                void Reply(string text) { lock (gate) { try { writer.WriteLine(text); } catch (IOException) { } catch (ObjectDisposedException) { } } }
                try
                {
                    string line;
                    while (!stopping && (line = reader.ReadLine()) != null)
                        if (line.Length > 0 && line.Length <= 1 << 20) inbox.Enqueue((line, Reply));
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void Update()
        {
            int budget = 64; // requests per frame
            while (budget-- > 0 && inbox.TryDequeue(out var item)) dispatcher.Handle(item.line, item.reply);
            dispatcher.Pump(Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            stopping = true;
            try { listener?.Stop(); } catch (SocketException) { }
            if (apiFile != null && File.Exists(apiFile)) File.Delete(apiFile);
            if (Instance == this) { Instance = null; DebugApi.Listening = false; }
        }
    }
}
