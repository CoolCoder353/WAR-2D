using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Audio;
using WAR2D.Audio.Chiptune;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// The command-line builds (asset-generation spec §3.2), behind <c>tools/art-build.sh</c> and
    /// <c>tools/audio-build.sh</c>: builds the art, the sounds or the music, all of it or the ids a glob matches, and
    /// writes a change report (each sprite, sound or track whose hash differs from <c>Assets/Art/hashes.json</c>) with
    /// before and after files. With <c>write</c> false nothing under <c>Assets</c> changes: the way to see what a change
    /// would do before the owner has approved it.
    /// </summary>
    public static class AssetCli
    {
        /// <summary>The scale of the before and after sheets.</summary>
        public const int SheetScale = 4;

        /// <summary>Batch-mode entry: <c>-assetKind art|sfx|music -assetOnly &lt;glob&gt; -assetWrite true|false -assetOut &lt;folder&gt;</c>.</summary>
        public static void FromArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string name, string fallback)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }
            Run(Arg("-assetKind", "art"), Arg("-assetOnly", "*"), Arg("-assetWrite", "false") == "true", Arg("-assetOut", "TestResults/assets"), Arg("-assetShow", ""));
        }

        /// <summary>
        /// Builds <paramref name="kind"/> (art, sfx or music) and writes <c>report.txt</c> in <paramref name="outFolder"/>.
        /// Sprites whose id matches <paramref name="show"/> get their pictures written whether they changed or not.
        /// </summary>
        public static void Run(string kind, string only, bool write, string outFolder, string show = "")
        {
            Directory.CreateDirectory(outFolder);
            var report = new StringBuilder();
            try
            {
                Func<string, bool> match = Glob(only);
                switch (kind)
                {
                    case "art": Art(match, write, outFolder, report, string.IsNullOrEmpty(show) ? (_ => false) : Glob(show)); break;
                    case "sfx": Sounds(match, write, outFolder, report, false); break;
                    case "music": Sounds(match, write, outFolder, report, true); break;
                    default: throw new ArgumentException($"unknown kind '{kind}' (art, sfx or music)");
                }
                if (write) AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                report.AppendLine($"FAILED: {e.Message}");
                Debug.LogException(e);
            }
            File.WriteAllText(Path.Combine(outFolder, "report.txt"), report.ToString());
        }

        /// <summary>A matcher for a glob of <c>*</c> and <c>?</c> over a whole id (case-sensitive).</summary>
        public static Func<string, bool> Glob(string glob)
        {
            var regex = new Regex("^" + Regex.Escape(glob ?? "*").Replace("\\*", ".*").Replace("\\?", ".") + "$");
            return id => regex.IsMatch(id);
        }

        private static void Art(Func<string, bool> match, bool write, string outFolder, StringBuilder report, Func<string, bool> show)
        {
            ArtBuild.BuiltAtlas ground = ArtBuild.LoadBuilt(ArtAtlas.Tiles);
            AssetHashes hashes = AssetHashes.Load();
            int rendered = 0, kept = 0, changed = 0;
            var changedFiles = new List<string>();
            foreach (var (name, kind) in ArtBuild.Atlases)
            {
                ArtBuild.BuiltAtlas old = ArtBuild.LoadBuilt(name);
                ArtBuild.BuiltAtlas built = ArtBuild.Render(name, kind, e => match(e.Id), old);
                rendered += built.Rendered;
                kept += built.Sprites.Count - built.Rendered;
                foreach (var pair in built.Sprites)
                {
                    hashes.sprites.TryGetValue(pair.Key, out string recorded);
                    bool same = recorded == pair.Value;
                    if (same && !show(pair.Key)) continue;
                    if (!same) changed++;
                    if (old != null && !same) Pictures(old, ground, pair.Key, "before", outFolder);
                    Pictures(built, ground, pair.Key, "after", outFolder);
                    report.AppendLine(same ? $"shown: {name}/{pair.Key}" : $"changed: {name}/{pair.Key}" + (recorded == null ? " (new)" : ""));
                }
                foreach (var (file, bytes) in built.Files)
                {
                    string path = Path.Combine(ArtBuild.OutputFolder, file);
                    if (File.Exists(path) && AssetHashes.Sha(File.ReadAllBytes(path)) == AssetHashes.Sha(bytes)) continue;
                    changedFiles.Add(file);
                    if (write) File.WriteAllBytes(path, bytes);
                }
                if (write) built.AddTo(hashes);
            }
            if (write) hashes.Save();
            report.Insert(0, $"art: {rendered} sprites rendered, {kept} taken from the built atlases\n");
            Summary(report, changed, "sprite", changedFiles, write, outFolder);
        }

        /// <summary>Writes sprite <paramref name="id"/>'s cells at <see cref="SheetScale"/> and, on the ground, its contact sheet.</summary>
        private static void Pictures(ArtBuild.BuiltAtlas atlas, ArtBuild.BuiltAtlas ground, string id, string label, string outFolder)
        {
            if (!(atlas.Sheet(id, SheetScale, out int w, out int h) is Color32[] cells)) return;
            File.WriteAllBytes(Path.Combine(outFolder, $"{id}.{label}.png"), Png.Encode(w, h, cells));
            if (ground == null || !(ContactSheet.Render(atlas, ground, id, out int cw, out int ch) is Color32[] contact)) return;
            File.WriteAllBytes(Path.Combine(outFolder, $"{id}.{label}.contact.png"), Png.Encode(cw, ch, contact));
        }

        private static void Sounds(Func<string, bool> match, bool write, string outFolder, StringBuilder report, bool music)
        {
            AssetHashes hashes = AssetHashes.Load();
            SortedDictionary<string, string> table = music ? hashes.tracks : hashes.sounds;
            string source = music ? AudioBuild.MusicSource : AudioBuild.SfxSource, output = music ? AudioBuild.MusicOutput : AudioBuild.SfxOutput;
            int built = 0, changed = 0;
            var changedFiles = new List<string>();
            foreach (string spec in Directory.GetFiles(source, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(spec);
                if (!match(id)) continue;
                built++;
                byte[] bytes = music ? AudioBuild.RenderSong(AudioBuild.ReadSong(spec)) : AudioBuild.RenderSfx(AudioBuild.ReadSfx(spec));
                string hash = AssetHashes.Sha(bytes), path = $"{output}/{id}.wav";
                table.TryGetValue(id, out string recorded);
                if (recorded == hash && File.Exists(path) && AssetHashes.Sha(File.ReadAllBytes(path)) == hash) continue;
                changed++;
                changedFiles.Add(id + ".wav");
                report.AppendLine($"changed: {id}" + (recorded == null ? " (new)" : ""));
                if (File.Exists(path)) File.Copy(path, Path.Combine(outFolder, $"{id}.before.wav"), true);
                File.WriteAllBytes(Path.Combine(outFolder, $"{id}.after.wav"), bytes);
                if (!write) continue;
                File.WriteAllBytes(path, bytes);
                table[id] = hash;
            }
            if (write) hashes.Save();
            report.Insert(0, $"{(music ? "music" : "sfx")}: {built} built\n");
            Summary(report, changed, music ? "track" : "sound", changedFiles, write, outFolder);
        }

        private static void Summary(StringBuilder report, int changed, string noun, List<string> files, bool write, string outFolder)
        {
            if (changed == 0 && files.Count == 0)
            {
                report.AppendLine("nothing changed");
                return;
            }
            report.AppendLine($"{changed} {noun}{(changed == 1 ? "" : "s")} changed; before and after in {outFolder}");
            if (files.Count > 0) report.AppendLine($"files {(write ? "written" : "that would change")}: {string.Join(", ", files)}");
            if (!write) report.AppendLine("nothing was written under Assets (--check)");
        }
    }
}
