using System.Collections.Generic;
using UnityEngine;
using WAR2D.Net.Replication;

/// <summary>
/// Draws the local team's fog of war over the map: black where unexplored, dimmed where explored but
/// hidden, clear where seen. One texel per fog cell, filtered for soft edges; only changed texels are
/// rewritten.
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
        if (!_dirty) return;
        _dirty = false;
        ClientFog fog = ClientFog.Current;
        if (fog.State == null) return;

        _changed.Clear();
        bool all = fog.TakeChanges(_changed);
        if (_texture == null || _texture.width != fog.Width || _texture.height != fog.Height)
        {
            Build(fog);
            all = true;
        }
        var pixels = _texture.GetPixelData<Color32>(0);
        if (all)
        {
            for (int i = 0; i < fog.State.Length; i++) pixels[i] = Palette[fog.State[i]];
        }
        else
        {
            foreach (int i in _changed) pixels[i] = Palette[fog.State[i]];
        }
        _texture.Apply(false);
    }

    private void Build(ClientFog fog)
    {
        if (_texture != null) Destroy(_texture);
        _texture = new Texture2D(fog.Width, fog.Height, TextureFormat.RGBA32, mipChain: false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "FogView",
        };
        if (_material == null)
        {
            _material = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3500 };
        }
        _material.mainTexture = _texture;

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
        meshRenderer.sortingOrder = 1000;
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
