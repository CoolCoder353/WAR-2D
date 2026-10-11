using System.IO;
using UnityEditor;
using UnityEngine;
using WAR2D.Audio;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// <c>WAR-2D/Audio/Build SFX</c>: renders every <c>Assets/Art/Audio/sfx/*.json</c> with <see cref="Sfxr"/>
    /// to <c>Assets/Resources/Audio/Sfx/&lt;name&gt;.wav</c> (44.1 kHz mono 16-bit).
    /// </summary>
    public static class AudioBuild
    {
        public const string SfxSource = "Assets/Art/Audio/sfx", SfxOutput = "Assets/Resources/Audio/Sfx";
        public const int SampleRate = 44100;

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

    /// <summary>Imports the generated sounds mono and decompressed on load (short, played often).</summary>
    public sealed class AudioImporter : AssetPostprocessor
    {
        private void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith(AudioBuild.SfxOutput + "/")) return;
            var importer = (UnityEditor.AudioImporter)assetImporter;
            importer.forceToMono = true;
            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.PCM;
            importer.defaultSampleSettings = settings;
        }
    }
}
