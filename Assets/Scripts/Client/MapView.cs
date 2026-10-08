using Unity.Collections;
using UnityEngine;
using WAR2D.World;

/// <summary>
/// Draws a <see cref="MapGrid"/> as one point-filtered texture (one texel per tile) on a quad behind
/// everything else. Real tile art replaces this in v0.8.
/// </summary>
public class MapView : MonoBehaviour
{
    private static MapView _instance;
    private Texture2D _texture;
    private Material _material;
    private Mesh _mesh;

    /// <summary>Bakes the grid and shows it, replacing any earlier map. Safe to call again.</summary>
    public static void Show(MapGrid grid)
    {
        if (_instance == null) _instance = new GameObject("MapView").AddComponent<MapView>();
        _instance.Bake(grid);
    }

    /// <summary>The backdrop colour of a tile kind (the v0.3 spike's colours).</summary>
    public static Color32 TileColor(TileType type) => type switch
    {
        TileType.Ground => new Color32(33, 33, 38, 255),
        TileType.Wall => new Color32(76, 74, 71, 255),
        TileType.Gem => new Color32(56, 51, 77, 255),
        _ => new Color32(12, 12, 12, 255),
    };

    private void Bake(MapGrid grid)
    {
        if (_texture == null || _texture.width != grid.Width || _texture.height != grid.Height)
        {
            if (_texture != null) Destroy(_texture);
            _texture = new Texture2D(grid.Width, grid.Height, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "MapView",
            };
        }

        NativeArray<Color32> pixels = _texture.GetPixelData<Color32>(0);
        var palette = new Color32[4];
        for (int i = 0; i < palette.Length; i++) palette[i] = TileColor((TileType)i);
        for (int i = 0; i < grid.Tiles.Length; i++) pixels[i] = palette[grid.Tiles[i] & 3];
        _texture.Apply(false);

        if (_material == null) _material = new Material(Shader.Find("Sprites/Default"));
        _material.mainTexture = _texture;

        if (_mesh != null) Destroy(_mesh);
        _mesh = new Mesh { name = "MapView" };
        float w = grid.Width, h = grid.Height;
        _mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(w, 0, 0), new Vector3(0, h, 0), new Vector3(w, h, 0) };
        _mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        _mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        _mesh.RecalculateBounds();

        if (!TryGetComponent(out MeshFilter filter)) filter = gameObject.AddComponent<MeshFilter>();
        if (!TryGetComponent(out MeshRenderer meshRenderer)) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        filter.sharedMesh = _mesh;
        meshRenderer.sharedMaterial = _material;
        meshRenderer.sortingOrder = -1000;
        transform.position = new Vector3(0, 0, 1);
    }

    private void OnDestroy()
    {
        if (_texture != null) Destroy(_texture);
        if (_material != null) Destroy(_material);
        if (_mesh != null) Destroy(_mesh);
        if (_instance == this) _instance = null;
    }
}
