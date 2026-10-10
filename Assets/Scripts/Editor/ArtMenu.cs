using System.IO;
using UnityEditor;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Generators;

namespace WAR2D.EditorTools
{
    /// <summary>Editor menu for the code-generated art (plan v0.8). The generators live in the runtime assembly; this only writes and imports their output.</summary>
    public static class ArtMenu
    {
        public const string PrototypeFolder = "Assets/Art/Prototype/a";
        /// <summary>The seed the committed prototype art is generated with.</summary>
        public const int PrototypeSeed = 1;

        /// <summary>
        /// Writes one sheet per prototype generator to <see cref="PrototypeFolder"/>: a row per direction,
        /// a column per frame. Same seed, same bytes.
        /// </summary>
        [MenuItem("WAR-2D/Art/Generate prototypes")]
        public static void GeneratePrototypes()
        {
            Directory.CreateDirectory(PrototypeFolder);
            foreach (ISpriteGenerator generator in PrototypeGenerators.All)
            {
                ArtEntry entry = PrototypeGenerators.Entry(generator.Id);
                PixelCanvas[] frames = generator.Generate(PrototypeSeed);
                Color32[] sheet = PixelCanvas.Sheet(frames, entry.Frames, out int w, out int h);
                File.WriteAllBytes(Path.Combine(PrototypeFolder, generator.Id + ".png"), Png.Encode(w, h, sheet));
            }
            AssetDatabase.Refresh();
            foreach (ISpriteGenerator generator in PrototypeGenerators.All) ApplyPixelImport(Path.Combine(PrototypeFolder, generator.Id + ".png"), PrototypeGenerators.TileSize);
            Debug.Log($"[Art] wrote {PrototypeGenerators.All.Length} prototype sheets to {PrototypeFolder}");
        }

        public const string AiPrototypeFolder = "Assets/Art/Prototype/b";

        /// <summary>
        /// Cleans the AI image tool's raw output (<c>b/raw</c>) to prototype (a)'s size and palette: the
        /// chroma-green background is keyed out, each subject cropped to a square around its content, and the
        /// result quantized with <see cref="PaletteQuantizer"/>. The terrain sheet is four tiles in a row.
        /// </summary>
        [MenuItem("WAR-2D/Art/Clean AI prototypes")]
        public static void CleanAiPrototypes()
        {
            int tile = PrototypeGenerators.TileSize;
            Color32[] palette = PrototypeGenerators.Palette;
            foreach (string id in new[] { "Tank", "Miner" })
            {
                Color32[] raw = Load(Path.Combine(AiPrototypeFolder, "raw", id + ".png"), out int w, out int h);
                ChromaKey(raw);
                Color32[] square = CropSquare(raw, w, h, ContentBounds(raw, w, h), out int side);
                Write(Path.Combine(AiPrototypeFolder, id + ".png"), tile, tile, PaletteQuantizer.Quantize(square, side, side, tile, tile, palette));
            }

            Color32[] terrain = Load(Path.Combine(AiPrototypeFolder, "raw", "Terrain.png"), out int tw, out int th);
            string[] tiles = { "Ground", "Wall", "Gem", "Border" };
            for (int i = 0; i < tiles.Length; i++)
            {
                int x0 = i * tw / tiles.Length, x1 = (i + 1) * tw / tiles.Length;
                int side = Mathf.Min(x1 - x0, th);
                var cell = new RectInt(x0 + (x1 - x0 - side) / 2, (th - side) / 2, side, side);
                Color32[] square = CropSquare(terrain, tw, th, cell, out int s);
                Write(Path.Combine(AiPrototypeFolder, tiles[i] + ".png"), tile, tile, PaletteQuantizer.Quantize(square, s, s, tile, tile, palette));
            }
            AssetDatabase.Refresh();
            foreach (string id in new[] { "Tank", "Miner", "Ground", "Wall", "Gem", "Border" })
                ApplyPixelImport(Path.Combine(AiPrototypeFolder, id + ".png"), tile);
            Debug.Log($"[Art] cleaned the AI prototypes into {AiPrototypeFolder}");
        }

        private static Color32[] Load(string path, out int width, out int height)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path))) throw new IOException($"can't read {path}");
                width = texture.width;
                height = texture.height;
                return texture.GetPixels32();
            }
            finally { Object.DestroyImmediate(texture); }
        }

        private static void Write(string path, int width, int height, Color32[] pixels) =>
            File.WriteAllBytes(path, Png.Encode(width, height, pixels));

        /// <summary>Makes the requested #00FF00 background (and its anti-aliased fringe) transparent.</summary>
        private static void ChromaKey(Color32[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                if (c.g > 150 && c.g > c.r + 60 && c.g > c.b + 60) pixels[i] = new Color32(0, 0, 0, 0);
            }
        }

        private static RectInt ContentBounds(Color32[] pixels, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * width + x].a < 128) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return maxX < 0 ? new RectInt(0, 0, width, height) : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>The smallest square holding <paramref name="r"/>, centred on it (outside the image is transparent).</summary>
        private static Color32[] CropSquare(Color32[] pixels, int width, int height, RectInt r, out int side)
        {
            side = Mathf.Max(r.width, r.height);
            int ox = r.x - (side - r.width) / 2, oy = r.y - (side - r.height) / 2;
            var square = new Color32[side * side];
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int sx = ox + x, sy = oy + y;
                square[y * side + x] = (uint)sx < (uint)width && (uint)sy < (uint)height ? pixels[sy * width + sx] : new Color32(0, 0, 0, 0);
            }
            return square;
        }

        /// <summary>Pixel-art import: point filter, no compression, no mipmaps, the given pixels per unit.</summary>
        public static void ApplyPixelImport(string assetPath, int pixelsPerUnit)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer)) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.spritePixelsPerUnit = pixelsPerUnit;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }
    }
}
