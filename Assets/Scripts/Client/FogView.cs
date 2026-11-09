using WAR2D.Dev.Logging;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WAR2D.Net.Replication;

/// <summary>
/// Draws the local team's fog of war over the map: black where unexplored, dimmed where explored but
/// hidden, clear where seen. One texel per fog cell, filtered and then dithered on the art-pixel grid by the
/// <c>Fog</c> shader, so its edges read as pixel art; only changed texels are rewritten. A changed cell eases to
/// its new level over <see cref="FadeSeconds"/> (polish item H), which the shader's dither turns into a dissolve.
/// The levels the cells ease toward are a texture rewritten only when the fog changes; each frame's easing step (<see cref="Toward"/>
/// for every cell) runs on the GPU into the texture the overlay draws (2026-11-08: a battle keeps cells easing all the time, and
/// uploading the grid every frame cost a 100,000-unit client 1.5 ms of its render thread a frame).
/// </summary>
public class FogView : MonoBehaviour
{
    private static FogView _instance;
    private static readonly Color32[] Palette =
    {
        new Color32(0, 0, 0, 255), // unexplored
        new Color32(0, 0, 0, 140), // explored, hidden
        new Color32(0, 0, 0, 0),   // visible
    };

    private Texture2D _target; // each cell's level, as a byte
    private readonly RenderTexture[] _current = new RenderTexture[2]; // the eased levels, drawn from and stepped into in turn
    private int _front;
    private Material _material, _fade;
    private MaterialPropertyBlock _fadeProperties;
    private CommandBuffer _commands;
    private Mesh _mesh;
    private bool _dirty;
    private readonly List<int> _changed = new List<int>();
    // Steps and seconds of easing since the last change: past either bound every cell is on its level.
    private int _easedFrames = int.MaxValue;
    private float _easedSeconds = float.MaxValue;

    /// <summary>Seconds a cell takes to ease all the way between unexplored and seen (a smaller change takes less).</summary>
    public const float FadeSeconds = 0.25f;

    /// <summary>An alpha moved from <paramref name="current"/> toward <paramref name="target"/> by <paramref name="dt"/> seconds of the fade.</summary>
    public static byte Toward(byte current, byte target, float dt)
    {
        int step = StepOf(dt);
        return current < target ? (byte)Mathf.Min(target, current + step) : (byte)Mathf.Max(target, current - step);
    }

    /// <summary>How many levels of 255 a cell eases in a frame of <paramref name="dt"/> seconds: at least one.</summary>
    private static int StepOf(float dt) => Mathf.Max(1, Mathf.CeilToInt(255f * dt / FadeSeconds));

    /// <summary>Creates the overlay if there is none.</summary>
    public static void Ensure()
    {
        if (_instance == null) _instance = new GameObject("FogView").AddComponent<FogView>();
    }

    private void OnEnable() => ClientFog.Updated += MarkDirty;
    private void OnDisable() => ClientFog.Updated -= MarkDirty;
    private void MarkDirty() => _dirty = true;

    private void LateUpdate()
    {
        ClientFog fog = ClientFog.Current;
        if (fog.State == null) return;
        bool snap = false;
        if (_dirty)
        {
            _dirty = false;
            _changed.Clear();
            bool all = fog.TakeChanges(_changed);
            if (_target == null || _target.width != fog.Width || _target.height != fog.Height)
            {
                Build(fog);
                all = true;
            }
            var levels = _target.GetPixelData<byte>(0);
            if (all)
            {
                // A new grid (the match's first snapshot, a rejoin) is drawn at once: nothing to ease from.
                for (int i = 0; i < fog.State.Length; i++) levels[i] = Palette[fog.State[i]].a;
                snap = true;
            }
            else foreach (int i in _changed) levels[i] = Palette[fog.State[i]].a;
            _target.Apply(false);
            _easedFrames = 0;
            _easedSeconds = 0f;
        }
        if (_target == null) return;
        if (!_current[_front].IsCreated()) snap = true; // lost with the device: start from the levels
        float dt = Time.unscaledDeltaTime;
        if (!snap && (_easedFrames > 255 || _easedSeconds > FadeSeconds)) return; // every cell is on its level
        _easedFrames++;
        _easedSeconds += dt;
        Ease(snap ? 255 : StepOf(dt));
    }

