using UnityEditor;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// Import settings for generated art (style guide): uncompressed, at <see cref="ArtStyle.PixelsPerUnit"/>.
    /// The atlases under <c>Assets/Resources/Art/</c> get mipmaps with trilinear filtering for smooth zoom-out
    /// (the mask in linear colour); everything else under <c>Assets/Art/</c> is point-filtered without mipmaps.
    /// </summary>
    public sealed class ArtImporter : AssetPostprocessor
    {
        public const string AtlasFolder = "Assets/Resources/Art/", SourceFolder = "Assets/Art/";

        /// <summary>True for textures this importer configures.</summary>
        public static bool Handles(string assetPath) => assetPath.StartsWith(AtlasFolder) || assetPath.StartsWith(SourceFolder);

        /// <summary>True for an atlas (mipmapped) rather than a source image.</summary>
        public static bool IsAtlas(string assetPath) => assetPath.StartsWith(AtlasFolder);

        /// <summary>True for a team mask (linear, no alpha transparency).</summary>
        public static bool IsMask(string assetPath) => assetPath.EndsWith("_mask.png");

        private void OnPreprocessTexture()
        {
            if (!Handles(assetPath)) return;
            var importer = (TextureImporter)assetImporter;
            bool atlas = IsAtlas(assetPath), mask = IsMask(assetPath);
            importer.textureType = TextureImporterType.Default;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.isReadable = false;
            importer.spritePixelsPerUnit = ArtStyle.PixelsPerUnit;
            importer.sRGBTexture = !mask;
            importer.alphaIsTransparency = !mask;
            importer.mipmapEnabled = atlas;
            importer.filterMode = atlas ? FilterMode.Trilinear : FilterMode.Point;
        }
    }
}
