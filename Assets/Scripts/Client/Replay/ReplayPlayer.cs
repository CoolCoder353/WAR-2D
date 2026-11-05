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
    /// <summary>Where a replay's camera starts.</summary>
    public static class ReplayOpening
    {
        /// <summary>The first player's HQ tile in a recording's summary; false for a recording without one (cut short, or no HQ placed).</summary>
        public static bool TryFirstHq(SummaryData? summary, out float2 hq)
        {
            hq = default;
            if (!summary.HasValue || summary.Value.HqX == null || summary.Value.HqY == null || summary.Value.HqX.Length == 0 || summary.Value.HqY.Length == 0) return false;
            hq = new float2(summary.Value.HqX[0], summary.Value.HqY[0]);
            return true;
        }
    }

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
        /// <summary>v0.18: the match's graphs and timeline, read when the file opens so they are there from the start.</summary>
        public SummaryData? Summary { get; private set; }
        /// <summary>v0.18: the file has every player's fog (false: too large, or the recording was cut short).</summary>
        public bool HasFog { get; private set; }
        public float Seconds { get; private set; }
        public float DurationSeconds { get; }
        public string Error => reader?.Error;
        /// <summary>True once every record has been read (with or without an <c>End</c> record).</summary>
        public bool AtEnd { get; private set; }
    
        public ReplayCursor(string path)
        {
            this.path = path;
            int last = 0;
            Open();
            bool fog = false, dropped = false;
            while (reader.TryNext(out int t, out ReplayChannel channel, out ArraySegment<byte> payload))
            {
                last = t;
                if (channel == ReplayChannel.TeamFog) fog = true;
                else if (channel == ReplayChannel.TeamFogDropped) dropped = true;
                else if (channel == ReplayChannel.Summary)
                {
                    try { Summary = Newtonsoft.Json.JsonConvert.DeserializeObject<SummaryData>(System.Text.Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count)); }
                    catch (Newtonsoft.Json.JsonException e) { WarLog.Warn(LogCategory.Replay, "unreadable summary: " + e.Message); }
                }
            }
            HasFog = fog && !dropped;
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
    
        public void Restart() { Open(); Seconds = 0f; havePending = false; AtEnd = false; }
    
        /// <summary>Applies every record whose tick time is ≤ <paramref name="seconds"/>, in order. Never goes back (use <see cref="Restart"/>).</summary>
        public void AdvanceTo(float seconds, Action<int, ReplayChannel, ArraySegment<byte>> apply)
        {
            if (seconds < Seconds) return;
            Seconds = seconds;
            while (true)
            {
                if (!havePending)
                {
                    if (!reader.TryNext(out pendingTick, out pendingChannel, out ArraySegment<byte> p)) { AtEnd = true; return; }
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
        private Camera replayCamera;
        private Character.Character_Settings cameraSettings;
        private Vector3? dragOrigin;
        private float panHeldSeconds;
        private bool quiet;
        private bool sawEnd;
        private IReadOnlyList<(int owner, float resources, int units, int buildings)> timeline = Array.Empty<(int, float, int, int)>();

        public float Speed = 1f;
        public bool Playing = true;
        public float Seconds => cursor != null ? cursor.Seconds : 0f;
        public float DurationSeconds => cursor != null ? cursor.DurationSeconds : 0f;
        /// <summary>
        /// True when playback reached the end of the file without an <c>End</c> record: the recording was cut
        /// short (size cap, full writer queue or a crash). Playback still stops there.
        /// </summary>
        public bool Incomplete => cursor != null && cursor.AtEnd && !sawEnd;
        public ReplayHeader Header => cursor?.Header;
        /// <summary>v0.18: the match's graphs and timeline, when the file has them.</summary>
        public SummaryData? Summary => cursor?.Summary;
        /// <summary>v0.18: the per-player view can be offered.</summary>
        public bool HasFog => cursor != null && cursor.HasFog && playerView != null;
        /// <summary>v0.18: which player's eyes are shown, or 0 for everything.</summary>
        public int Viewer => playerView != null ? playerView.Viewer : 0;

        private ReplayView playerView;
        private readonly ClientBuildings shownBuildings = new ClientBuildings();
        private bool fogDirty;

        /// <summary>Shows the replay through a player's eyes, or everything for 0 (v0.18, Figma R1).</summary>
        public void SetViewer(int owner)
        {
            if (playerView == null || !HasFog) return;
            playerView.Viewer = owner;
            ApplyViewer();
        }

        private void ApplyViewer()
        {
            if (playerView == null || view == null) return;
            int viewer = playerView.Viewer;
            ReplayHeader h = cursor.Header;
            view.UnitShown = viewer == 0 ? (Func<int, float2, bool>)null : (owner, at) => playerView.ShowsUnit(viewer, owner, at);
            view.BuildingsShown = viewer == 0 ? null : shownBuildings;
            if (viewer != 0) playerView.BuildingsFor(viewer, ClientBuildings.Current, shownBuildings);
            if (viewer != 0) ClientFog.ShowReplayGrid(playerView.GridOf(viewer), h.FogW, h.FogH, h.FogCellSize);
            else
            {
                // Everything: an all-clear grid, in case a player's fog was showing.
                if (h.FogW <= 0 || h.FogH <= 0) return;
                var clear = new byte[h.FogW * h.FogH];
                Array.Fill(clear, (byte)FogState.Visible);
                ClientFog.ShowReplayGrid(clear, h.FogW, h.FogH, h.FogCellSize);
            }
            fogDirty = false;
        }

        /// <summary>Moves the camera to a place on the map (a seek-bar marker).</summary>
        public void CentreOn(float2 at)
        {
            if (replayCamera != null) replayCamera.transform.position = new Vector3(at.x, at.y, replayCamera.transform.position.z);
        }
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
            Character.Character_Settings cameraSettings = Resources.Load<Character.Character_Settings>("Character_Settings");
            if (cameraSettings == null)
            {
                error = "camera settings are missing";
                cursor.Dispose();
                return null;
            }
            MenuBattle.Instance?.TearDown();
            var player = new GameObject("ReplayPlayer").AddComponent<ReplayPlayer>();
            player.cursor = cursor;
            player.cameraSettings = cameraSettings;
            // An untrusted file's fog size must fit its map, or it simply offers no per-player view.
            bool fogFits = h.FogCellSize > 0 && h.FogW >= 1 && h.FogH >= 1 && h.FogW <= h.MapSize && h.FogH <= h.MapSize;
            if (cursor.HasFog && fogFits) player.playerView = new ReplayView(h.FogW, h.FogH, h.FogCellSize);
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
            ClientTerrain.Current.Changed += OnClientTerrainChanged;
            ReplayHeader header = cursor.Header;
            view = new GameObject("ReplayView").AddComponent<ClientWorld>();
            view.ClockDriven = true;
            view.ColourIndexOf = owner =>
            {
                int i = header.Players.FindIndex(p => p.OwnerId == owner);
                return i >= 0 ? header.Players[i].Colour : 0;
            };
            Camera cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            replayCamera = cam;
            view.ViewCamera = cam;
            if (cam != null)
            {
                cam.orthographic = true;
                cam.orthographicSize = 26f;
                cam.transform.position = new Vector3(map.Grid.Width / 2f, map.Grid.Height / 2f, cam.transform.position.z);
                // Open on a base rather than whatever is in the map's middle (usually empty ground).
                if (ReplayOpening.TryFirstHq(cursor != null ? cursor.Summary : null, out float2 hq)) CentreOn(hq);
            }
        }

        private void OnClientTerrainChanged(IReadOnlyList<int2> tiles)
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
            UpdateCamera();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f) * Speed; // a hitch slows playback rather than skipping it
            if (Playing)
            {
                cursor.AdvanceTo(cursor.Seconds + dt, Apply);
                view.AdvanceClock(dt);
                if (Playing && cursor.AtEnd) StopAtEndOfFile();
            }
        }

        /// <summary>The share of the view's half height the replay's control bar can cover.</summary>
        private const float ControlBarShare = 0.35f;

        private void UpdateCamera()
        {
            Camera cam = replayCamera;
            if (cam == null || cameraSettings == null || map == null) return;

            Vector3 next = cam.transform.position;
            float scroll = GameInput.Zoom.ReadValue<float>();
            bool pointerOverUI = GameInput.PointerOverUI;
            if (scroll != 0f && !pointerOverUI)
            {
                Vector2 pointer = GameInput.Point.ReadValue<Vector2>();
                Vector3 before = cam.ScreenToWorldPoint(new Vector3(pointer.x, pointer.y, -cam.transform.position.z));
                cam.orthographicSize = CameraZoom.Step(cam.orthographicSize, scroll, WAR2D.Art.ArtStyle.PixelsPerUnit,
                    Screen.height, cameraSettings.zoomScale.x, cameraSettings.zoomScale.y);
                Vector3 after = cam.ScreenToWorldPoint(new Vector3(pointer.x, pointer.y, -cam.transform.position.z));
                next += new Vector3(before.x - after.x, before.y - after.y, 0f);
            }

            Vector2 panInput = GameInput.Pan.ReadValue<Vector2>();
            panHeldSeconds = panInput.sqrMagnitude > 0f ? panHeldSeconds + Time.unscaledDeltaTime : 0f;
            Vector2 pan = Vector2.ClampMagnitude(panInput, 1f);
            float multiplier = GameInput.FastPan.IsPressed() ? cameraSettings.shiftSpeedMultiplyer : 1f;
            float acceleration = Mathf.Lerp(1f, 4f, Mathf.Clamp01(panHeldSeconds / 1.25f));
            next += (Vector3)(pan * cameraSettings.speed * multiplier * acceleration * (cam.orthographicSize / 5f) * Time.unscaledDeltaTime);

            if (GameInput.DragPan.WasPressedThisFrame() && !pointerOverUI)
                dragOrigin = PointerWorld(cam);
            else if (!GameInput.DragPan.IsPressed())
                dragOrigin = null;
            if (dragOrigin.HasValue)
            {
                Vector3 now = PointerWorld(cam);
                next += new Vector3(dragOrigin.Value.x - now.x, dragOrigin.Value.y - now.y, 0f);
            }

            next.x = Mathf.Clamp(next.x, 0f, map.Grid.Width);
            // The control bar covers the bottom of the view: the camera may go far enough below the map to show that edge above it.
            next.y = Mathf.Clamp(next.y, -cam.orthographicSize * ControlBarShare, map.Grid.Height);
            cam.transform.position = next;
        }

        private static Vector3 PointerWorld(Camera cam)
        {
            Vector2 pointer = GameInput.Point.ReadValue<Vector2>();
            Vector3 world = cam.ScreenToWorldPoint(new Vector3(pointer.x, pointer.y, -cam.transform.position.z));
            world.z = 0f;
            return world;
        }

        /// <summary>A recording without its End record (cut short) stops at its last record instead of playing on forever.</summary>
        private void StopAtEndOfFile()
        {
            Playing = false;
            if (!sawEnd) WarLog.Warn(LogCategory.Replay, $"the recording ends at {cursor.Seconds:0.0} s without an End record (cut short)");
        }

        private void LateUpdate()
        {
            if (fogDirty && Viewer != 0) ApplyViewer();
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
                case ReplayChannel.TeamFog:
                    if (playerView != null) { playerView.Fog(payload, ClientBuildings.Current); fogDirty = true; }
                    break;
                case ReplayChannel.Explosions:
                case ReplayChannel.Blasts:
                case ReplayChannel.BomberBlasts:
                    if (quiet) break;
                    for (int i = 0; i + 8 <= payload.Count; i += 8)
                    {
                        var p = new Vector2(BitConverter.ToSingle(payload.Array, payload.Offset + i), BitConverter.ToSingle(payload.Array, payload.Offset + i + 4));
                        if (Viewer != 0 && !playerView.Sees(Viewer, new float2(p.x, p.y))) continue; // only what that player's team saw
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
                case ReplayChannel.End: Playing = false; sawEnd = true; break;
            }
        }

        /// <summary>Jumps to a time. Going back restarts the recording and fast-forwards (no effects).</summary>
        public void Seek(float seconds)
        {
            if (cursor == null) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            seconds = Mathf.Clamp(seconds, 0f, cursor.DurationSeconds);
            if (seconds < cursor.Seconds)
            {
                Reset();
                cursor.Restart();
                sawEnd = false;
                if (playerView != null)
                {
                    int viewer = playerView.Viewer;
                    playerView = new ReplayView(cursor.Header.FogW, cursor.Header.FogH, cursor.Header.FogCellSize) { Viewer = viewer };
                }
            }
            quiet = true;
            cursor.AdvanceTo(seconds, Apply);
            quiet = false;
            view.AdvanceClock(0f);
            ApplyViewer();
            WarLog.Info(LogCategory.Replay, $"seek to {seconds:0.0} s took {watch.ElapsedMilliseconds} ms");
        }

        private void Reset()
        {
            // At once: ClientWorld.OnDestroy clears the client stores, and a second ClientWorld refuses to start while the first exists.
            if (view != null) DestroyImmediate(view.gameObject);
            ClientTerrain.Current.Changed -= OnClientTerrainChanged;
            ClientBuildings.Clear();
            ClientTerrain.Clear();
            ClientTerrain.Current.Init(map.Grid);
            ClientTerrain.Current.Changed += OnClientTerrainChanged;
            ReplayHeader header = cursor.Header;
            view = new GameObject("ReplayView").AddComponent<ClientWorld>();
            view.ClockDriven = true;
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
            replayCamera = null;
            cameraSettings = null;
            dragOrigin = null;
            panHeldSeconds = 0f;
            ClientTerrain.Current.Changed -= OnClientTerrainChanged;
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
