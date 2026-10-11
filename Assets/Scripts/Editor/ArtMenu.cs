using UnityEditor;
using UnityEngine;

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
    }
}
