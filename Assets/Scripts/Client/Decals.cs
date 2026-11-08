using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using WAR2D.Net.Replication;
using WAR2D.Art;
using WAR2D.Art.Render;

/// <summary>
/// Client-only ground marks from the <c>Effects</c> atlas (polish items 3 and G): scorch where a unit died, rubble over a
/// destroyed building's footprint, and tread marks behind vehicles. Each mark holds, then fades through its dithered frames
/// and goes. Like <see cref="Effects"/>, each kind has a fixed pool of marks made once; when a pool is full the oldest mark
/// is reused. Marks only follow deaths and movement the client already draws. A destroyed unit also leaves its wreck (its own
/// sprite at its last facing, charred), which fades out by alpha (playtest 2026-10-30: wrecks, and longer-lasting marks).
/// Since 2026-11-02 marks last much longer (the owner chose longer holds over bigger pools, which cost every frame): scorch
/// and wrecks ten minutes, tracks two, and rubble until a building or blueprint goes up over it. A big fight still recycles
/// the oldest marks once a pool is full.
/// Every mark is a quad in one of two meshes, the ground marks' and the wrecks' over them, each one renderer (2026-11-08: as a
/// sprite renderer each, the 2,900 marks of a big battle cost a 100,000-unit client on three cores a third of its frame).
/// </summary>
public static class Decals
{
    /// <summary>Pool sizes: the most of each kind on the map at once.</summary>
    public const int ScorchPool = 256, LargeScorchPool = 64, RubblePool = 48, LargeRubblePool = 16, TrackPool = 2048, WreckPool = 192, CraterPool = 96;

    /// <summary>Seconds a shell's crater holds before it fades, and the seconds the fade takes: about half a minute in all.</summary>
    public const float CraterHold = 10f, CraterFade = 20f;

    /// <summary>Seconds a scorch holds before it fades, and the seconds the fade takes.</summary>
    public const float ScorchHold = 600f, ScorchFade = 20f;

    /// <summary>Rubble never fades on its own: it stays until a building or blueprint enters over it (<see cref="ClearRubbleUnder"/>).</summary>
    public const float RubbleHold = float.PositiveInfinity, RubbleFade = 30f;

    /// <summary>Seconds a tread mark holds before it fades, and the seconds the fade takes.</summary>
    public const float TrackHold = 120f, TrackFade = 10f;

    /// <summary>Seconds a unit's wreck holds before it fades, and the seconds the fade takes.</summary>
    public const float WreckHold = 600f, WreckFade = 15f;

    /// <summary>The tint on a wreck's sprite: charred, dark and a little warm.</summary>
    public static readonly Color WreckTint = new Color(0.32f, 0.29f, 0.27f, 1f);

    private sealed class Slot
    {
        public float Start;
        public int Direction, Frame;
        /// <summary>Rubble: the width in tiles of the building it was left by.</summary>
        public int Tiles;
        public bool Active;
        public Vector2 Position;
        /// <summary>A tread mark's heading (radians); the others lie unturned.</summary>
        public float Heading;
        public Sprite Sprite;
        public Color Tint = Color.white;
    }

    private sealed class Pool
    {
        public readonly string Id;
        public readonly int Size;
        public readonly float Hold, Fade;
        public readonly List<Slot> Slots = new List<Slot>();
        public int Next;
        /// <summary>The mesh the pool's marks are in, and its first quad there (slot i is quad First + i).</summary>
        public Layer Layer;
        public int First;

        public Pool(string id, int size, float hold, float fade) { Id = id; Size = size; Hold = hold; Fade = fade; }
    }

    /// <summary>One mesh of quads, one per slot of its pools: written on the CPU as marks change, uploaded once a frame when any did.</summary>
    private sealed class Layer
    {
        public readonly string Name;
        public readonly int Order;
        public int Quads;
        public Vertex[] Vertices;
        public Mesh Mesh;
        public UnityEngine.Material Material;
        public int DirtyFrom = int.MaxValue, DirtyTo = -1;

        public Layer(string name, int order) { Name = name; Order = order; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Color32 Colour;
        public Vector2 Uv;
    }

