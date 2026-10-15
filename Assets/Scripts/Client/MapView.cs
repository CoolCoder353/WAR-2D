using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Style;
using WAR2D.Client.Render;
using WAR2D.World;

/// <summary>
/// Draws a <see cref="MapGrid"/> on one quad behind everything else with the <c>Terrain</c> shader: an RGBA32
/// index texture (one texel per tile: R = <see cref="TileType"/>, G = its frame: the <see cref="TileAutotile"/>
/// variant, plus the damage state × 16 for player walls, B = a player wall's owner palette index + 1) picks each
/// tile's frame in the <c>Tiles</c> atlas, and the shader tints wall team pixels in the owner's colour.
/// Wall owners and health come from <see cref="WAR2D.Net.Replication.ClientTerrain"/>. <see cref="Refresh"/>
/// rewrites changed tiles (and their neighbours, whose masks change with them).
/// </summary>
public class MapView : MonoBehaviour
{
    public const string ShaderPath = "Shaders/Terrain";

    /// <summary>Per-type slots in the shader's tables; 4 and 5 are reserved for v0.7's player walls.</summary>
    public const int TypeSlots = 8;

    private static MapView _instance;
    private Texture2D _index;
    private Material _material;
    private Mesh _mesh;
    private GraphicsBuffer _frameRects;
    private MapGrid _grid;

    /// <summary>The shown map's index texture (tests), or null.</summary>
    public static Texture2D IndexTexture => _instance != null ? _instance._index : null;

    /// <summary>Bakes the grid and shows it, replacing any earlier map. Safe to call again.</summary>
    public static void Show(MapGrid grid)
    {
        // A -nographics player (the perf clients) has no GPU buffers to draw the map with.
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        if (_instance == null) _instance = new GameObject("MapView").AddComponent<MapView>();
        _instance.Bake(grid);
    }

    /// <summary>Rewrites <paramref name="tiles"/> (and their neighbours) after their type changed in the shown grid.</summary>
    public static void Refresh(IReadOnlyList<int2> tiles)
    {
        if (_instance == null || _instance._index == null || tiles == null || tiles.Count == 0) return;
        _instance.Rewrite(tiles);
    }

    /// <summary>The tile kind the view shows at a tile (before a <see cref="Refresh"/> it is the old one); Border when none.</summary>
    public static TileType ShownType(int2 tile)
    {
        if (_instance == null || _instance._index == null || (uint)tile.x >= (uint)_instance._index.width || (uint)tile.y >= (uint)_instance._index.height)
            return TileType.Border;
        return (TileType)_instance._index.GetPixelData<Color32>(0)[tile.y * _instance._index.width + tile.x].r;
    }

    /// <summary>Removes the shown map (the menu battle's teardown).</summary>
    public static void Hide()
    {
        if (_instance != null) Destroy(_instance.gameObject);
        _instance = null;
    }

    /// <summary>
    /// A tile kind's flat colour (far zoom, minimap, lobby preview): a base colour from the style's palette
    /// (<see cref="ArtStyle.Palette"/>).
    /// </summary>
    public static Color32 TileColor(TileType type)
    {
        Color32[] roles = StylePalettes.Charcoal.Colours;
        return type switch
        {
            TileType.Ground => roles[StylePalette.Floor0],
            TileType.Wall => roles[StylePalette.Rock2],
            TileType.Gem => roles[StylePalette.Gem1],
            TileType.PlayerWall => roles[StylePalette.Rock1],
            TileType.WallBlueprint => roles[StylePalette.Floor1],
            _ => roles[StylePalette.Rock0],
        };
    }

    /// <summary>The number of player colours the shader holds (index 0 of its table is "no owner").</summary>
    public const int TeamColours = 8;

    /// <summary>
    /// The index texel of tile (x, y): R = type, G = frame (variant, and for a player wall + 16 × damage state),
    /// B = for a wall tile its owner's palette index + 1 (0 for none). <paramref name="wall"/> gives a tile index's
    /// owner palette index (-1 for none) and health percent; null reads the local client's terrain.
    /// </summary>
    public static Color32 IndexOf(MapGrid grid, int x, int y, System.Func<int, (int colour, int health)> wall = null)
    {
        int i = y * grid.Width + x;
        byte type = grid.Tiles[i];
        byte variant = TileAutotile.Variant(grid, x, y);
        byte owner = 0;
        if (type == (byte)TileType.PlayerWall || type == (byte)TileType.WallBlueprint)
        {
            (int colour, int health) = (wall ?? LocalWall)(i);
            owner = (byte)(colour < 0 || colour >= TeamColours ? 0 : colour + 1);
            if (type == (byte)TileType.PlayerWall) variant = (byte)(variant + ArtManifest.AutotileVariants * WallRules.DamageState(health));
        }
        return new Color32(type, variant, owner, 255);
    }

