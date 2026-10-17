using System;
using System.Collections.Generic;
using System.IO;
using Config;
using Mirror;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Dev.Logging;
using WAR2D.Net.Replay;
using WAR2D.Net.Replication;
using WAR2D.Net.Steam;
using WAR2D.World;

namespace WAR2D.Client.Replay
{
    public sealed class ReplayCursor : IDisposable
    {
        private readonly string path;
        private FileStream file;
        private ReplayReader reader;
        private bool havePending;
        private int pendingTick;
        private ReplayChannel pendingChannel;
        private byte[] pendingBytes = new byte[0];
        private int pendingLength;
    
        public ReplayHeader Header { get; private set; }
        public float Seconds { get; private set; }
        public float DurationSeconds { get; }
        public string Error => reader?.Error;
    
        public ReplayCursor(string path)
        {
            this.path = path;
            int last = 0;
            Open();
            while (reader.TryNext(out int t, out _, out _)) last = t;
            DurationSeconds = last * Header.TickSeconds;
            Restart();
        }
    
        private void Open()
        {
            reader?.Dispose();
            file = File.OpenRead(path);
            if (!ReplayReader.TryOpen(file, out reader, out string error)) { file.Dispose(); throw new InvalidDataException(error); }
            Header = reader.Header;
        }
    
        public void Restart() { Open(); Seconds = 0f; havePending = false; }
    
        /// <summary>Applies every record whose tick time is ≤ <paramref name="seconds"/>, in order. Never goes back (use <see cref="Restart"/>).</summary>
        public void AdvanceTo(float seconds, Action<int, ReplayChannel, ArraySegment<byte>> apply)
        {
            if (seconds < Seconds) return;
            Seconds = seconds;
            while (true)
            {
                if (!havePending)
                {
                    if (!reader.TryNext(out pendingTick, out pendingChannel, out ArraySegment<byte> p)) return;
                    if (pendingBytes.Length < p.Count) pendingBytes = new byte[Math.Max(p.Count, pendingBytes.Length * 2)];
                    Array.Copy(p.Array, p.Offset, pendingBytes, 0, p.Count);
                    pendingLength = p.Count;
                    havePending = true;
                }
                if (pendingTick * Header.TickSeconds > seconds + 1e-4f) return;
                havePending = false;
                apply(pendingTick, pendingChannel, new ArraySegment<byte>(pendingBytes, 0, pendingLength));
            }
        }
    
        public void Dispose() { reader?.Dispose(); reader = null; }
    }

    /// <summary>
    /// Plays a recording offline: rebuilds the seeded map, feeds the recorded payloads into a
    /// <see cref="ClientWorld"/>, <see cref="ClientBuildings"/> and <see cref="ClientTerrain"/> the way a live
    /// client receives them, with no server and no fog. Speed 0.5 to 8; seeking back restarts and fast-forwards.
    /// </summary>
    public sealed class ReplayPlayer : MonoBehaviour
    {
        public static ReplayPlayer Instance { get; private set; }

        private ReplayCursor cursor;
        private MapStore map;
        private ClientWorld view;
        private bool quiet;
        private IReadOnlyList<(int owner, float resources, int units, int buildings)> timeline = Array.Empty<(int, float, int, int)>();

        public float Speed = 1f;
        public bool Playing = true;
        public float Seconds => cursor != null ? cursor.Seconds : 0f;
        public float DurationSeconds => cursor != null ? cursor.DurationSeconds : 0f;
        public ReplayHeader Header => cursor?.Header;
        public IReadOnlyList<(int owner, float resources, int units, int buildings)> LatestTimeline => timeline;

        /// <summary>Opens a recording and shows it; null with <paramref name="error"/> when it can't be played.</summary>
        public static ReplayPlayer Open(string path, out string error)
        {
            error = null;
            if (NetworkClient.active || NetworkServer.active) { error = "leave the game first"; return null; }
            Instance?.Close();
            ReplayCursor cursor;
            try { cursor = new ReplayCursor(path); }
            catch (Exception e) when (e is IOException || e is InvalidDataException) { error = e.Message; return null; }
            ReplayHeader h = cursor.Header;
            if (h.MapSize < 16 || h.MapSize > 4096 || h.TickSeconds > 1f || h.Players == null)
            {
                error = "not a playable replay (bad map size or tick)";
                cursor.Dispose();
                return null;
            }
            if (!GameVersion.IsCompatible(cursor.Header.GameVersion, Application.version))
            {
                error = "recorded with version " + cursor.Header.GameVersion;
                cursor.Dispose();
                return null;
            }
            MenuBattle.Instance?.TearDown();
            var player = new GameObject("ReplayPlayer").AddComponent<ReplayPlayer>();
            player.cursor = cursor;
            player.Build();
            return player;
        }

        private void Awake() { Instance = this; }

