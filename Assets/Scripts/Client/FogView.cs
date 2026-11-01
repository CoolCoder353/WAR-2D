using WAR2D.Dev.Logging;
using System.Collections.Generic;
using UnityEngine;
using WAR2D.Net.Replication;

/// <summary>
/// Draws the local team's fog of war over the map: black where unexplored, dimmed where explored but
/// hidden, clear where seen. One texel per fog cell, filtered and then dithered on the art-pixel grid by the
/// <c>Fog</c> shader, so its edges read as pixel art; only changed texels are rewritten. A changed cell eases to
/// its new level over <see cref="FadeSeconds"/> (polish item H), which the shader's dither turns into a dissolve.
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

    private Texture2D _texture;
    private Material _material;
    private Mesh _mesh;
    private bool _dirty;
    private readonly List<int> _changed = new List<int>();
    private readonly List<int> _fading = new List<int>(); // cells still easing toward their level
    private bool[] _isFading = System.Array.Empty<bool>();

    /// <summary>Seconds a cell takes to ease all the way between unexplored and seen (a smaller change takes less).</summary>
    public const float FadeSeconds = 0.25f;

    /// <summary>Cells easing now (tests and the perf notes).</summary>
    public static int Fading => _instance != null ? _instance._fading.Count : 0;

    /// <summary>An alpha moved from <paramref name="current"/> toward <paramref name="target"/> by <paramref name="dt"/> seconds of the fade.</summary>
    public static byte Toward(byte current, byte target, float dt)
    {
        int step = Mathf.Max(1, Mathf.CeilToInt(255f * dt / FadeSeconds));
        return current < target ? (byte)Mathf.Min(target, current + step) : (byte)Mathf.Max(target, current - step);
    }

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
        if (!_dirty && _fading.Count == 0) return;
        if (fog.State == null) return;
        if (_dirty)
        {
            _dirty = false;
            _changed.Clear();
            bool all = fog.TakeChanges(_changed);
            if (_texture == null || _texture.width != fog.Width || _texture.height != fog.Height)
            {
                Build(fog);
                all = true;
            }
            if (all)
            {
                // A new grid (the match's first snapshot, a rejoin) is drawn at once: nothing to ease from.
                var every = _texture.GetPixelData<Color32>(0);
                for (int i = 0; i < fog.State.Length; i++) every[i] = Palette[fog.State[i]];
                foreach (int i in _fading) _isFading[i] = false;
                _fading.Clear();
            }
            else
            {
                foreach (int i in _changed)
                    if (!_isFading[i]) { _isFading[i] = true; _fading.Add(i); }
            }
        }
        if (_texture == null) return;
        var pixels = _texture.GetPixelData<Color32>(0);
        float dt = Time.unscaledDeltaTime;
        for (int k = _fading.Count - 1; k >= 0; k--)
        {
            int i = _fading[k];
            byte target = Palette[fog.State[i]].a;
            byte next = Toward(pixels[i].a, target, dt);
            pixels[i] = new Color32(0, 0, 0, next);
            if (next != target) continue;
            _isFading[i] = false;
            _fading[k] = _fading[_fading.Count - 1];
            _fading.RemoveAt(_fading.Count - 1);
        }
        _texture.Apply(false);
    }

    private void Build(ClientFog fog)
    {
        if (_texture != null) Destroy(_texture);
        _isFading = new bool[fog.Width * fog.Height];
        _fading.Clear();
        _texture = new Texture2D(fog.Width, fog.Height, TextureFormat.RGBA32, mipChain: false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "FogView",
        };
        if (_material == null)
        {
            Shader shader = Resources.Load<Shader>("Shaders/Fog");
            if (shader == null) WarLog.Error(LogCategory.Art, "Resources/Shaders/Fog.shader is missing");
            _material = new Material(shader) { renderQueue = 3500 };
            _material.SetFloat("_PixelsPerUnit", WAR2D.Art.ArtStyle.PixelsPerUnit);
            _material.SetFloat("_Explored", Palette[1].a / 255f);
        }
        _material.mainTexture = _texture;
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

    private void OnDestroy()
    {
        if (_texture != null) Destroy(_texture);
        if (_material != null) Destroy(_material);
        if (_mesh != null) Destroy(_mesh);
        if (_instance == this) _instance = null;
    }
}