    /// <summary>A wall tile's owner colour and health as the local client knows them.</summary>
    private static (int colour, int health) LocalWall(int index)
    {
        var terrain = WAR2D.Net.Replication.ClientTerrain.Current;
        int owner = terrain.WallOwner(index);
        return (owner == 0 ? -1 : PlayerPalette.ColourIndexOf(owner), owner == 0 ? 100 : terrain.WallHealth(index));
    }

    private void Bake(MapGrid grid)
    {
        _grid = grid;
        if (_index == null || _index.width != grid.Width || _index.height != grid.Height)
        {
            if (_index != null) Destroy(_index);
            _index = new Texture2D(grid.Width, grid.Height, TextureFormat.RGBA32, mipChain: false, linear: true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "MapIndex",
            };
        }

        NativeArray<Color32> texels = _index.GetPixelData<Color32>(0);
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
            texels[y * grid.Width + x] = IndexOf(grid, x, y);
        _index.Apply(false);

        if (_material == null) CreateMaterial();
        SetTeamColours();
        _material.SetTexture("_IndexTex", _index);
        _material.SetVector("_MapSize", new Vector4(grid.Width, grid.Height, 0, 0));

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
        meshRenderer.sortingOrder = SortingOrders.Terrain;
        transform.position = new Vector3(0, 0, 1);
    }

    private void CreateMaterial()
    {
        Shader shader = Resources.Load<Shader>(ShaderPath);
        if (shader == null) Debug.LogError($"Resources/{ShaderPath}.shader is missing");
        _material = new Material(shader) { name = "Terrain" };
        ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Tiles);
        if (atlas == null) Debug.LogError($"Resources/{ArtAtlas.Folder}/{ArtAtlas.Tiles} is missing: run WAR-2D/Art/Build all.");
        _material.SetTexture("_AtlasTex", atlas != null ? atlas.Texture : Texture2D.whiteTexture);
        _material.SetTexture("_MaskTex", atlas != null && atlas.Mask != null ? atlas.Mask : Texture2D.blackTexture);
        Palettes.Changed += SetTeamColours;
        _material.SetFloat("_TileSize", ArtStyle.TileSize);

        Vector4[] rects = InstancedUnitRenderer.UvRects(atlas);
        _frameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rects.Length, 16);
        _frameRects.SetData(rects);
        _material.SetBuffer("_FrameRects", _frameRects);

        var first = new float[TypeSlots];
        var flat = new Vector4[TypeSlots];
        string[] names = { nameof(TileType.Ground), nameof(TileType.Wall), nameof(TileType.Gem), nameof(TileType.Border), "PlayerWall", "WallBlueprint" };
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        for (int t = 0; t < TypeSlots; t++)
        {
            int cell = atlas != null && t < names.Length ? atlas.FirstFrame(names[t]) : -1;
            first[t] = cell < 0 ? (atlas != null ? atlas.FirstFrame(nameof(TileType.Border)) : 0) : cell;
            Color c = TileColor(t <= (int)TileType.WallBlueprint ? (TileType)t : TileType.Wall);
            flat[t] = linear ? (Vector4)c.linear : (Vector4)c;
        }
        _material.SetFloatArray("_TileFirst", first);
        _material.SetVectorArray("_TileFlat", flat);
    }

    private void Rewrite(IReadOnlyList<int2> tiles)
    {
        NativeArray<Color32> texels = _index.GetPixelData<Color32>(0);
        foreach (int2 tile in tiles)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int x = tile.x + dx, y = tile.y + dy;
            if ((uint)x >= (uint)_grid.Width || (uint)y >= (uint)_grid.Height) continue;
            texels[y * _grid.Width + x] = IndexOf(_grid, x, y);
        }
        _index.Apply(false);
    }

    /// <summary>The owner colours of the current palette (slot 0 is "no owner": grey).</summary>
    private void SetTeamColours()
    {
        if (_material == null) return;
        var colours = new Vector4[TeamColours + 1];
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        for (int i = 0; i <= TeamColours; i++)
        {
            Color c = PlayerPalette.Of(i - 1);
            colours[i] = linear ? (Vector4)c.linear : (Vector4)c;
        }
        _material.SetVectorArray("_TeamColors", colours);
    }

    private void OnDestroy()
    {
        Palettes.Changed -= SetTeamColours;
        if (_index != null) Destroy(_index);
        if (_material != null) Destroy(_material);
        if (_mesh != null) Destroy(_mesh);
        _frameRects?.Dispose();
        if (_instance == this) _instance = null;
    }
}
