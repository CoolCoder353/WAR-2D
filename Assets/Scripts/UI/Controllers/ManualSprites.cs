using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.UI
{
    /// <summary>
    /// The manual's sprites (v0.18): frames cut from the built atlases and tinted as the unit shader tints them, kept once made.
    /// The atlases aren't readable on the CPU, so each frame is drawn once through a render texture. Null without a GPU (a
    /// headless player), when the manual shows the name instead.
    /// </summary>
    public static class ManualSprites
    {
        private const float TeamGain = 1.55f; // TeamSprite.shader
        private static readonly Dictionary<(string, int, int, Color32), Texture2D> Cache = new Dictionary<(string, int, int, Color32), Texture2D>();

        /// <summary>The frames to loop for a sprite at each facing: [facing][frame]. Facings start down-right and turn anticlockwise.</summary>
        public static Texture2D[][] Frames(string type, Color32 colour, bool allFacings)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            if (type == "Wall") return One(Cell(ArtAtlas.Tiles, "PlayerWall", 0, colour));
            if (type == nameof(BuildingType.GunTurret)) return Turret(colour, allFacings);
            ArtAtlas units = ArtAtlas.Load(ArtAtlas.Units);
            AtlasSprite unit = units?.Data.sprites.Find(s => s.id == type);
            if (unit != null) return Grid(ArtAtlas.Units, unit, System.Math.Min(2, unit.frames), colour, allFacings);
            ArtAtlas buildings = ArtAtlas.Load(ArtAtlas.Buildings);
            AtlasSprite building = buildings?.Data.sprites.Find(s => s.id == type);
            return building != null ? Grid(ArtAtlas.Buildings, building, building.frames, colour, allFacings) : null;
        }

        private static Texture2D[][] One(Texture2D texture) => texture == null ? null : new[] { new[] { texture } };

        private static Texture2D[][] Grid(string atlas, AtlasSprite sprite, int loop, Color32 colour, bool allFacings)
        {
            int facings = allFacings ? sprite.directions : 1;
            var grid = new Texture2D[facings][];
            for (int k = 0; k < facings; k++)
            {
                int direction = (k + sprite.directions - 1) % sprite.directions;
                grid[k] = new Texture2D[loop];
                for (int f = 0; f < loop; f++) grid[k][f] = Cell(atlas, sprite.id, direction * sprite.frames + f, colour);
            }
            return grid;
        }

        private static Texture2D[][] Turret(Color32 colour, bool allFacings)
        {
            ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Buildings);
            AtlasSprite head = atlas?.Data.sprites.Find(s => s.id == ArtManifest.TurretHead);
            AtlasSprite body = atlas?.Data.sprites.Find(s => s.id == nameof(BuildingType.GunTurret));
            if (head == null || body == null) return null;
            int facings = allFacings ? head.directions : 1;
            var grid = new Texture2D[facings][];
            for (int k = 0; k < facings; k++)
            {
                int direction = (k + head.directions - 1) % head.directions;
                grid[k] = new Texture2D[head.frames];
                for (int f = 0; f < head.frames; f++)
                {
                    var key = ("Turret" + direction, f, 0, colour);
                    if (!Cache.TryGetValue(key, out Texture2D t))
                        Cache[key] = t = Over(atlas, atlas.Data.frames[body.first + f % body.frames], atlas.Data.frames[head.first + direction * head.frames + f], colour);
                    grid[k][f] = t;
                }
            }
            return grid;
        }

        private static Texture2D Cell(string atlasName, string id, int cell, Color32 colour)
        {
            var key = (id, cell, 0, colour);
            if (Cache.TryGetValue(key, out Texture2D cached) && cached != null) return cached;
            ArtAtlas atlas = ArtAtlas.Load(atlasName);
            AtlasSprite sprite = atlas?.Data.sprites.Find(s => s.id == id);
            if (sprite == null) return null;
            AtlasFrame frame = atlas.Data.frames[sprite.first + cell];
            Color[] pixels = Tinted(atlas, frame, colour);
            return Cache[key] = Make(frame.w, frame.h, pixels);
        }

        private static Texture2D Over(ArtAtlas atlas, AtlasFrame under, AtlasFrame over, Color32 colour)
        {
            // Lined up on their pivots, on a canvas that holds both.
            float left = Mathf.Max(under.pivotX, over.pivotX), bottom = Mathf.Max(under.pivotY, over.pivotY);
            int w = Mathf.CeilToInt(left + Mathf.Max(under.w - under.pivotX, over.w - over.pivotX));
            int h = Mathf.CeilToInt(bottom + Mathf.Max(under.h - under.pivotY, over.h - over.pivotY));
            var canvas = new Color[w * h];
            Stamp(canvas, w, h, Tinted(atlas, under, colour), under, Mathf.RoundToInt(left - under.pivotX), Mathf.RoundToInt(bottom - under.pivotY));
            Stamp(canvas, w, h, Tinted(atlas, over, colour), over, Mathf.RoundToInt(left - over.pivotX), Mathf.RoundToInt(bottom - over.pivotY));
            return Make(w, h, canvas);
        }

        private static void Stamp(Color[] canvas, int w, int h, Color[] pixels, AtlasFrame f, int x0, int y0)
        {
            for (int y = 0; y < f.h; y++)
                for (int x = 0; x < f.w; x++)
                {
                    Color c = pixels[y * f.w + x];
                    if (c.a > 0f && x0 + x < w && y0 + y < h) canvas[(y0 + y) * w + x0 + x] = c;
                }
        }

        private static Color[] Tinted(ArtAtlas atlas, AtlasFrame f, Color32 colour)
        {
            Color[] body = Read(atlas.Texture, atlas.Data, f);
            Color[] mask = atlas.Mask != null ? Read(atlas.Mask, atlas.Data, f) : null;
            Color team = colour;
            for (int i = 0; i < body.Length && mask != null; i++)
            {
                float a = body[i].a, m = mask[i].r;
                body[i] = Color.Lerp(body[i], new Color(Mathf.Clamp01(body[i].r * team.r * TeamGain), Mathf.Clamp01(body[i].g * team.g * TeamGain), Mathf.Clamp01(body[i].b * team.b * TeamGain)), m);
                body[i].a = a;
            }
            return body;
        }

        private static Color[] Read(Texture2D source, AtlasData data, AtlasFrame f)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(f.w, f.h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            rt.filterMode = FilterMode.Point;
            Graphics.Blit(source, rt, new Vector2((float)f.w / data.width, (float)f.h / data.height), new Vector2((float)f.x / data.width, (float)f.y / data.height));
            RenderTexture.active = rt;
            var read = new Texture2D(f.w, f.h, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, f.w, f.h), 0, 0, false);
            Color[] pixels = read.GetPixels();
            Object.Destroy(read);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            return pixels;
        }

        private static Texture2D Make(int w, int h, Color[] pixels)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Manual sprite" };
            texture.SetPixels(pixels);
            texture.Apply(false);
            return texture;
        }
    }
}
