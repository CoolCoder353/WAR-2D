using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WAR2D.Spike
{
    /// <summary>
    /// Runs one spike benchmark when <c>ScaleSpike</c> is the active scene. The scene is empty:
    /// everything a benchmark needs is built from code at runtime. The player is launched with
    /// <c>-spike &lt;name&gt;</c> and the options in <see cref="SpikeArgs"/>; in the editor the
    /// <see cref="editorArgs"/> field is used instead.
    /// </summary>
    public sealed class SpikeDriver : MonoBehaviour
    {
        /// <summary>Name of the scene this driver bootstraps in.</summary>
        public const string SceneName = "ScaleSpike";

        /// <summary>
        /// Benchmark name (the <c>-spike</c> argument) to its runner. A benchmark file registers
        /// itself from a <c>[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]</c> method, which
        /// runs before the scene loads, e.g. <c>Benches["sim"] = SimBench.Run;</c>.
        /// </summary>
        public static readonly Dictionary<string, Func<SpikeArgs, IEnumerator>> Benches =
            new Dictionary<string, Func<SpikeArgs, IEnumerator>>();

        /// <summary>The driver of the running spike scene, if there is one.</summary>
        public static SpikeDriver Instance { get; private set; }

        /// <summary>Editor-only options; the <c>-spike</c> player parses its command line instead.</summary>
        [SerializeField] private SpikeArgs editorArgs = SpikeArgs.Defaults;

        /// <summary>The 20 Hz clock every tick benchmark drives its world from.</summary>
        public SpikeClock Clock { get; private set; }

        /// <summary>The camera the rendering benchmarks draw through, null for the others.</summary>
        public Camera BenchCamera { get; private set; }

        private SpikeArgs args;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (SceneManager.GetActiveScene().name != SceneName) return;
            // A driver authored into the scene wins, so its inspector options can be used.
            if (FindFirstObjectByType<SpikeDriver>() != null) return;
            new GameObject("[Spike] Driver").AddComponent<SpikeDriver>();
        }

        private void Awake()
        {
            Instance = this;
            args = Application.isEditor ? editorArgs : SpikeArgs.Parse(Environment.GetCommandLineArgs());

            if (Application.isBatchMode)
            {
                Application.targetFrameRate = -1;
                QualitySettings.vSyncCount = 0;
            }

            Clock = gameObject.AddComponent<SpikeClock>();
            if (WantsCamera(args.Bench)) CreateCamera();

            Debug.Log($"[Spike] start bench={args.Bench} units={args.Units} map={args.MapSize} teams={args.Teams} " +
                      $"seed={args.Seed} ticks={args.Ticks} warmup={args.Warmup} vision={args.VisionRadius} " +
                      $"bvision={args.BuildingVision} large={args.LargePercent} slice={args.SliceTicks} " +
                      $"cell={args.CellSize} clients={args.Clients} async={args.Async} clump={args.Clump} " +
                      $"out={args.OutDir} tag={args.Tag} quit={args.Quit}");
            StartCoroutine(RunBench());
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private IEnumerator RunBench()
        {
            if (Benches.TryGetValue(args.Bench, out Func<SpikeArgs, IEnumerator> run))
            {
                // Nested coroutine: a benchmark that throws stops only itself, so this still logs
                // and quits instead of leaving a batch-mode player running forever.
                yield return StartCoroutine(run(args));
            }
            else
            {
                Debug.LogError($"[Spike] Unknown bench '{args.Bench}'. Registered: {string.Join(", ", Benches.Keys)}");
            }

            Debug.Log($"[Spike] done {args.Bench}");
            if (args.Quit) Application.Quit();
        }

        /// <summary>The rendering benchmarks are the only ones that draw anything.</summary>
        private static bool WantsCamera(string bench) =>
            bench.StartsWith("render", StringComparison.Ordinal) ||
            bench.StartsWith("combined", StringComparison.Ordinal);

        private void CreateCamera()
        {
            var go = new GameObject("[Spike] Camera") { tag = "MainCamera" };
            go.transform.position = new Vector3(args.MapSize * 0.5f, args.MapSize * 0.5f, -10f);
            BenchCamera = go.AddComponent<Camera>();
            BenchCamera.orthographic = true;
            BenchCamera.orthographicSize = args.MapSize * 0.5f;
            BenchCamera.clearFlags = CameraClearFlags.SolidColor;
            BenchCamera.backgroundColor = new Color(0.04f, 0.04f, 0.05f);
            BenchCamera.nearClipPlane = 0.1f;
            BenchCamera.farClipPlane = 100f;
        }
    }
}
