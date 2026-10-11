using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using WAR2D.Art;
using WAR2D.EditorTools;

/// <summary>The generated atlases (<c>WAR-2D/Art/Build all</c>), the team key split and the import settings.</summary>
public class ArtImportTests
{
    private static Color32[] ReadPng(string path, out int w, out int h)
    {
        var t = new Texture2D(2, 2);
        try
        {
            Assert.IsTrue(t.LoadImage(File.ReadAllBytes(path)), path);
            w = t.width;
            h = t.height;
            return t.GetPixels32();
        }
        finally { Object.DestroyImmediate(t); }
    }

    private static IEnumerable<string> Pngs() =>
        new[] { ArtImporter.AtlasFolder, ArtImporter.SourceFolder }.SelectMany(f => Directory.GetFiles(f, "*.png")).Select(p => p.Replace('\\', '/'));

    [Test]
    public void EveryManifestIdIsInItsAtlasWithAllItsFrames()
    {
        foreach (var (name, kind) in ArtBuild.Atlases)
        {
            ArtAtlas atlas = ArtAtlas.Load(name);
            Assert.IsNotNull(atlas, $"{name} isn't built: run WAR-2D/Art/Build all");
            Assert.AreEqual(ArtStyle.PixelsPerUnit, atlas.Data.pixelsPerUnit);
            foreach (ArtEntry e in ArtManifest.All.Where(e => e.Kind == kind))
            {
                Assert.AreEqual(e.Frames * e.Directions, atlas.FrameCount(e.Id), $"{name}: {e}");
                Sprite s = atlas.Sprite(e.Id, atlas.FrameCount(e.Id) - 1);
                Assert.IsNotNull(s, e.Id);
                Assert.AreEqual(e.WidthTiles, s.rect.width / ArtStyle.PixelsPerUnit, 1e-3, $"{e.Id} is {e.WidthTiles} tiles wide");
            }
        }
    }

    [Test]
    public void AtlasesArePowerOfTwoWithFramesInBounds()
    {
        foreach (var (name, _) in ArtBuild.Atlases)
        {
            ArtAtlas atlas = ArtAtlas.Load(name);
            Assert.IsNotNull(atlas, name);
            Assert.AreEqual((atlas.Data.width, atlas.Data.height), (atlas.Texture.width, atlas.Texture.height), name);
            Assert.IsTrue(Mathf.IsPowerOfTwo(atlas.Texture.width) && Mathf.IsPowerOfTwo(atlas.Texture.height), name);
            Assert.AreEqual((atlas.Texture.width, atlas.Texture.height), (atlas.Mask.width, atlas.Mask.height), name);
            foreach (Vector4 r in atlas.FrameRects)
                Assert.IsTrue(r.x >= 0 && r.y >= 0 && r.x + r.z <= atlas.Texture.width && r.y + r.w <= atlas.Texture.height, $"{name} {r}");
        }
    }

    [Test]
    public void SplitMasksExactlyTheTeamKey()
    {
        Color32 floor = ArtStyle.Palette[5];
        var source = new[] { floor, ArtStyle.TeamKeyRamp[0], new Color32(0, 0, 0, 0), ArtStyle.TeamKeyRamp[4], ArtStyle.TeamKeyRamp[2] };
        Color32[] colour = ArtStyle.SplitTeamKey(source, out Color32[] mask);
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255, 255 }, mask.Select(m => m.r).ToArray());
        CollectionAssert.AreEqual(new[] { floor, ArtStyle.TeamGrey[0], source[2], ArtStyle.TeamGrey[4], ArtStyle.TeamGrey[2] }, colour);
    }

    [Test]
    public void AtlasPixelsArePaletteTeamGreyOrShadow()
    {
        var allowed = new HashSet<Color32>(ArtStyle.Palette.Concat(ArtStyle.TeamGrey)) { new Color32(0, 0, 0, ArtStyle.ShadowAlpha) };
        foreach (var (name, _) in ArtBuild.Atlases)
        {
            Color32[] colour = ReadPng($"{ArtImporter.AtlasFolder}{name}.png", out _, out _);
            Color32[] mask = ReadPng($"{ArtImporter.AtlasFolder}{name}_mask.png", out _, out _);
            int team = 0;
            for (int i = 0; i < colour.Length; i++)
            {
                Color32 c = colour[i];
                if (c.a == 0) continue;
                Assert.IsTrue(allowed.Contains(c), $"{name} pixel {i}: {c}");
                if (mask[i].r == 0) continue;
                team++;
                CollectionAssert.Contains(ArtStyle.TeamGrey, c, $"{name} pixel {i} is masked but not a team grey");
            }
            Assert.Greater(team, 0, $"{name} has no team pixels");
        }
    }

    [Test]
    public void ImporterSettingsApplyToEveryArtTexture()
    {
        List<string> pngs = Pngs().ToList();
        Assert.Greater(pngs.Count, 3);
        foreach (string path in pngs)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.IsNotNull(importer, path);
            bool atlas = ArtImporter.IsAtlas(path);
            Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression, path);
            Assert.AreEqual(ArtStyle.PixelsPerUnit, importer.spritePixelsPerUnit, 1e-3, path);
            Assert.AreEqual(atlas, importer.mipmapEnabled, path);
            Assert.AreEqual(atlas ? FilterMode.Trilinear : FilterMode.Point, importer.filterMode, path);
            Assert.AreEqual(!ArtImporter.IsMask(path), importer.sRGBTexture, path);
        }
    }
}
