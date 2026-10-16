// DebugOverlays.cs
using System.Collections.Generic;
using System.Linq;
using Mirror;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using WAR2D.Client.Render;
using WAR2D.Dev.DebugApi;
using WAR2D.Sim;

namespace WAR2D.Dev.Overlays
{
    /// <summary>
    /// Development-build overlays over the map: one point-filtered sprite per grid layer and one line mesh. Server
    /// layers draw only in the host process. Toggled from the API (<c>overlay</c>), the console, or F1–F9.
    /// With every layer off it does nothing.
    /// </summary>
    public sealed class DebugOverlays : MonoBehaviour
    {
        public static DebugOverlays Instance { get; private set; }

        private readonly List<OverlayLayer> layers = new List<OverlayLayer>
        {
            new FootprintsLayer(), new FogLayer(), new HashLayer(),
            new FlowLayer(), new RoutesLayer(), new TargetsLayer(), new SectorsLayer(), new InterestLayer(), new TerrainChangesLayer(),
        };
        private readonly HashSet<OverlayLayer> on = new HashSet<OverlayLayer>();
        private readonly Dictionary<OverlayLayer, (SpriteRenderer sprite, Texture2D texture, OverlayCanvas canvas)> views = new Dictionary<OverlayLayer, (SpriteRenderer, Texture2D, OverlayCanvas)>();
        private Mesh lineMesh;
        private Material lineMaterial;
        private SimContext attached;

        public IEnumerable<string> Enabled => on.Select(l => l.Name);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!DevUi.Enabled) return;
            var go = new GameObject("DebugOverlays");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<DebugOverlays>();
            ViewCommands.OverlayHook = (name, enable, args) => Instance.Set(name, enable, args);
        }

        public object Set(string name, bool enable, JObject args)
        {
            OverlayLayer layer = layers.FirstOrDefault(l => string.Equals(l.Name, name, System.StringComparison.OrdinalIgnoreCase))
                                 ?? throw new DebugError($"no layer '{name}' (layers: {string.Join(", ", layers.Select(l => l.Name))})");
            if (enable && layer.ServerOnly && !NetworkServer.active) throw new DebugError($"'{layer.Name}' reads server data: use the host process");
            if (args != null) layer.Args = args;
            if (enable && on.Add(layer)) SettledSnapshot.Wanted++;
            if (!enable && on.Remove(layer)) { SettledSnapshot.Wanted--; Hide(layer); }
            return Enabled.ToArray();
        }

        private void Update()
        {
            Keyboard k = Keyboard.current;
            if (k != null)
                foreach (OverlayLayer l in layers)
                    if (KeyFor(l.Hotkey) is Key key && k[key].wasPressedThisFrame)
                        try { Set(l.Name, !on.Contains(l), null); } catch (DebugError e) { Logging.WarLog.Warn(Logging.LogCategory.Dev, e.Message); }

            if (on.Count == 0) { Detach(); return; }
            if (SimContext.Current != attached) { Detach(); attached = SimContext.Current; if (attached != null) attached.Settled += Capture; }
        }

        private void Capture(SimData data, int tick, int count)
        {
            foreach (OverlayLayer l in on) l.OnSettled(data, tick, count);
        }

        private void Detach()
        {
            if (attached != null) attached.Settled -= Capture;
            attached = null;
        }

        private void LateUpdate()
        {
            if (on.Count == 0) return;
            var lines = new List<(float2, float2, Color32)>();
            foreach (OverlayLayer l in on)
            {
                if (!views.TryGetValue(l, out var view)) view = views[l] = (NewSprite(l.Name), null, new OverlayCanvas());
                view.canvas.Lines.Clear();
                l.Draw(view.canvas);
                lines.AddRange(view.canvas.Lines);
                if (view.canvas.Texels == null || view.canvas.GridWidth == 0) { view.sprite.enabled = false; continue; }
                if (view.texture == null || view.texture.width != view.canvas.GridWidth || view.texture.height != view.canvas.GridHeight)
                {
                    view.texture = new Texture2D(view.canvas.GridWidth, view.canvas.GridHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                    view.sprite.sprite = Sprite.Create(view.texture, new Rect(0, 0, view.texture.width, view.texture.height), Vector2.zero, 1f / view.canvas.CellSize);
                    views[l] = view;
                }
                view.texture.SetPixels32(view.canvas.Texels);
                view.texture.Apply(false);
                view.sprite.enabled = true;
            }
            DrawLines(lines);
        }

        private SpriteRenderer NewSprite(string name)
        {
            var go = new GameObject("Overlay " + name);
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = SortingOrders.DebugOverlay;
            return sr;
        }

        private void Hide(OverlayLayer l)
        {
            if (views.TryGetValue(l, out var v) && v.sprite != null) v.sprite.enabled = false;
        }

        private void DrawLines(List<(float2 a, float2 b, Color32 c)> lines)
        {
            if (lines.Count == 0) return;
            if (lineMesh == null) { lineMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 }; lineMaterial = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")); }
            var v = new Vector3[lines.Count * 2];
            var c = new Color32[lines.Count * 2];
            var idx = new int[lines.Count * 2];
            for (int i = 0; i < lines.Count; i++)
            {
                v[2 * i] = new Vector3(lines[i].a.x, lines[i].a.y, 0f); v[2 * i + 1] = new Vector3(lines[i].b.x, lines[i].b.y, 0f);
                c[2 * i] = c[2 * i + 1] = lines[i].c; idx[2 * i] = 2 * i; idx[2 * i + 1] = 2 * i + 1;
            }
            lineMesh.Clear();
            lineMesh.vertices = v; lineMesh.colors32 = c; lineMesh.SetIndices(idx, MeshTopology.Lines, 0);
            Graphics.RenderMesh(new RenderParams(lineMaterial) { layer = 0 }, lineMesh, 0, Matrix4x4.identity);
        }

        private static Key? KeyFor(KeyCode code) => code switch
        {
            KeyCode.F1 => Key.F1, KeyCode.F2 => Key.F2, KeyCode.F3 => Key.F3, KeyCode.F4 => Key.F4, KeyCode.F5 => Key.F5,
            KeyCode.F6 => Key.F6, KeyCode.F7 => Key.F7, KeyCode.F8 => Key.F8, KeyCode.F9 => Key.F9, _ => null,
        };

        private void OnDestroy() { Detach(); if (Instance == this) Instance = null; }
    }
}
