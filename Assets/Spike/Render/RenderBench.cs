using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>render</c> and <c>render-baseline</c> benches: how many client frames a 1920x1080
    /// window holds while it draws 10k own units (plus 10k visible enemies in the <c>mixed</c> scene),
    /// against today's GameObject-per-unit baseline.
    ///
    /// <para><b>What the measured section contains:</b> per frame, the client path following
    /// (<see cref="PathFollowerJob"/>), the instance packing (instanced only), the buffer upload and
    /// the draws - three instanced draws for the instanced scene (map backdrop, units, health bars),
    /// or one <c>transform.position</c> write per unit for the baseline. The run is windowed at
    /// 1920x1080 with vsync off. The protocol is 300 warm-up frames (which also pay for shader, Burst
    /// and pipeline compilation) then 1,800 sampled frames.</para>
    ///
    /// <para><b>Evidence:</b> each scene captures two frames - the map backdrop alone before the units
    /// start drawing, and a frame at the end of the run - saves both PNGs to the out directory and
    /// reports the share of pixels the units changed. A black frame at 300 fps fails this check
    /// instead of passing silently.</para>
    /// </summary>
    public static class RenderBench
    {
        /// <summary>Sprite world size of a small unit, the shader's <c>_UnitSize</c>; large units double it.</summary>
        public const float UnitSize = 0.8f;

        /// <summary>Player tints the scene cycles through.</summary>
        public const int Teams = 8;

        /// <summary>Share of the units that are damaged and therefore draw a health bar.</summary>
        public const int DamagedPercent = 20;

        /// <summary>Waypoints per unit's route.</summary>
        public const int WaypointsPerUnit = 6;

        /// <summary>Health-bar atlas frames (length steps).</summary>
        public const int BarFrames = 11;

        /// <summary>The plan's own10k cluster: 10k units in about 110 x 110 tiles.</summary>
        internal const float ClusterTilesAt10k = 110f;

        /// <summary>Units per worker slice in the per-frame jobs.</summary>
        private const int JobBatch = 128;

        /// <summary>Frames of frame-timing lag dropped at the front of the sample window.</summary>
        private const int FrameTimingSkip = 4;

        /// <summary>Channel difference above which a pixel counts as changed by the units.</summary>
        private const int ChangedThreshold = 8, StrongThreshold = 32;

        /// <summary>The driver's camera clear colour, and the colour the backdrop must cover.</summary>
        private static readonly Color ClearColor = new Color(0.04f, 0.04f, 0.05f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            SpikeDriver.Benches["render"] = Run;
            SpikeDriver.Benches["render-baseline"] = Run;
        }

        public static IEnumerator Run(SpikeArgs a)
        {
            Camera camera = SpikeDriver.Instance != null ? SpikeDriver.Instance.BenchCamera : null;
            if (camera == null)
            {
                Debug.LogError("[Render] the spike scene has no bench camera; nothing can be drawn");
                yield break;
            }
            bool instanced = a.Bench != "render-baseline";
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true; // an unfocused window must keep rendering
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = ClearColor;

            var scenes = new List<string>();
            if (string.IsNullOrEmpty(a.RenderScene) || a.RenderScene == "own10k" || a.RenderScene == "both") scenes.Add("own10k");
            if (string.IsNullOrEmpty(a.RenderScene) || a.RenderScene == "mixed" || a.RenderScene == "both") scenes.Add("mixed");
            if (scenes.Count == 0)
            {
                Debug.LogError($"[Render] unknown -scene '{a.RenderScene}'; expected own10k, mixed or both");
                yield break;
            }

            SpikeMap map = MapGenerator.Generate(a.MapSize, (uint)a.Seed, Allocator.Persistent);
            Backdrop backdrop = null;
            Texture2D barAtlas = null;
            try
            {
                backdrop = new Backdrop(map);
                Texture2D atlas = Resources.Load<Texture2D>("tank");
                if (atlas == null)
                {
                    Debug.LogError("[Render] Resources/tank.png is missing from the build");
                    yield break;
                }
                atlas.filterMode = FilterMode.Point;
                barAtlas = BuildBarAtlas(BarFrames);

                foreach (string scene in scenes)
                {
                    yield return RunScene(a, camera, backdrop, scene, instanced, atlas, barAtlas);
                }
            }
            finally
            {
                backdrop?.Dispose();
                if (barAtlas != null) UnityEngine.Object.Destroy(barAtlas);
                map.Dispose();
            }
        }

        /// <summary>One scene: build the units, warm up, sample, take the evidence frame.</summary>
        private static IEnumerator RunScene(SpikeArgs a, Camera camera, Backdrop backdrop, string scene,
            bool instanced, Texture2D atlas, Texture2D barAtlas)
        {
            int own = math.max(1, a.RenderUnits);
            int total = scene == "mixed" ? own * 2 : own;
            var centre = new float2(a.MapSize * 0.5f, a.MapSize * 0.5f);
            SpikeArgs args = a;
            args.Units = total;
            args.Tag = string.IsNullOrEmpty(a.Tag) ? scene : a.Tag + "-" + scene;

            using SceneData data = SceneData.Build(total, a.LargePercent, (uint)a.Seed + 7u, centre);
            camera.transform.position = new Vector3(centre.x, centre.y, -10f);
            camera.orthographicSize = data.Side * 0.5f;
            Debug.Log($"[Render] {scene} ({(instanced ? "instanced" : "baseline")}): {data.Units} units " +
                      $"({data.LargeUnits} large), {data.DamagedCount} damaged, {data.Side:F0} tile cluster, " +
                      $"camera half-height {camera.orthographicSize:F1}, {a.Frames} sampled frames after {a.FrameWarmup} warm-up");

            InstancedUnitRenderer renderer = null;
            SpriteBaseline baseline = null;
            if (instanced)
            {
                renderer = new InstancedUnitRenderer(data.Units, data.DamagedCount, atlas, barAtlas, BarFrames, UnitSize);
            }
            else
            {
                float start = Time.realtimeSinceStartup;
                baseline = SpriteBaseline.Create(data, atlas, UnitSize);
                baseline.SetVisible(false);
                Debug.Log($"[Render] baseline built {baseline.UnitObjects + baseline.BarObjects} GameObjects in " +
                          $"{Time.realtimeSinceStartup - start:F1} s");
            }

            try
            {
                // Backdrop-only frame, the reference the evidence frame is compared against.
                Tick(data, renderer, baseline, backdrop, instanced, units: false);
                var reference = new Shot();
                yield return Take(reference, args, "bg");
                double backdropShare = ShareDifferent(reference.Pixels, ClearColor);
                yield return null;

                baseline?.SetVisible(true);
                var frameMs = new SpikeStats();
                var fps = new SpikeStats();
                var cpuMain = new SpikeStats();
                var gpu = new SpikeStats();
                var clientMs = new SpikeStats();
                var frameTimings = new FrameTiming[8];
                int sampled = a.Frames + FrameTimingSkip;
                double lastFrame = Time.realtimeSinceStartupAsDouble;

                // Frame timings lag the frame that produced them, so the high-water mark advances on
                // every frame and only timings produced inside the sample window are recorded.
                ulong lastTiming = 0;
                for (int i = 0; i < a.FrameWarmup + sampled; i++)
                {
                    bool recording = i >= a.FrameWarmup + FrameTimingSkip;
                    Tick(data, renderer, baseline, backdrop, instanced, units: true, recording ? clientMs : null);
                    yield return null;

                    double now = Time.realtimeSinceStartupAsDouble;
                    double milliseconds = (now - lastFrame) * 1000.0;
                    lastFrame = now;
                    recording &= milliseconds > 0.0;
                    lastTiming = Drain(lastTiming, frameTimings, recording ? cpuMain : null, recording ? gpu : null);
                    if (!recording) continue;
                    frameMs.Add(milliseconds);
                    fps.Add(1000.0 / milliseconds);
                }
                Debug.Log($"[Render] FrameTimingManager: feature enabled {FrameTimingManager.IsFeatureEnabled()}, " +
                          $"cpu timer {FrameTimingManager.GetCpuTimerFrequency()} Hz, " +
                          $"gpu timer {FrameTimingManager.GetGpuTimerFrequency()} Hz, {cpuMain.Count} cpu / {gpu.Count} gpu timings");

                // Evidence frame: the units are on screen and have just moved.
                Tick(data, renderer, baseline, backdrop, instanced, units: true);
                var evidence = new Shot();
                yield return Take(evidence, args, "shot");
                Coverage(reference.Pixels, evidence.Pixels, out double changed, out double strong, out double mean);

                Report(args, data, instanced, baseline, frameMs, fps, cpuMain, gpu, clientMs, changed, strong, mean, backdropShare);
                yield return null;
            }
            finally
            {
                renderer?.Dispose();
                baseline?.Dispose();
            }
        }

        /// <summary>
        /// Records every frame timing the manager has produced since <paramref name="last"/>, keyed by
        /// the monotonic present timestamp so a timing is never counted twice, and returns the newest.
        /// Pass null stats to only advance the high-water mark.
        /// </summary>
        private static ulong Drain(ulong last, FrameTiming[] timings, SpikeStats cpuMain, SpikeStats gpu)
        {
            FrameTimingManager.CaptureFrameTimings();
            uint got = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            for (int t = 0; t < got; t++)
            {
                ulong stamp = timings[t].cpuTimePresentCalled;
                if (stamp == 0 || stamp <= last) continue;
                last = stamp;
                cpuMain?.Add(timings[t].cpuMainThreadFrameTime);
                if (gpu != null && timings[t].gpuFrameTime > 0.0) gpu.Add(timings[t].gpuFrameTime);
            }
            return last;
        }

        /// <summary>
        /// One frame of client work: path following, packing, upload, draws. <paramref name="clientMs"/>
        /// collects the main-thread wall time of that work, the number a client budget cares about when
        /// the frame timings themselves are not available.
        /// </summary>
        private static void Tick(SceneData data, InstancedUnitRenderer renderer, SpriteBaseline baseline,
            Backdrop backdrop, bool instanced, bool units, SpikeStats clientMs = null)
        {
            long started = clientMs != null ? Stopwatch.GetTimestamp() : 0L;
            backdrop.Draw();
            if (!units)
            {
                if (clientMs != null) clientMs.Add(Elapsed(started));
                return;
            }

            JobHandle follow = new PathFollowerJob
            {
                Waypoints = data.Waypoints,
                WaypointStart = data.WaypointStart,
                Speed = data.Speed,
                StartTime = data.StartTime,
                Now = (float)Time.timeAsDouble,
                Positions = data.Positions,
                Rotations = data.Rotations,
            }.Schedule(data.Units, JobBatch);

            if (instanced)
            {
                JobHandle pack = new PackUnitsJob
                {
                    Positions = data.Positions,
                    Rotations = data.Rotations,
                    Colors = data.Colors,
                    Scale = data.Scale,
                    Health = data.Health,
                    Instances = data.Instances,
                }.Schedule(data.Units, JobBatch, follow);
                JobHandle bars = new PackHealthBarsJob
                {
                    Positions = data.Positions,
                    Health = data.Health,
                    Damaged = data.Damaged,
                    Scale = data.Scale,
                    BarFrames = BarFrames,
                    UnitHalfSize = UnitSize * 0.5f,
                    Bars = data.Bars,
                    Count = data.BarCount,
                }.Schedule(follow);
                JobHandle.CompleteAll(ref pack, ref bars);
                renderer.Draw(data.Instances, data.Units);
                renderer.DrawHealthBars(data.Bars, data.BarCount[0]);
            }
            else
            {
                follow.Complete();
                baseline.SetPositions(data.Positions);
            }
            if (clientMs != null) clientMs.Add(Elapsed(started));
        }

        /// <summary>Milliseconds since a <see cref="Stopwatch.GetTimestamp"/> reading.</summary>
        private static double Elapsed(long started) =>
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Writes the scene's metric rows and a human-readable summary.</summary>
        private static void Report(SpikeArgs args, SceneData data, bool instanced, SpriteBaseline baseline,
            SpikeStats frameMs, SpikeStats fps, SpikeStats cpuMain, SpikeStats gpu, SpikeStats clientMs,
            double changed, double strong, double mean, double backdropShare)
        {
            double p99 = frameMs.Percentile(99);
            SpikeResults.Write(args, "render.fps", "fps", fps);
            SpikeResults.Write(args, "render.frame", "ms", frameMs);
            SpikeResults.Write(args, "render.frame.p99", "ms", Single(p99));
            if (cpuMain.Count > 0) SpikeResults.Write(args, "render.cpu.main", "ms", cpuMain);
            else Debug.LogWarning("[Render] FrameTimingManager returned no CPU frame timings");
            if (gpu.Count > 0) SpikeResults.Write(args, "render.gpu", "ms", gpu);
            else Debug.LogWarning("[Render] FrameTimingManager returned no GPU frame timings (driver does not expose them)");
            SpikeResults.Write(args, "render.cpu.client", "ms", clientMs);

            SpikeResults.Write(args, "render.units", "units", Single(data.Units));
            SpikeResults.Write(args, "render.damaged", "units", Single(data.DamagedCount));
            SpikeResults.Write(args, "render.healthbars", "bars", Single(data.DamagedCount));
            SpikeResults.Write(args, "render.coverage", "share", Single(changed));
            SpikeResults.Write(args, "render.coverage.strong", "share", Single(strong));
            SpikeResults.Write(args, "render.backdrop", "share", Single(backdropShare));
            SpikeResults.Write(args, "render.width", "px", Single(Screen.width));
            SpikeResults.Write(args, "render.height", "px", Single(Screen.height));
            if (instanced) SpikeResults.Write(args, "render.draws", "draws", Single(InstancedUnitRenderer.DrawsPerFrame + 1));
            else SpikeResults.Write(args, "render.objects", "objects", Single(baseline.UnitObjects + baseline.BarObjects));

            Debug.Log($"[Render] {args.Tag}: {fps.Mean:F1} fps, frame p50 {frameMs.Percentile(50):F2} " +
                      $"p99 {p99:F2} max {frameMs.Max:F2} ms, cpu.main p50 {(cpuMain.Count > 0 ? cpuMain.Percentile(50) : double.NaN):F2} ms, " +
                      $"gpu p50 {(gpu.Count > 0 ? gpu.Percentile(50) : double.NaN):F2} ms, client p50 {clientMs.Percentile(50):F3} ms, " +
                      $"coverage {changed:P1} (strong {strong:P1}, mean {mean:P3}), backdrop {backdropShare:P1}, " +
                      $"{Screen.width}x{Screen.height}");
        }

        /// <summary>Waits for the frame to finish rendering, then reads the back buffer and saves it as a PNG.</summary>
        private static IEnumerator Take(Shot into, SpikeArgs args, string suffix)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            if (shot == null || shot.width == 0)
            {
                Debug.LogError("[Render] the screenshot capture returned nothing");
                yield break;
            }
            into.Width = shot.width;
            into.Height = shot.height;
            into.Pixels = shot.GetPixels32();
            Directory.CreateDirectory(args.OutDir);
            File.WriteAllBytes(Path.Combine(args.OutDir, $"{args.Tag}-{suffix}.png"), shot.EncodeToPNG());
            UnityEngine.Object.Destroy(shot);
        }

        /// <summary>The share of pixels the units changed, against the backdrop-only frame.</summary>
        private static void Coverage(Color32[] reference, Color32[] shot, out double changed, out double strong, out double mean)
        {
            changed = strong = mean = 0.0;
            int count = math.min(reference.Length, shot.Length);
            if (count == 0) return;
            long any = 0, big = 0;
            double sum = 0.0;
            for (int i = 0; i < count; i++)
            {
                int dr = Math.Abs(shot[i].r - reference[i].r);
                int dg = Math.Abs(shot[i].g - reference[i].g);
                int db = Math.Abs(shot[i].b - reference[i].b);
                int max = Math.Max(dr, Math.Max(dg, db));
                if (max > ChangedThreshold) any++;
                if (max > StrongThreshold) big++;
                sum += (dr + dg + db) / 3.0;
            }
            changed = (double)any / count;
            strong = (double)big / count;
            mean = sum / count / 255.0;
        }

        /// <summary>Share of pixels that are not the camera's clear colour, i.e. the backdrop drew.</summary>
        private static double ShareDifferent(Color32[] pixels, Color clear)
        {
            if (pixels.Length == 0) return 0.0;
            var c = (Color32)clear;
            long different = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                int max = Math.Max(Math.Abs(pixels[i].r - c.r),
                    Math.Max(Math.Abs(pixels[i].g - c.g), Math.Abs(pixels[i].b - c.b)));
                if (max > ChangedThreshold) different++;
            }
            return (double)different / pixels.Length;
        }

        /// <summary>The health-bar atlas: frame n is the bar at n / (frames - 1) health, drawn as a white bar in a transparent square.</summary>
        private static Texture2D BuildBarAtlas(int frames)
        {
            const int cell = 32, barHeight = 8;
            var texture = new Texture2D(frames * cell, cell, TextureFormat.RGBA32, false)
            {
                name = "Spike health-bar atlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[frames * cell * cell];
            for (int f = 0; f < frames; f++)
            {
                float health = frames > 1 ? f / (float)(frames - 1) : 1f;
                int width = math.max(1, (int)math.round(health * cell));
                int x0 = (cell - width) / 2;
                for (int y = (cell - barHeight) / 2; y < (cell + barHeight) / 2; y++)
                for (int x = x0; x < x0 + width; x++)
                    pixels[y * texture.width + f * cell + x] = new Color32(255, 255, 255, 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static SpikeStats Single(double value)
        {
            var stats = new SpikeStats();
            stats.Add(value);
            return stats;
        }

        /// <summary>Captured pixels, and the size they were captured at.</summary>
        private sealed class Shot
        {
            public Color32[] Pixels = Array.Empty<Color32>();
            public int Width, Height;
        }

        /// <summary>
        /// The map, baked into one point-filtered texture (1 texel per tile) and drawn as a single
        /// instanced quad with the spike shader. No sprite or URP material is involved, so the
        /// background cannot be the thing that fails to render under the 2D renderer.
        /// </summary>
        private sealed class Backdrop : IDisposable
        {
            private readonly Material material;
            private readonly GraphicsBuffer instances, frameRects;
            private readonly Texture2D texture;

            public Backdrop(in SpikeMap map)
            {
                var pixels = new Color32[map.Width * map.Height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = TileColor(map.Tiles[i]);
                texture = new Texture2D(map.Width, map.Height, TextureFormat.RGBA32, false)
                {
                    name = "Spike map backdrop",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                texture.SetPixels32(pixels);
                texture.Apply(false, false);

                material = new Material(InstancedUnitRenderer.LoadShader())
                {
                    name = "Spike map backdrop",
                    renderQueue = 1999,
                };
                material.SetFloat("_UnitSize", 1f);
                material.SetTexture("_MainTex", texture);
                instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, InstancedUnitRenderer.InstanceStride);
                frameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                frameRects.SetData(new[] { new Vector4(0f, 0f, 1f, 1f) });
                material.SetBuffer("_Instances", instances);
                material.SetBuffer("_FrameRects", frameRects);
                instances.SetData(new[]
                {
                    new UnitInstance
                    {
                        position = new float2(map.Width * 0.5f, map.Height * 0.5f),
                        rotation = 0f,
                        frame = 0,
                        color = uint.MaxValue,
                        health = 1f,
                        scale = map.Width,
                        pad = 0f,
                    },
                });
            }

            public void Draw()
            {
                Graphics.RenderPrimitives(
                    new RenderParams(material) { worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f) },
                    MeshTopology.Triangles, 6, 1);
            }

            private static Color32 TileColor(byte tile) => tile switch
            {
                SpikeMap.Floor => new Color32(33, 33, 38, 255),
                SpikeMap.Rock => new Color32(76, 74, 71, 255),
                SpikeMap.Gem => new Color32(56, 51, 77, 255),
                _ => new Color32(12, 12, 12, 255),
            };

            public void Dispose()
            {
                instances.Dispose();
                frameRects.Dispose();
                UnityEngine.Object.Destroy(material);
                UnityEngine.Object.Destroy(texture);
            }
        }
    }

    /// <summary>
    /// A scene's unit data: static per unit (tint, health, size, route) plus the per-frame outputs.
    /// Both presenters get the same data, so the instanced renderer and the sprite baseline draw
    /// exactly the same army.
    /// </summary>
    internal sealed class SceneData : IDisposable
    {
        public int Units, DamagedCount, LargeUnits;

        /// <summary>Side of the tile cluster the units are spread over.</summary>
        public float Side;

        public NativeArray<float2> Waypoints, Positions;
        public NativeArray<int> WaypointStart;
        public NativeArray<float> Speed, StartTime, Rotations, Health, Scale;
        public NativeArray<uint> Colors;
        public NativeArray<byte> Damaged;
        public NativeArray<UnitInstance> Instances, Bars;
        public NativeArray<int> BarCount;

        /// <summary>Units uniformly spread over a <c>110 * sqrt(n / 10000)</c> tile square, so every scene holds the plan's density.</summary>
        public static SceneData Build(int units, int largePercent, uint seed, float2 centre)
        {
            var rng = new Random(seed == 0 ? 1u : seed);
            float side = math.max(8f, RenderBench.ClusterTilesAt10k * math.sqrt(units / 10000f));
            float2 lo = centre - side * 0.5f, hi = centre + side * 0.5f;
            var data = new SceneData
            {
                Units = units,
                Side = side,
                Waypoints = new NativeArray<float2>(units * RenderBench.WaypointsPerUnit, Allocator.Persistent),
                Positions = new NativeArray<float2>(units, Allocator.Persistent),
                WaypointStart = new NativeArray<int>(units + 1, Allocator.Persistent),
                Speed = new NativeArray<float>(units, Allocator.Persistent),
                StartTime = new NativeArray<float>(units, Allocator.Persistent),
                Rotations = new NativeArray<float>(units, Allocator.Persistent),
                Health = new NativeArray<float>(units, Allocator.Persistent),
                Scale = new NativeArray<float>(units, Allocator.Persistent),
                Colors = new NativeArray<uint>(units, Allocator.Persistent),
                Damaged = new NativeArray<byte>(units, Allocator.Persistent),
                Instances = new NativeArray<UnitInstance>(units, Allocator.Persistent),
                Bars = new NativeArray<UnitInstance>(units, Allocator.Persistent),
                BarCount = new NativeArray<int>(1, Allocator.Persistent),
            };

            int damaged = 0, large = 0;
            for (int i = 0; i < units; i++)
            {
                float2 start = new float2(rng.NextFloat(lo.x, hi.x), rng.NextFloat(lo.y, hi.y));
                data.WaypointStart[i] = i * RenderBench.WaypointsPerUnit;
                data.Waypoints[i * RenderBench.WaypointsPerUnit] = start;
                float2 point = start;
                for (int k = 1; k < RenderBench.WaypointsPerUnit; k++)
                {
                    point = math.clamp(point + rng.NextFloat2Direction() * rng.NextFloat(8f, 25f), lo, hi);
                    data.Waypoints[i * RenderBench.WaypointsPerUnit + k] = point;
                }
                data.Positions[i] = start;
                data.Speed[i] = rng.NextFloat(2f, 4f);
                data.StartTime[i] = -rng.NextFloat(0f, 6f);
                data.Colors[i] = InstancedUnitRenderer.Pack(InstancedUnitRenderer.TeamColors[i % RenderBench.Teams]);
                bool isDamaged = i % (100 / RenderBench.DamagedPercent) == 0;
                data.Damaged[i] = isDamaged ? (byte)1 : (byte)0;
                data.Health[i] = isDamaged ? rng.NextFloat(0.15f, 0.95f) : 1f;
                bool isLarge = rng.NextInt(0, 100) < largePercent;
                data.Scale[i] = isLarge ? 2f : 1f;
                if (isDamaged) damaged++;
                if (isLarge) large++;
            }
            data.WaypointStart[units] = units * RenderBench.WaypointsPerUnit;
            data.DamagedCount = damaged;
            data.LargeUnits = large;
            return data;
        }

        public void Dispose()
        {
            Waypoints.Dispose();
            Positions.Dispose();
            WaypointStart.Dispose();
            Speed.Dispose();
            StartTime.Dispose();
            Rotations.Dispose();
            Health.Dispose();
            Scale.Dispose();
            Colors.Dispose();
            Damaged.Dispose();
            Instances.Dispose();
            Bars.Dispose();
            BarCount.Dispose();
        }
    }
}
