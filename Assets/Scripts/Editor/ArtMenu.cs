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
