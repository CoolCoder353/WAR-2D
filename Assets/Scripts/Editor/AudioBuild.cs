using System.IO;
using UnityEditor;
using UnityEngine;
using WAR2D.Audio;
using WAR2D.Audio.Chiptune;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// <c>WAR-2D/Audio/Build SFX</c>: renders every <c>Assets/Art/Audio/sfx/*.json</c> with <see cref="Sfxr"/>
    /// to <c>Assets/Resources/Audio/Sfx/&lt;name&gt;.wav</c> (44.1 kHz mono 16-bit).
    /// </summary>
    public static class AudioBuild
    {
        public const string SfxSource = "Assets/Art/Audio/sfx", SfxOutput = "Assets/Resources/Audio/Sfx";
        public const string MusicSource = "Assets/Art/Audio/music", MusicOutput = "Assets/Resources/Audio/Music";
        public const int SampleRate = 44100;

        /// <summary>Music renders at half rate: the chip's voices sit well below 11 kHz, and the WAVs stay small in git.</summary>
        public const int MusicSampleRate = 22050;

        /// <summary>
        /// <c>WAR-2D/Audio/Build music</c>: composes every <c>Assets/Art/Audio/music/*.json</c> with
        /// <see cref="SongWriter"/> and renders it with <see cref="ChiptuneRenderer"/> to
        /// <c>Assets/Resources/Audio/Music/&lt;name&gt;.wav</c> (imported as streamed Vorbis).
        /// </summary>
        [MenuItem("WAR-2D/Audio/Build music")]
        public static void BuildMusic()
        {
            Directory.CreateDirectory(MusicOutput);
            int count = 0;
            foreach (string path in Directory.GetFiles(MusicSource, "*.json"))
            {
                var spec = JsonUtility.FromJson<SongSpec>(File.ReadAllText(path));
                float[] samples = ChiptuneRenderer.Render(SongWriter.Compose(spec), MusicSampleRate, out _);
                File.WriteAllBytes($"{MusicOutput}/{Path.GetFileNameWithoutExtension(path)}.wav", Wav.Encode(samples, MusicSampleRate));
                count++;
            }
            AssetDatabase.Refresh();
            Debug.Log($"Built {count} tracks into {MusicOutput}");
        }

        [MenuItem("WAR-2D/Audio/Build SFX")]
        public static void BuildSfx()
        {
            Directory.CreateDirectory(SfxOutput);
            int count = 0;
            foreach (string path in Directory.GetFiles(SfxSource, "*.json"))
            {
                var p = JsonUtility.FromJson<SfxrParams>(File.ReadAllText(path));
                float[] samples = Sfxr.Render(p, p.seed, SampleRate);
                File.WriteAllBytes($"{SfxOutput}/{Path.GetFileNameWithoutExtension(path)}.wav", Wav.Encode(samples, SampleRate));
                count++;
            }
            AssetDatabase.Refresh();
            Debug.Log($"Built {count} sounds into {SfxOutput}");
        }
    }

    /// <summary>Imports the generated sounds mono and decompressed on load (short, played often), and the music streamed.</summary>
    public sealed class AudioImporter : AssetPostprocessor
    {
        private void OnPreprocessAudio()
        {
            var importer = (UnityEditor.AudioImporter)assetImporter;
            if (assetPath.StartsWith(AudioBuild.MusicOutput + "/"))
            {
                // Long tracks: streamed and Vorbis-compressed in the build.
                importer.forceToMono = true;
                AudioImporterSampleSettings music = importer.defaultSampleSettings;
                music.loadType = AudioClipLoadType.Streaming;
                music.compressionFormat = AudioCompressionFormat.Vorbis;
                music.quality = 0.6f;
                importer.defaultSampleSettings = music;
                return;
            }
            if (!assetPath.StartsWith(AudioBuild.SfxOutput + "/")) return;
            importer.forceToMono = true;
            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.PCM;
            importer.defaultSampleSettings = settings;
        }
    }
}
