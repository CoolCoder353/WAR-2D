using UnityEditor;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.EditorTools
{
    /// <summary>Shared editor helpers for the code-generated art (plan v0.8).</summary>
    public static class ArtMenu
    {
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

        /// <summary>Writes <c>Assets/Art/palette.png</c>: <see cref="ArtStyle.Palette"/> at 1 px per colour.</summary>
        [MenuItem("WAR-2D/Art/Write palette")]
        public static void WritePalette()
        {
            const string path = "Assets/Art/palette.png";
            System.IO.Directory.CreateDirectory("Assets/Art");
            System.IO.File.WriteAllBytes(path, Png.Encode(ArtStyle.Palette.Length, 1, ArtStyle.Palette));
            AssetDatabase.ImportAsset(path);
            ApplyPixelImport(path, 1);
        }
    }
}
