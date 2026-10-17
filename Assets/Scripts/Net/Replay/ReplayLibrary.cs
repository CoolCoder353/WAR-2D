using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace WAR2D.Net.Replay
{
    public sealed class ReplaySummary
    {
        public string Path;
        public DateTime Utc;
        public ReplayHeader Header;
        public long Bytes;
        /// <summary>The match length from the replay's info file, or -1 when unknown.</summary>
        public float DurationSeconds = -1f;
    }

    /// <summary>The local Replays folder (<c>persistentDataPath/Replays</c>).</summary>
    public static class ReplayLibrary
    {
        public static string Folder => System.IO.Path.Combine(Application.persistentDataPath, "Replays");

        /// <summary>Every readable replay, newest first (unreadable files are skipped, not deleted).</summary>
        public static IReadOnlyList<ReplaySummary> List()
        {
            if (!Directory.Exists(Folder)) return Array.Empty<ReplaySummary>();
            var list = new List<ReplaySummary>();
            foreach (string path in Directory.GetFiles(Folder, "*" + ReplayFile.Extension))
            {
                try
                {
                    using FileStream f = File.OpenRead(path);
                    if (!ReplayReader.TryOpen(f, out ReplayReader r, out _)) continue;
                    using (r) list.Add(new ReplaySummary { Path = path, Utc = File.GetLastWriteTimeUtc(path), Header = r.Header, Bytes = new FileInfo(path).Length, DurationSeconds = ReadDuration(path) });
                }
                catch (IOException) { }
            }
            return list.OrderByDescending(s => s.Utc).ToList();
        }

        /// <summary>The info file beside a replay (its length, so the list needn't read the whole file).</summary>
        public static string InfoPath(string path) => path + ".info";

        /// <summary>Writes a replay's info file (best effort).</summary>
        public static void WriteInfo(string path, float durationSeconds)
        {
            try { File.WriteAllText(InfoPath(path), durationSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static float ReadDuration(string path)
        {
            try
            {
                string text = File.Exists(InfoPath(path)) ? File.ReadAllText(InfoPath(path)) : null;
                return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 0f && s < 1e7f ? s : -1f;
            }
            catch (IOException) { return -1f; }
        }

        public static void Delete(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            if (!full.StartsWith(System.IO.Path.GetFullPath(Folder) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || !full.EndsWith(ReplayFile.Extension)) return; // only our own files
            if (File.Exists(full)) File.Delete(full);
            if (File.Exists(InfoPath(full))) File.Delete(InfoPath(full));
        }

        /// <summary>Deletes the oldest replays in the Replays folder beyond <paramref name="keep"/>.</summary>
        public static void Prune(int keep) => Prune(Folder, keep);

        /// <summary>Deletes the oldest replays in <paramref name="folder"/> beyond <paramref name="keep"/>.</summary>
        public static void Prune(string folder, int keep)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string old in Directory.GetFiles(folder, "*" + ReplayFile.Extension).OrderByDescending(File.GetLastWriteTimeUtc).Skip(keep))
                try { File.Delete(old); if (File.Exists(InfoPath(old))) File.Delete(InfoPath(old)); } catch (IOException) { }
        }
    }
}
