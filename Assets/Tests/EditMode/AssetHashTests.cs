using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using WAR2D.EditorTools;

/// <summary>
/// The golden hashes (asset-generation spec §3.6): what the art generators make now, and every built atlas, sound and
/// track on disk, is what <c>Assets/Art/hashes.json</c> records. A change to a generator or a shared helper fails here
/// with the name of each sprite it moved, so no built asset changes unseen. After an owner-approved change, the builds
/// (or <c>WAR-2D/Art/Write hashes</c>) rewrite the file.
/// </summary>
public class AssetHashTests
{
    private static void Compare(string what, IDictionary<string, string> now, IDictionary<string, string> recorded, List<string> problems)
    {
        foreach (var pair in now)
        {
            if (!recorded.TryGetValue(pair.Key, out string hash)) problems.Add($"{what} {pair.Key}: not in {AssetHashes.FilePath}");
            else if (hash != pair.Value) problems.Add($"{what} {pair.Key}: changed");
        }
        problems.AddRange(recorded.Keys.Where(id => !now.ContainsKey(id)).Select(id => $"{what} {id}: recorded but no longer made"));
    }

    /// <summary>Renders every atlas (about two minutes), so it is left out of <c>tools/check.sh --fast</c>.</summary>
    [Test, Category("Slow")]
    public void TheGeneratorsMakeTheRecordedSpritesAndAtlases()
    {
        AssetHashes recorded = AssetHashes.Load();
        var made = new AssetHashes();
        foreach (var (atlas, kind) in ArtBuild.Atlases) ArtBuild.Render(atlas, kind).AddTo(made);
        var problems = new List<string>();
        Compare("sprite", made.sprites, recorded.sprites, problems);
        Compare("atlas", made.atlases, recorded.atlases, problems);
        Assert.IsEmpty(problems, string.Join("\n", problems));
    }

    [Test]
    public void TheBuiltFilesAreTheRecordedOnes()
    {
        AssetHashes recorded = AssetHashes.Load();
        var disk = new AssetHashes();
        foreach (string file in recorded.atlases.Keys)
        {
            string path = Path.Combine(ArtBuild.OutputFolder, file);
            disk.atlases[file] = File.Exists(path) ? AssetHashes.Sha(File.ReadAllBytes(path)) : "missing";
        }
        AssetHashes.HashWavs(disk.sounds, AudioBuild.SfxOutput);
        AssetHashes.HashWavs(disk.tracks, AudioBuild.MusicOutput);
        var problems = new List<string>();
        Assert.IsNotEmpty(recorded.atlases, AssetHashes.FilePath + " records no atlas");
        Compare("atlas", disk.atlases, recorded.atlases, problems);
        Compare("sound", disk.sounds, recorded.sounds, problems);
        Compare("track", disk.tracks, recorded.tracks, problems);
        Assert.IsEmpty(problems, string.Join("\n", problems));
    }
}