    /// <summary>Moves every cell <paramref name="step"/> levels toward its target on the GPU, into the texture the overlay draws.</summary>
    private void Ease(int step)
    {
        RenderTexture from = _current[_front], to = _current[1 - _front];
        if (!to.IsCreated()) to.Create();
        _fadeProperties.SetTexture("_Current", from);
        _fadeProperties.SetTexture("_Target", _target);
        _fadeProperties.SetFloat("_Step", step);
        _commands.Clear();
        _commands.SetRenderTarget(to);
        _commands.DrawProcedural(Matrix4x4.identity, _fade, 0, MeshTopology.Triangles, 3, 1, _fadeProperties);
        Graphics.ExecuteCommandBuffer(_commands);
        _front = 1 - _front;
        _material.mainTexture = to;
    }

    private void Build(ClientFog fog)
    {
        Release();
        _target = new Texture2D(fog.Width, fog.Height, TextureFormat.R8, mipChain: false, linear: true)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "FogView levels",
        };
        for (int k = 0; k < 2; k++)
        {
            _current[k] = new RenderTexture(fog.Width, fog.Height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "FogView",
            };
            _current[k].Create();
        }
        _front = 0;
        if (_fade == null)
        {
            Shader fade = Resources.Load<Shader>("Shaders/FogFade");
            if (fade == null) WarLog.Error(LogCategory.Art, "Resources/Shaders/FogFade.shader is missing");
            _fade = new Material(fade);
            _fadeProperties = new MaterialPropertyBlock();
            _commands = new CommandBuffer { name = "FogView ease" };
        }
        if (_material == null)
        {
            Shader shader = Resources.Load<Shader>("Shaders/Fog");
            if (shader == null) WarLog.Error(LogCategory.Art, "Resources/Shaders/Fog.shader is missing");
            _material = new Material(shader) { renderQueue = 3500 };
            _material.SetFloat("_PixelsPerUnit", WAR2D.Art.ArtStyle.PixelsPerUnit);
            _material.SetFloat("_Explored", Palette[1].a / 255f);
        }
        _material.mainTexture = _current[_front];
        _material.SetVector("_WorldSize", new Vector4(fog.Width * fog.CellSize, fog.Height * fog.CellSize, 0, 0));

        if (_mesh != null) Destroy(_mesh);
        _mesh = new Mesh { name = "FogView" };
        float w = fog.Width * fog.CellSize, h = fog.Height * fog.CellSize;
        _mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(w, 0, 0), new Vector3(0, h, 0), new Vector3(w, h, 0) };
        _mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        _mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        _mesh.RecalculateBounds();

        if (!TryGetComponent(out MeshFilter filter)) filter = gameObject.AddComponent<MeshFilter>();
        if (!TryGetComponent(out MeshRenderer meshRenderer)) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        filter.sharedMesh = _mesh;
        meshRenderer.sharedMaterial = _material;
        meshRenderer.sortingOrder = WAR2D.Client.Render.SortingOrders.Fog;
        transform.position = new Vector3(0, 0, -1);
    }

    private void Release()
    {
        if (_target != null) Destroy(_target);
        for (int k = 0; k < 2; k++)
            if (_current[k] != null)
            {
                _current[k].Release();
                Destroy(_current[k]);
                _current[k] = null;
            }
    }

    private void OnDestroy()
    {
        Release();
        if (_material != null) Destroy(_material);
        if (_fade != null) Destroy(_fade);
        _commands?.Release();
        if (_mesh != null) Destroy(_mesh);
        if (_instance == this) _instance = null;
    }
}