        private void Build()
        {
            // The menu battle's view (torn down with a deferred Destroy) would make ours refuse to start.
            if (ClientWorld.Instance != null) DestroyImmediate(ClientWorld.Instance.gameObject);
            GameConfigData config = ConfigLoader.LoadConfig();
            map = MapStore.Generate(cursor.Header.MapSize, cursor.Header.Seed, config.Match.Map.GemChance);
            MapView.Show(map.Grid);
            ClientBuildings.Clear();
            ClientTerrain.Clear();
            ClientTerrain.Current.Init(map.Grid);
            ClientTerrain.Current.Changed += OnTerrainChanged;
            ReplayHeader header = cursor.Header;
            view = new GameObject("ReplayView").AddComponent<ClientWorld>();
            view.ColourIndexOf = owner =>
            {
                int i = header.Players.FindIndex(p => p.OwnerId == owner);
                return i >= 0 ? header.Players[i].Colour : 0;
            };
            Camera cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            view.ViewCamera = cam;
            if (cam != null)
            {
                cam.orthographic = true;
                cam.orthographicSize = 26f;
                cam.transform.position = new Vector3(map.Grid.Width / 2f, map.Grid.Height / 2f, cam.transform.position.z);
            }
        }

        private void OnTerrainChanged(IReadOnlyList<int2> tiles)
        {
            if (map == null) return;
            byte[] known = ClientTerrain.Current.Tiles;
            var grid = map.Grid.Tiles;
            foreach (int2 t in tiles) grid[map.Grid.Index(t)] = known[map.Grid.Index(t)];
            MapView.Refresh(tiles);
        }

        private void Update()
        {
            if (cursor == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f) * Speed; // a hitch slows playback rather than skipping it
            if (Playing)
            {
                cursor.AdvanceTo(cursor.Seconds + dt, Apply);
                view.AdvanceClock(dt);
            }
        }

        private void LateUpdate()
        {
            if (view != null) view.Draw(null);
        }

        private void Apply(int tick, ReplayChannel channel, ArraySegment<byte> payload)
        {
            switch (channel)
            {
                case ReplayChannel.UnitsReliable:
                case ReplayChannel.UnitsUnreliable:
                    view.Apply(new ReplicationBatch { Tick = tick, Payload = payload });
                    break;
                case ReplayChannel.Buildings: BuildingCodec.Apply(ClientBuildings.Current, payload); break;
                case ReplayChannel.Terrain: TerrainCodec.Apply(ClientTerrain.Current, payload); break;
                case ReplayChannel.Explosions:
                case ReplayChannel.Blasts:
                case ReplayChannel.BomberBlasts:
                    if (quiet) break;
                    for (int i = 0; i + 8 <= payload.Count; i += 8)
                    {
                        var p = new Vector2(BitConverter.ToSingle(payload.Array, payload.Offset + i), BitConverter.ToSingle(payload.Array, payload.Offset + i + 4));
                        if (channel == ReplayChannel.Explosions) Effects.Explosion(p); else if (channel == ReplayChannel.Blasts) Effects.BombBlast(p); else Effects.BomberBlast(p);
                    }
                    break;
                case ReplayChannel.Timeline:
                    try
                    {
                        var rows = Newtonsoft.Json.Linq.JArray.Parse(System.Text.Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count));
                        var list = new List<(int, float, int, int)>();
                        foreach (var r in rows) list.Add(((int)r["Item1"], (float)r["Item2"], (int)r["Item3"], (int)r["Item4"]));
                        timeline = list;
                    }
                    catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is NullReferenceException || e is InvalidCastException) { }
                    break;
                case ReplayChannel.End: Playing = false; break;
            }
        }

        /// <summary>Jumps to a time. Going back restarts the recording and fast-forwards (no effects).</summary>
        public void Seek(float seconds)
        {
            if (cursor == null) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            seconds = Mathf.Clamp(seconds, 0f, cursor.DurationSeconds);
            if (seconds < cursor.Seconds) { Reset(); cursor.Restart(); }
            quiet = true;
            cursor.AdvanceTo(seconds, Apply);
            quiet = false;
            view.AdvanceClock(0f);
            WarLog.Info(LogCategory.Replay, $"seek to {seconds:0.0} s took {watch.ElapsedMilliseconds} ms");
        }

        private void Reset()
        {
            // At once: ClientWorld.OnDestroy clears the client stores, and a second ClientWorld refuses to start while the first exists.
            if (view != null) DestroyImmediate(view.gameObject);
            ClientTerrain.Current.Changed -= OnTerrainChanged;
            ClientBuildings.Clear();
            ClientTerrain.Clear();
            ClientTerrain.Current.Init(map.Grid);
            ClientTerrain.Current.Changed += OnTerrainChanged;
            ReplayHeader header = cursor.Header;
            view = new GameObject("ReplayView").AddComponent<ClientWorld>();
            view.ColourIndexOf = owner => { int i = header.Players.FindIndex(p => p.OwnerId == owner); return i >= 0 ? header.Players[i].Colour : 0; };
            view.ViewCamera = Camera.main;
            map.Dispose();
            map = MapStore.Generate(header.MapSize, header.Seed, ConfigLoader.LoadConfig().Match.Map.GemChance);
            MapView.Show(map.Grid);
        }

        /// <summary>Tears the viewer down and returns the menu to normal.</summary>
        public void Close()
        {
            if (view != null) DestroyImmediate(view.gameObject);
            view = null;
            ClientTerrain.Current.Changed -= OnTerrainChanged;
            MapView.Hide();
            ClientBuildings.Clear();
            ClientTerrain.Clear();
            cursor?.Dispose();
            cursor = null;
            map?.Dispose();
            map = null;
            if (Instance == this) Instance = null;
            Destroy(gameObject);
        }

        private void OnDestroy() { if (Instance == this) Instance = null; }
    }
}
