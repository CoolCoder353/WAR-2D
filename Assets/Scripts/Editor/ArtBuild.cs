using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;
using WAR2D.Art.Style;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// Builds the art (plan v0.8 Tasks 6 and 11): renders every unit, building and tile in <see cref="ArtManifest"/>
    /// with <see cref="RenderGen"/>, finishes it as pixel art (<see cref="PixelFinish.FinishTeam"/>), splits the
    /// team key into the mask and packs the frames into <c>Assets/Resources/Art/{Units,Buildings,Tiles}</c>
    /// (<c>.png</c>, <c>_mask.png</c>, <c>.json</c>); effects come from <see cref="EffectGen"/> into
    /// <c>Effects</c>. Units, buildings and tiles with no generator yet (the v0.7 kit) get
    /// <see cref="RenderGen.Placeholder"/>. Icon and UI atlases come with plan Task 12.
    /// Deterministic: the same code writes the same bytes.
    /// </summary>
    public static class ArtBuild
    {
        public const string OutputFolder = "Assets/Resources/Art";
        public const string PalettePath = "Assets/Art/palette.png";

        /// <summary>The map seed the terrain variants are drawn with.</summary>
        public const int Seed = ArtManifest.Seed;

        /// <summary>Transparent pixels between frames (mipmaps bleed less with two).</summary>
        public const int Padding = 2;

        // Team parts render in two key colours; pixels whose colour changes between them are team pixels.
        private static readonly Color KeyA = new Color(0.64f, 0f, 0.64f), KeyB = new Color(0f, 0.64f, 0f);

        /// <summary>Which atlas each kind goes in.</summary>
        public static readonly (string atlas, ArtKind kind)[] Atlases =
        {
            (ArtAtlas.Units, ArtKind.Unit), (ArtAtlas.Buildings, ArtKind.Building), (ArtAtlas.Tiles, ArtKind.Tile),
            (ArtAtlas.Effects, ArtKind.Effect),
        };

        [MenuItem("WAR-2D/Art/Build all")]
        public static void BuildAll()
        {
            Directory.CreateDirectory(OutputFolder);
            WritePalette();
            AssetHashes hashes = AssetHashes.Load();
            hashes.sprites.Clear();
            hashes.atlases.Clear();
            foreach (var (atlas, kind) in Atlases)
            {
                BuiltAtlas built = Render(atlas, kind);
                foreach (var (file, bytes) in built.Files) File.WriteAllBytes(Path.Combine(OutputFolder, file), bytes);
                built.AddTo(hashes);
            }
            hashes.Save();
            AssetDatabase.Refresh();
            Debug.Log($"[Art] built {Atlases.Length} atlases in {OutputFolder}");
        }

        /// <summary>
        /// Writes <c>Assets/Art/hashes.json</c> from what the generators make now and the built sounds and tracks on disk,
        /// without building anything: for adopting the file, or after an approved change was built another way.
        /// </summary>
        [MenuItem("WAR-2D/Art/Write hashes")]
        public static void WriteHashes()
        {
            var hashes = new AssetHashes();
            foreach (var (atlas, kind) in Atlases) Render(atlas, kind).AddTo(hashes);
            AssetHashes.HashWavs(hashes.sounds, AudioBuild.SfxOutput);
            AssetHashes.HashWavs(hashes.tracks, AudioBuild.MusicOutput);
            hashes.Save();
            AssetDatabase.ImportAsset(AssetHashes.FilePath);
            Debug.Log($"[Art] wrote {AssetHashes.FilePath}");
        }

        /// <summary>Writes <c>Assets/Art/palette.png</c>: <see cref="ArtStyle.Palette"/> at 1 px per colour.</summary>
        [MenuItem("WAR-2D/Art/Write palette")]
        public static void WritePalette()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PalettePath));
            File.WriteAllBytes(PalettePath, Png.Encode(ArtStyle.Palette.Length, 1, ArtStyle.Palette));
            AssetDatabase.ImportAsset(PalettePath);
        }

        private struct Frame
        {
            public Color32[] Colour, Mask;
            public int W, H;
            public float PivotY;
        }

        /// <summary>One atlas as the build writes it: the three files' bytes and a hash per sprite.</summary>
        public sealed class BuiltAtlas
        {
            public string Name;
            public byte[] Png, Mask, Json;
            /// <summary>The atlas's layout and its colour and mask pixels (bottom-up rows).</summary>
            public AtlasData Data;
            public Color32[] Pixels, MaskPixels;
            /// <summary>How many sprites were rendered (the rest were taken from the atlas already built).</summary>
            public int Rendered;
            /// <summary>Sprite id to the SHA-256 of its finished frames (size, pivot, colour and mask), before packing.</summary>
            public readonly SortedDictionary<string, string> Sprites = new SortedDictionary<string, string>(StringComparer.Ordinal);

            /// <summary>The files in <see cref="OutputFolder"/>, by name.</summary>
            public (string file, byte[] bytes)[] Files => new[] { (Name + ".png", Png), (Name + "_mask.png", Mask), (Name + ".json", Json) };

            /// <summary>
            /// Sprite <paramref name="id"/>'s cells as one picture at <paramref name="scale"/>: a row per facing, a column per
            /// frame, on transparent. Null when the atlas has no such sprite.
            /// </summary>
            public Color32[] Sheet(string id, int scale, out int width, out int height)
            {
                width = height = 0;
                AtlasSprite sprite = Data.sprites.Find(s => s.id == id);
                if (sprite == null) return null;
                int cw = 0, ch = 0;
                for (int k = 0; k < sprite.frames * sprite.directions; k++)
                {
                    cw = Math.Max(cw, Data.frames[sprite.first + k].w);
                    ch = Math.Max(ch, Data.frames[sprite.first + k].h);
                }
                width = sprite.frames * (cw + 1) * scale;
                height = sprite.directions * (ch + 1) * scale;
                var sheet = new Color32[width * height];
                for (int k = 0; k < sprite.frames * sprite.directions; k++)
                {
                    AtlasFrame f = Data.frames[sprite.first + k];
                    int ox = (k % sprite.frames) * (cw + 1) * scale, oy = (sprite.directions - 1 - k / sprite.frames) * (ch + 1) * scale;
                    for (int y = 0; y < f.h * scale; y++)
                        for (int x = 0; x < f.w * scale; x++)
                            sheet[(oy + y) * width + ox + x] = Pixels[(f.y + y / scale) * Data.width + f.x + x / scale];
                }
                return sheet;
            }

            /// <summary>Records this atlas's sprites and files in <paramref name="hashes"/>.</summary>
            public void AddTo(AssetHashes hashes)
            {
                foreach (var pair in Sprites) hashes.sprites[pair.Key] = pair.Value;
                foreach (var (file, bytes) in Files) hashes.atlases[file] = AssetHashes.Sha(bytes);
            }
        }

        /// <summary>The atlas <paramref name="name"/> as it is built on disk, or null when it isn't there.</summary>
        public static BuiltAtlas LoadBuilt(string name)
        {
            string path = Path.Combine(OutputFolder, name);
            if (!File.Exists(path + ".png") || !File.Exists(path + "_mask.png") || !File.Exists(path + ".json")) return null;
            var built = new BuiltAtlas
            {
                Name = name,
                Png = File.ReadAllBytes(path + ".png"),
                Mask = File.ReadAllBytes(path + "_mask.png"),
                Json = File.ReadAllBytes(path + ".json"),
                Data = new AtlasData(),
            };
            JsonUtility.FromJsonOverwrite(File.ReadAllText(path + ".json"), built.Data);
            built.Pixels = Decode(built.Png, built.Data);
            built.MaskPixels = Decode(built.Mask, built.Data);
            return built;
        }

        private static Color32[] Decode(byte[] png, AtlasData data)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(png) || texture.width != data.width || texture.height != data.height)
                    throw new InvalidOperationException("a built atlas is not the size its JSON says");
                return texture.GetPixels32();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>The frames of <paramref name="e"/> as <paramref name="old"/> holds them, or null when it has none or their count or size differs.</summary>
        private static List<Frame> Kept(ArtEntry e, BuiltAtlas old)
        {
            AtlasSprite sprite = old?.Data.sprites.Find(s => s.id == e.Id);
            if (sprite == null || sprite.frames != e.Frames || sprite.directions != e.Directions) return null;
            var kept = new List<Frame>();
            for (int k = 0; k < e.Frames * e.Directions; k++)
            {
                AtlasFrame at = old.Data.frames[sprite.first + k];
                var f = new Frame { W = at.w, H = at.h, PivotY = at.pivotY, Colour = new Color32[at.w * at.h], Mask = new Color32[at.w * at.h] };
                for (int y = 0; y < at.h; y++)
                {
                    Array.Copy(old.Pixels, (at.y + y) * old.Data.width + at.x, f.Colour, y * at.w, at.w);
                    Array.Copy(old.MaskPixels, (at.y + y) * old.Data.width + at.x, f.Mask, y * at.w, at.w);
                }
                kept.Add(f);
            }
            return kept;
        }

        /// <summary>
        /// Renders and packs the atlas of <paramref name="kind"/> in memory; nothing is written. With <paramref name="render"/>
        /// and <paramref name="old"/>, only the sprites it accepts are rendered and the others' frames are taken from
        /// <paramref name="old"/> as they are (a sprite <paramref name="old"/> doesn't hold is rendered anyway).
        /// </summary>
        public static BuiltAtlas Render(string name, ArtKind kind, Func<ArtEntry, bool> render = null, BuiltAtlas old = null)
        {
            int renderedSprites = 0;
            var data = new AtlasData { pixelsPerUnit = ArtStyle.PixelsPerUnit };
            var frames = new List<Frame>();
            var rects = new List<(string id, int frame, int w, int h)>();
            var cache = new Dictionary<string, Frame>();
            foreach (ArtEntry e in ArtManifest.All)
            {
                if (e.Kind != kind) continue;
                int cells = e.Frames * e.Directions;
                data.sprites.Add(new AtlasSprite { id = e.Id, first = frames.Count, frames = e.Frames, directions = e.Directions });
                if (render != null && !render(e) && Kept(e, old) is List<Frame> kept)
                {
                    for (int i = 0; i < kept.Count; i++)
                    {
                        frames.Add(kept[i]);
                        rects.Add((e.Id, i, kept[i].W, kept[i].H));
                    }
                    continue;
                }
                renderedSprites++;
                for (int i = 0; i < cells; i++)
                {
                    if (e.Id == ArtManifest.FloorFeature || e.Id == ArtManifest.RockFeature)
                    {
                        Frame stamp = Feature(e, i, cache);
                        frames.Add(stamp);
                        rects.Add((e.Id, i, stamp.W, stamp.H));
                        continue;
                    }
                    if (kind == ArtKind.Effect)
                    {
                        Frame fx = Effect(e, i);
                        frames.Add(fx);
                        rects.Add((e.Id, i, fx.W, fx.H));
                        continue;
                    }
                    ArtCell cell = e.Cell(i);
                    // A sprite with no generator yet draws as a placeholder of its size.
                    string key = e.MakeLit != null ? e.KeyFor(cell) : $"Placeholder {Math.Max(e.WidthTiles, e.HeightTiles)}";
                    if (!cache.TryGetValue(key, out Frame f))
                    {
                        LitCanvas canvas = e.MakeLit != null ? e.MakeLit(cell) : RenderGen.Placeholder(Math.Max(e.WidthTiles, e.HeightTiles));
                        cache[key] = f = Finish(canvas, e.Lift, kind != ArtKind.Tile);
                    }
                    frames.Add(f);
                    rects.Add((e.Id, i, f.W, f.H));
                }
            }

            PackResult pack = AtlasPacker.Pack(rects, Padding);
            data.width = pack.Width;
            data.height = pack.Height;
            var colour = new Color32[pack.Width * pack.Height];
            var mask = new Color32[colour.Length];
            for (int i = 0; i < mask.Length; i++) mask[i] = new Color32(0, 0, 0, 255);
            for (int k = 0; k < frames.Count; k++)
            {
                Frame f = frames[k];
                Vector2Int at = pack.Positions[k];
                for (int y = 0; y < f.H; y++)
                {
                    Array.Copy(f.Colour, y * f.W, colour, (at.y + y) * pack.Width + at.x, f.W);
                    Array.Copy(f.Mask, y * f.W, mask, (at.y + y) * pack.Width + at.x, f.W);
                }
                data.frames.Add(new AtlasFrame { x = at.x, y = at.y, w = f.W, h = f.H, pivotX = f.W / 2f, pivotY = f.PivotY });
            }

            var built = new BuiltAtlas
            {
                Name = name,
                Data = data,
                Pixels = colour,
                MaskPixels = mask,
                Rendered = renderedSprites,
                Png = Png.Encode(pack.Width, pack.Height, colour),
                Mask = Png.Encode(pack.Width, pack.Height, mask),
                Json = new System.Text.UTF8Encoding(false).GetBytes(JsonUtility.ToJson(data, true) + "\n"),
            };
            foreach (AtlasSprite sprite in data.sprites)
                built.Sprites[sprite.id] = SpriteHash(frames, sprite.first, sprite.frames * sprite.directions);
            Debug.Log($"[Art] {name}: {frames.Count} frames in {pack.Width}×{pack.Height}");
            return built;
        }

        /// <summary>The SHA-256 of <paramref name="count"/> finished frames from <paramref name="first"/>: each one's size, pivot, colour and mask.</summary>
        private static string SpriteHash(List<Frame> frames, int first, int count)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                for (int k = first; k < first + count; k++)
                {
                    Frame f = frames[k];
                    writer.Write(f.W);
                    writer.Write(f.H);
                    writer.Write(f.PivotY);
                    foreach (Color32 c in f.Colour) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
                    foreach (Color32 c in f.Mask) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
                }
                writer.Flush();
                return AssetHashes.Sha(stream.ToArray());
            }
        }

        /// <summary>
        /// A terrain feature stamp: the feature finished on its plain base, transparent wherever it came out the same as
        /// the base without it, so the shader blends in only what the feature changed.
        /// </summary>
        private static Frame Feature(ArtEntry e, int cell, Dictionary<string, Frame> cache)
        {
            bool rock = e.Id == ArtManifest.RockFeature;
            string baseKey = rock ? "RockFeatureBase" : "FloorFeatureBase";
            if (!cache.TryGetValue(baseKey, out Frame plain))
                cache[baseKey] = plain = Finish(rock ? RenderGen.RockFeatureBase(Seed) : RenderGen.FloorFeatureBase(Seed), 0f, false);
            Frame f = Finish(e.MakeLit(e.Cell(cell)), 0f, false);
            for (int i = 0; i < f.Colour.Length; i++)
            {
                Color32 a = f.Colour[i], b = plain.Colour[i];
                if (a.r == b.r && a.g == b.g && a.b == b.b) f.Colour[i].a = 0;
            }
            return f;
        }

        /// <summary>An effect frame from <see cref="EffectGen"/> (already palette pixel art), split into colour and mask.</summary>
        private static Frame Effect(ArtEntry e, int cell)
        {
            if (e.MakeEffect == null) throw new InvalidOperationException($"{e.Id} has no effect generator (give its ArtEntry one with Fx)");
            EffectFrame f = e.MakeEffect(e.Cell(cell));
            Color32[] colour = ArtStyle.SplitTeamKey(f.Pixels, out Color32[] mask);
            return new Frame { Colour = colour, Mask = mask, W = f.Width, H = f.Height, PivotY = f.Height / 2f };
        }

        /// <summary>Renders <paramref name="c"/>, finds its team pixels and finishes it; sprites get the outline and shadow.</summary>
        private static Frame Finish(LitCanvas c, float lift, bool sprite)
        {
            int w = c.Width, h = c.Height;
            Color32[] a = c.Render(KeyA, shadow: false, lift: lift);
            Color32[] b = c.Render(KeyB, shadow: false, lift: lift);
            var team = new bool[a.Length];
            for (int i = 0; i < a.Length; i++)
                team[i] = Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b) > 24;
            Color32? outline = sprite ? StylePalettes.Charcoal.Colours[StylePalette.Outline] : (Color32?)null;
            Color32[] finished = PixelFinish.FinishTeam(a, team, w, h, ArtStyle.Palette, ArtStyle.TeamKeyRamp, outline);
            Color32[] colour = ArtStyle.SplitTeamKey(finished, out Color32[] mask);
            return new Frame { Colour = colour, Mask = mask, W = w, H = h, PivotY = c.PivotY };
        }
    }
}