    private static readonly Pool Scorches = new Pool("Scorch", ScorchPool, ScorchHold, ScorchFade);
    private static readonly Pool LargeScorches = new Pool("ScorchLarge", LargeScorchPool, ScorchHold, ScorchFade);
    private static readonly Pool Rubble1 = new Pool("Rubble1", RubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Rubble2 = new Pool("Rubble2", RubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Rubble3 = new Pool("Rubble3", RubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Rubble5 = new Pool("Rubble5", LargeRubblePool, RubbleHold, RubbleFade);
    private static readonly Pool Tracks = new Pool("Tracks", TrackPool, TrackHold, TrackFade);
    private static readonly Pool Wrecks = new Pool("Wreck", WreckPool, WreckHold, WreckFade);
    private static readonly Pool Craters = new Pool("Crater", CraterPool, CraterHold, CraterFade);
    /// <summary>v0.20: the larger crater an Artillery shell's 3.5-tile blast leaves.</summary>
    private static readonly Pool LargeCraters = new Pool("CraterLarge", CraterPool, CraterHold, CraterFade);
    private static readonly Pool[] RubblePools = { Rubble1, Rubble2, Rubble3, Rubble5 };
    private static readonly Pool[] All = { Scorches, LargeScorches, Rubble1, Rubble2, Rubble3, Rubble5, Tracks, Wrecks, Craters, LargeCraters };
    // A wreck lies on its scorch.
    private static readonly Layer Marks = new Layer("Marks", WAR2D.Client.Render.SortingOrders.Decals);
    private static readonly Layer WreckLayer = new Layer("Wrecks", WAR2D.Client.Render.SortingOrders.Decals + 1);
    private static Runner runner;
    private static ArtAtlas atlas;

    static Decals()
    {
        foreach (Pool p in All)
        {
            p.Layer = p == Wrecks ? WreckLayer : Marks;
            p.First = p.Layer.Quads;
            p.Layer.Quads += p.Size;
        }
    }

    /// <summary>Marks made so far (tests: stays at most the pool sizes).</summary>
    public static int Created { get; private set; }

    /// <summary>Scorches (both sizes) on the map now.</summary>
    public static int ActiveScorches => Count(Scorches) + Count(LargeScorches);

    /// <summary>Rubble patches on the map now.</summary>
    public static int ActiveRubble => Count(Rubble1) + Count(Rubble2) + Count(Rubble3) + Count(Rubble5);

    /// <summary>Tread marks on the map now.</summary>
    public static int ActiveTracks => Count(Tracks);

    /// <summary>Unit wrecks on the map now.</summary>
    public static int ActiveWrecks => Count(Wrecks);

    /// <summary>Shell craters on the map now.</summary>
    public static int ActiveCraters => Count(Craters);

    /// <summary>A wreck's alpha <paramref name="age"/> seconds after it was left: 1 while it holds, then down to 0; below 0 once it is gone.</summary>
    public static float WreckAlpha(float age) => age < WreckHold ? 1f : 1f - (age - WreckHold) / WreckFade;

    /// <summary>
    /// The fade frame a mark shows <paramref name="age"/> seconds after it was made: 0 while it holds, then one frame per equal
    /// share of the fade; -1 once it is gone.
    /// </summary>
    public static int FrameAt(float age, float hold, float fade, int frames = EffectGen.DecalFrames)
    {
        if (age < hold) return 0;
        float into = (age - hold) / fade;
        if (into >= 1f) return -1;
        return Mathf.Min(frames - 1, 1 + (int)(into * (frames - 1)));
    }

    /// <summary>The scorch left where a unit died; <paramref name="large"/> for a 2-tile unit.</summary>
    public static void Scorch(Vector2 position, bool large) => Place(large ? LargeScorches : Scorches, position, 0);

    /// <summary>Rubble over a destroyed building's footprint, centred on <paramref name="centre"/>; <paramref name="tiles"/> is its width.</summary>
    public static void Rubble(Vector2 centre, int tiles)
    {
        Slot s = Place(tiles >= 5 ? Rubble5 : tiles >= 3 ? Rubble3 : tiles == 2 ? Rubble2 : Rubble1, centre, 0);
        if (s != null) s.Tiles = Mathf.Max(1, tiles);
    }

    /// <summary>
    /// Removes the rubble a footprint of <paramref name="size"/> tiles centred on <paramref name="centre"/> overlaps (a building
    /// or blueprint went up over it). Returns how many patches went.
    /// </summary>
    public static int ClearRubbleUnder(Vector2 centre, int2 size)
    {
        int cleared = 0;
        foreach (Pool pool in RubblePools)
        {
            for (int i = 0; i < pool.Slots.Count; i++)
            {
                Slot s = pool.Slots[i];
                if (!s.Active) continue;
                Vector2 d = s.Position - centre;
                if (Mathf.Abs(d.x) * 2f >= s.Tiles + size.x || Mathf.Abs(d.y) * 2f >= s.Tiles + size.y) continue;
                Hide(pool, i);
                cleared++;
            }
        }
        return cleared;
    }

    /// <summary>
    /// A tread mark centred on <paramref name="position"/>, laid along <paramref name="heading"/> (radians). The east–west mark is
    /// turned to the exact heading (playtest 2026-10-30: marks snapped to 45° steps made a staircase behind a vehicle).
    /// </summary>
    public static void Track(Vector2 position, float heading) => Place(Tracks, position, 0, heading: heading);

    /// <summary>The wreck a destroyed unit leaves at <paramref name="position"/>: <paramref name="sprite"/> (its last facing), charred.</summary>
    public static void Wreck(Vector2 position, Sprite sprite)
    {
        if (sprite != null) Place(Wrecks, position, 0, sprite, WreckTint);
    }

    /// <summary>The crater a shell leaves where it lands: one of the drawn craters, fixed by the spot so nothing random runs per shot.</summary>
    public static void Crater(Vector2 position) =>
        Place(Craters, position, (Mathf.FloorToInt(position.x * 7f) + Mathf.FloorToInt(position.y * 13f) & int.MaxValue) % EffectGen.CraterVariants);

    /// <summary>v0.20: the larger crater of an Artillery shell's blast, chosen by the spot like <see cref="Crater"/>.</summary>
    public static void CraterLarge(Vector2 position) =>
        Place(LargeCraters, position, (Mathf.FloorToInt(position.x * 7f) + Mathf.FloorToInt(position.y * 13f) & int.MaxValue) % EffectGen.CraterVariants);

    /// <summary>v0.20: a wreck burnt black (a fire death): the wreck, darker.</summary>
    public static void BurntWreck(Vector2 position, Sprite sprite)
    {
        if (sprite != null) Place(Wrecks, position, 0, sprite, BurntTint);
    }

    /// <summary>v0.20: a burnt wreck's tint, darker than <see cref="WreckTint"/>.</summary>
    private static readonly Color BurntTint = new Color(0.22f, 0.2f, 0.19f, 1f);

    /// <summary>Removes every mark and its meshes (tests, and the end of a match).</summary>
    public static void Clear()
    {
        if (runner != null)
        {
            if (Application.isPlaying) Object.Destroy(runner.gameObject);
            else Object.DestroyImmediate(runner.gameObject);
        }
        Reset();
    }

    /// <summary>The heading (degrees) the drawn quad of the <paramref name="slot"/>-th tread mark lies along (tests).</summary>
    internal static float DrawnTrackDegrees(int slot)
    {
        Vertex[] v = Marks.Vertices;
        int q = (Tracks.First + slot) * 4;
        Vector2 along = v[q + 1].Position - v[q].Position;
        return Mathf.Repeat(Mathf.Atan2(along.y, along.x) * Mathf.Rad2Deg, 360f);
    }

    private static void Reset()
    {
        runner = null;
        Created = 0;
        foreach (Pool p in All)
        {
            p.Slots.Clear();
            p.Next = 0;
        }
        foreach (Layer layer in new[] { Marks, WreckLayer })
        {
            if (layer.Mesh != null) Destroy(layer.Mesh);
            if (layer.Material != null) Destroy(layer.Material);
            layer.Mesh = null;
            layer.Material = null;
            layer.Vertices = null;
            layer.DirtyFrom = int.MaxValue;
            layer.DirtyTo = -1;
        }
    }

    private static void Destroy(Object o)
    {
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }

    private static int Count(Pool p)
    {
        int n = 0;
        foreach (Slot s in p.Slots) if (s.Active) n++;
        return n;
    }

    private static Slot Place(Pool pool, Vector2 position, int direction, Sprite own = null, Color? tint = null, float heading = 0f)
    {
        if (runner == null)
        {
            Reset();
            runner = new GameObject("Decals").AddComponent<Runner>();
        }
        if (atlas == null || atlas.Texture == null) atlas = ArtAtlas.Load(ArtAtlas.Effects);
        if (own == null && (atlas == null || !atlas.Contains(pool.Id))) return null;

        int i;
        if (pool.Slots.Count < pool.Size)
        {
            i = pool.Slots.Count;
            pool.Slots.Add(new Slot());
            Created++;
        }
        else
        {
            i = pool.Next;
            pool.Next = (pool.Next + 1) % pool.Size;
        }
        Slot s = pool.Slots[i];
        s.Active = true;
        s.Start = Time.time;
        s.Direction = direction;
        s.Frame = 0;
        s.Position = position;
        s.Heading = heading;
        s.Tint = tint ?? Color.white;
        s.Sprite = own != null ? own : atlas.Sprite(pool.Id, direction * atlas.Frames(pool.Id));
        Draw(pool, i);
        return s;
    }

    /// <summary>Hides a mark: its quad shrinks to nothing.</summary>
    private static void Hide(Pool pool, int i)
    {
        pool.Slots[i].Active = false;
        Write(pool, i, default, default, default, default, default, default);
    }

    /// <summary>Writes a mark's quad: its sprite's rect around its pivot, turned by its heading, in its tint.</summary>
    private static void Draw(Pool pool, int i)
    {
        Slot s = pool.Slots[i];
        Sprite sprite = s.Sprite;
        if (sprite == null || sprite.texture == null)
        {
            Write(pool, i, default, default, default, default, default, default);
            return;
        }
        Layer layer = pool.Layer;
        if (layer.Material == null) Build(layer, sprite.texture);
        else if (layer.Material.mainTexture != sprite.texture) layer.Material.mainTexture = sprite.texture;
        Rect rect = sprite.rect, uvRect = sprite.textureRect;
        float ppu = sprite.pixelsPerUnit;
        Vector2 min = -sprite.pivot / ppu, max = min + rect.size / ppu;
        float c = Mathf.Cos(s.Heading), n = Mathf.Sin(s.Heading);
        Vector2 Turn(float x, float y) => s.Position + new Vector2(x * c - y * n, x * n + y * c);
        float w = sprite.texture.width, h = sprite.texture.height;
        Write(pool, i, Turn(min.x, min.y), Turn(max.x, min.y), Turn(max.x, max.y), Turn(min.x, max.y),
            new Vector4(uvRect.xMin / w, uvRect.yMin / h, uvRect.xMax / w, uvRect.yMax / h), s.Tint);
    }

    private static void Write(Pool pool, int i, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 uv, Color32 colour)
    {
        Layer layer = pool.Layer;
        if (layer.Vertices == null) return; // nothing drawn in it yet
        int quad = pool.First + i, v = quad * 4;
        layer.Vertices[v] = new Vertex { Position = a, Colour = colour, Uv = new Vector2(uv.x, uv.y) };
        layer.Vertices[v + 1] = new Vertex { Position = b, Colour = colour, Uv = new Vector2(uv.z, uv.y) };
        layer.Vertices[v + 2] = new Vertex { Position = c, Colour = colour, Uv = new Vector2(uv.z, uv.w) };
        layer.Vertices[v + 3] = new Vertex { Position = d, Colour = colour, Uv = new Vector2(uv.x, uv.w) };
        layer.DirtyFrom = Mathf.Min(layer.DirtyFrom, quad);
        layer.DirtyTo = Mathf.Max(layer.DirtyTo, quad);
    }

    /// <summary>The layer's mesh (every quad empty until written), its renderer under the runner, and a sprite material on <paramref name="texture"/>.</summary>
    private static void Build(Layer layer, Texture texture)
    {
        int quads = layer.Quads;
        layer.Vertices = new Vertex[quads * 4];
        var indices = new ushort[quads * 6];
        for (int q = 0; q < quads; q++)
        {
            int v = q * 4, k = q * 6;
            indices[k] = (ushort)v; indices[k + 1] = (ushort)(v + 2); indices[k + 2] = (ushort)(v + 1);
            indices[k + 3] = (ushort)v; indices[k + 4] = (ushort)(v + 3); indices[k + 5] = (ushort)(v + 2);
        }
        var mesh = new Mesh { name = "Decals " + layer.Name };
        mesh.MarkDynamic();
        mesh.SetVertexBufferParams(quads * 4,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
        mesh.SetVertexBufferData(layer.Vertices, 0, 0, quads * 4);
        mesh.SetIndexBufferParams(quads * 6, IndexFormat.UInt16);
        mesh.SetIndexBufferData(indices, 0, 0, quads * 6);
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, quads * 6), MeshUpdateFlags.DontRecalculateBounds);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f); // marks lie anywhere on the map: never culled as a whole
        layer.Mesh = mesh;
        layer.Material = new UnityEngine.Material(Shader.Find("Sprites/Default")) { name = "Decals " + layer.Name, mainTexture = texture };
        var go = new GameObject(layer.Name);
        go.transform.SetParent(runner.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = layer.Material;
        meshRenderer.sortingOrder = layer.Order;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
    }

    /// <summary>Uploads the quads written since the last upload.</summary>
    private static void Upload(Layer layer)
    {
        if (layer.Mesh == null || layer.DirtyTo < 0) return;
        int from = layer.DirtyFrom * 4, count = (layer.DirtyTo - layer.DirtyFrom + 1) * 4;
        layer.Mesh.SetVertexBufferData(layer.Vertices, from, from, count, 0,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        layer.DirtyFrom = int.MaxValue;
        layer.DirtyTo = -1;
    }

    /// <summary>
    /// Steps every mark through its fade at most this often (2026-11-08: walking the 2,700 pooled marks every frame cost a 100,000-unit
    /// client 0.35 ms of its main thread a frame; a fade frame lasts far longer).
    /// </summary>
    private const float StepSeconds = 0.05f;

    /// <summary>Steps every mark through its fade; hides the ones that are gone; uploads what changed.</summary>
    private sealed class Runner : MonoBehaviour
    {
        private ClientBuildings watched;
        private float lastStep = float.NegativeInfinity;

        private void OnEntered(BuildingData data, HealthComponent health)
        {
            if (data.buildingType == BuildingType.Mine) return;
            int2 size = WorldStateManager.GetBuildingSize(data.buildingType);
            float2 centre = Footprint.VisualCenter((int2)math.round(data.position), size);
            ClearRubbleUnder(new Vector2(centre.x, centre.y), size);
        }

        private void Update()
        {
            if (watched != ClientBuildings.Current) // replaced at the end of a match
            {
                if (watched != null) watched.Entered -= OnEntered;
                watched = ClientBuildings.Current;
                if (watched != null) watched.Entered += OnEntered;
            }
            float now = Time.time;
            if (now - lastStep < StepSeconds) return;
            lastStep = now;
            for (int i = 0; i < Wrecks.Slots.Count; i++)
            {
                Slot s = Wrecks.Slots[i];
                if (!s.Active) continue;
                float alpha = WreckAlpha(now - s.Start);
                if (alpha <= 0f)
                {
                    Hide(Wrecks, i);
                    continue;
                }
                if (alpha >= 1f) continue;
                s.Tint.a = alpha;
                Draw(Wrecks, i);
            }
            if (atlas == null) return;
            foreach (Pool pool in All)
            {
                if (pool == Wrecks) continue;
                int frames = atlas.Frames(pool.Id);
                for (int i = 0; i < pool.Slots.Count; i++)
                {
                    Slot s = pool.Slots[i];
                    if (!s.Active) continue;
                    int frame = FrameAt(now - s.Start, pool.Hold, pool.Fade, frames);
                    if (frame < 0)
                    {
                        Hide(pool, i);
                        continue;
                    }
                    if (frame == s.Frame) continue;
                    s.Frame = frame;
                    s.Sprite = atlas.Sprite(pool.Id, s.Direction * frames + frame);
                    Draw(pool, i);
                }
            }
        }

        // Marks placed after this (a later script's LateUpdate) go up with the next frame's.
        private void LateUpdate()
        {
            Upload(Marks);
            Upload(WreckLayer);
        }

        private void OnDestroy()
        {
            if (watched != null) watched.Entered -= OnEntered;
            if (runner == this) Reset();
        }
    }
}
