using System;
using System.Collections.Generic;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>One sprite's frames in an atlas (the JSON next to the atlas PNG).</summary>
    [Serializable]
    public sealed class AtlasSprite
    {
        public string id;
        /// <summary>Index of its first frame in <see cref="AtlasData.frames"/>.</summary>
        public int first;
        public int frames, directions;
    }

    /// <summary>One frame's pixel rect (bottom-left origin) and pivot (pixels from the rect's bottom-left).</summary>
    [Serializable]
    public sealed class AtlasFrame
    {
        public int x, y, w, h;
        public float pivotX, pivotY;
    }

    /// <summary>The atlas JSON: size, pixels per unit, sprites and frames.</summary>
    [Serializable]
    public sealed class AtlasData
    {
        public int width, height, pixelsPerUnit;
        public List<AtlasSprite> sprites = new List<AtlasSprite>();
        public List<AtlasFrame> frames = new List<AtlasFrame>();
    }

    /// <summary>
    /// A generated atlas under <c>Resources/Art/</c>: the colour texture (team parts in grey), the team mask
    /// (white where the owner's colour goes) and the frame table. A sprite's frame <c>f</c> facing direction
    /// <c>d</c> is cell <c>FirstFrame(id) + d × Frames + f</c>; direction 0 faces east and they go
    /// anticlockwise in 45° steps.
    /// </summary>
    public sealed class ArtAtlas
    {
        /// <summary>The atlases <c>WAR-2D/Art/Build all</c> writes.</summary>
        public const string Units = "Units", Buildings = "Buildings", Tiles = "Tiles";

        /// <summary>The <c>Resources</c> folder the atlases live in.</summary>
        public const string Folder = "Art";

        public Texture2D Texture { get; private set; }
        public Texture2D Mask { get; private set; }
        public AtlasData Data { get; private set; }

        /// <summary>Each frame's rect in pixels (x, y, width, height).</summary>
        public Vector4[] FrameRects { get; private set; }

        private readonly Dictionary<string, AtlasSprite> byId = new Dictionary<string, AtlasSprite>(StringComparer.Ordinal);
        private readonly Dictionary<int, Sprite> sprites = new Dictionary<int, Sprite>();
        private static readonly Dictionary<string, ArtAtlas> Loaded = new Dictionary<string, ArtAtlas>();

        /// <summary>Loads (once) the atlas called <paramref name="name"/>; null when it hasn't been built.</summary>
        public static ArtAtlas Load(string name)
        {
            if (Loaded.TryGetValue(name, out ArtAtlas cached) && cached.Texture != null) return cached;
            var texture = Resources.Load<Texture2D>($"{Folder}/{name}");
            var json = Resources.Load<TextAsset>($"{Folder}/{name}");
            if (texture == null || json == null) return null;
            var atlas = new ArtAtlas
            {
                Texture = texture,
                Mask = Resources.Load<Texture2D>($"{Folder}/{name}_mask"),
                Data = JsonUtility.FromJson<AtlasData>(json.text),
            };
            atlas.Index();
            Loaded[name] = atlas;
            return atlas;
        }

        /// <summary>An atlas from data already in memory (tests, the build).</summary>
        public static ArtAtlas From(AtlasData data, Texture2D texture = null, Texture2D mask = null)
        {
            var atlas = new ArtAtlas { Data = data, Texture = texture, Mask = mask };
            atlas.Index();
            return atlas;
        }

        private void Index()
        {
            foreach (AtlasSprite s in Data.sprites) byId[s.id] = s;
            FrameRects = new Vector4[Data.frames.Count];
            for (int i = 0; i < FrameRects.Length; i++)
            {
                AtlasFrame f = Data.frames[i];
                FrameRects[i] = new Vector4(f.x, f.y, f.w, f.h);
            }
        }

        /// <summary>True when the atlas has a sprite with this id.</summary>
        public bool Contains(string id) => byId.ContainsKey(id);

        /// <summary>The cell index of <paramref name="id"/>'s first frame, or -1.</summary>
        public int FirstFrame(string id) => byId.TryGetValue(id, out AtlasSprite s) ? s.first : -1;

        /// <summary>How many cells <paramref name="id"/> has (frames × directions), or 0.</summary>
        public int FrameCount(string id) => byId.TryGetValue(id, out AtlasSprite s) ? s.frames * s.directions : 0;

        /// <summary>A sprite for one cell of <paramref name="id"/> (made on first use, then cached).</summary>
        public Sprite Sprite(string id, int frame = 0)
        {
            int first = FirstFrame(id);
            if (first < 0 || Texture == null || frame < 0 || frame >= FrameCount(id)) return null;
            int cell = first + frame;
            if (sprites.TryGetValue(cell, out Sprite sprite) && sprite != null) return sprite;
            AtlasFrame f = Data.frames[cell];
            sprite = UnityEngine.Sprite.Create(Texture, new Rect(f.x, f.y, f.w, f.h), new Vector2(f.pivotX / f.w, f.pivotY / f.h),
                Data.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            sprite.name = $"{id}_{frame}";
            sprites[cell] = sprite;
            return sprite;
        }
    }
}
