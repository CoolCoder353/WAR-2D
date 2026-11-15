using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// The golden hashes (asset-generation spec §3.6), kept in <c>Assets/Art/hashes.json</c> and written by the art,
    /// sound and music builds: a SHA-256 per sprite (its finished frames, before packing), per atlas file, per built
    /// sound and per built track. <c>AssetHashTests</c> fail, naming each asset, when what the generators make or what
    /// is on disk differs from the file, so a helper change that moves a pixel or a sample can't land unseen.
    /// </summary>
    public sealed class AssetHashes
    {
        public const string FilePath = "Assets/Art/hashes.json";

        public SortedDictionary<string, string> sprites = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, string> atlases = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, string> sounds = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, string> tracks = new SortedDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The file's hashes, or an empty set when there is no file yet.</summary>
        public static AssetHashes Load() =>
            File.Exists(FilePath) ? JsonConvert.DeserializeObject<AssetHashes>(File.ReadAllText(FilePath)) ?? new AssetHashes() : new AssetHashes();

        /// <summary>Writes the file (keys sorted, so the same hashes give the same bytes).</summary>
        public void Save() => File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented).Replace("\r\n", "\n") + "\n");

        /// <summary>The SHA-256 of <paramref name="bytes"/> in lower-case hex.</summary>
        public static string Sha(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Replaces <paramref name="table"/> with the hash of every <c>.wav</c> in <paramref name="folder"/>, by file name without extension.</summary>
        public static void HashWavs(SortedDictionary<string, string> table, string folder)
        {
            table.Clear();
            foreach (string path in Directory.GetFiles(folder, "*.wav"))
                table[Path.GetFileNameWithoutExtension(path)] = Sha(File.ReadAllBytes(path));
        }
    }
}
