using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;

/// <summary>Every texture and sound the game loads from Resources comes from a generator (plan Task 16).</summary>
public class NoPlaceholderArtTests
{
    /// <summary>
    /// The pre-v0.8 tile sprites: still referenced by <c>Map_2</c>'s tilemap and palette (the map itself is drawn
    /// by the terrain shader), so they go when that scene is next edited in the editor, not by hand.
    /// </summary>
    private const string LegacyTileFolder = "Sprites/TileBases";

    [Test]
    public void EveryTextureIsAGeneratedAtlas()
    {
        // Our Resources folder only (packages ship their own Resources, such as Cinemachine's icon).
        var atlases = new HashSet<string> { ArtAtlas.Units, ArtAtlas.Buildings, ArtAtlas.Tiles, ArtAtlas.Effects };
        foreach (string path in Directory.GetFiles("Assets/Resources", "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".tga" && ext != ".psd" && ext != ".exr") continue;
            string unix = path.Replace('\\', '/');
            if (unix.Contains("/" + LegacyTileFolder + "/")) continue;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("_mask")) name = name.Substring(0, name.Length - 5);
            Assert.IsTrue(unix.StartsWith("Assets/Resources/" + ArtAtlas.Folder + "/") && atlases.Contains(name),
                $"'{unix}' has no generator: add it to ArtManifest and build it with WAR-2D/Art/Build all");
        }
    }

    [Test]
    public void EverySoundHasASourceSpec()
    {
        var sources = new HashSet<string>();
        foreach (string dir in new[] { "Assets/Art/Audio/sfx", "Assets/Art/Audio/music" })
            foreach (string path in Directory.GetFiles(dir, "*.json")) sources.Add(Path.GetFileNameWithoutExtension(path));
        int sounds = 0;
        foreach (string path in Directory.GetFiles("Assets/Resources", "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".wav" && ext != ".ogg" && ext != ".mp3") continue;
            sounds++;
            Assert.IsTrue(sources.Contains(Path.GetFileNameWithoutExtension(path)), $"'{path}' has no JSON under Assets/Art/Audio");
        }
        Assert.Greater(sounds, 0);
    }
}
