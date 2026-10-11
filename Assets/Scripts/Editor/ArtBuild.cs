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
        public const int Seed = 1;

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
            foreach (var (atlas, kind) in Atlases) Build(atlas, kind);
            AssetDatabase.Refresh();
            Debug.Log($"[Art] built {Atlases.Length} atlases in {OutputFolder}");
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

        private static void Build(string name, ArtKind kind)
        {
            var data = new AtlasData { pixelsPerUnit = ArtStyle.PixelsPerUnit };
            var frames = new List<Frame>();
            var rects = new List<(string id, int frame, int w, int h)>();
            var cache = new Dictionary<string, Frame>();
            foreach (ArtEntry e in ArtManifest.All)
            {
                if (e.Kind != kind) continue;
                int cells = e.Frames * e.Directions;
                data.sprites.Add(new AtlasSprite { id = e.Id, first = frames.Count, frames = e.Frames, directions = e.Directions });
                for (int i = 0; i < cells; i++)
                {
                    if (kind == ArtKind.Effect)
                    {
                        Frame fx = Effect(e, i);
                        frames.Add(fx);
                        rects.Add((e.Id, i, fx.W, fx.H));
                        continue;
                    }
                    var (key, make, lift) = Generator(e, i);
                    if (!cache.TryGetValue(key, out Frame f)) cache[key] = f = Finish(make(), lift, kind != ArtKind.Tile);
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

            string path = Path.Combine(OutputFolder, name);
            File.WriteAllBytes(path + ".png", Png.Encode(pack.Width, pack.Height, colour));
            File.WriteAllBytes(path + "_mask.png", Png.Encode(pack.Width, pack.Height, mask));
            File.WriteAllText(path + ".json", JsonUtility.ToJson(data, true) + "\n");
            Debug.Log($"[Art] {name}: {frames.Count} frames in {pack.Width}×{pack.Height}");
        }

        /// <summary>The generator for cell <paramref name="cell"/> of <paramref name="e"/>: a cache key, the canvas and its 3/4 lift.</summary>
        private static (string key, Func<LitCanvas> make, float lift) Generator(ArtEntry e, int cell)
        {
            int frame = cell % e.Frames, direction = cell / e.Frames;
            switch (e.Id)
            {
                case nameof(UnitType.Tank):
                    return ($"Tank {direction} {frame}", () => RenderGen.Tank(direction * Mathf.PI / 4f, frame), 0f);
                case nameof(BuildingType.Base):
                    return ("Base", RenderGen.Base, RenderGen.Lift);
                case nameof(BuildingType.SmallUnitSpawner):
                    return ($"Spawner {frame}", () => RenderGen.Spawner(frame), RenderGen.Lift);
                case nameof(BuildingType.Miner):
                    return ($"Miner {frame}", () => RenderGen.Miner(frame), RenderGen.Lift);
                case nameof(TileType.Ground):
                    return ($"Floor {frame}", () => RenderGen.Floor(Seed, frame), 0f);
                case nameof(TileType.Wall):
                    return ($"Rock {frame}", () => RenderGen.Rock(Seed, frame), 0f);
                case nameof(TileType.Gem):
                    // Variant-major: 4 variants × 16 autotile masks.
                    return ($"Gem {frame}", () => RenderGen.Gem(Seed, frame / ArtManifest.AutotileVariants, frame % ArtManifest.AutotileVariants), 0f);
                case nameof(TileType.Border):
                    return ("Rock 15", () => RenderGen.Rock(Seed, 15), 0f);
                default:
                    return ($"Placeholder {Math.Max(e.WidthTiles, e.HeightTiles)}", () => RenderGen.Placeholder(Math.Max(e.WidthTiles, e.HeightTiles)), 0f);
            }
        }

        /// <summary>An effect frame from <see cref="EffectGen"/> (already palette pixel art), split into colour and mask.</summary>
        private static Frame Effect(ArtEntry e, int cell)
        {
            EffectFrame? made = EffectGen.For(e.Id, cell, e.Frames);
            if (made == null) throw new InvalidOperationException($"{e.Id} has no effect generator (EffectGen.For)");
            EffectFrame f = made.Value;
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
