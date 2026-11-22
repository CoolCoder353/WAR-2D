using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using WAR2D.Art;
using WAR2D.Audio;
using WAR2D.Audio.Chiptune;
using WAR2D.EditorTools;

/// <summary>
/// The strict reader for the generators' parameter files, and the sound build that goes through it
/// (asset generation spec §2.1, §3.4): a spec's <c>wave</c> and <c>mood</c> are read by name, and what is
/// built is what the spec says.
/// </summary>
public class SpecJsonTests
{
    [Test]
    public void EnumsAreReadByName([Values] SfxrWave wave)
    {
        var p = SpecJson.Read<SfxrParams>($"{{ \"wave\": \"{wave}\" }}", "test.json");
        Assert.AreEqual(wave, p.wave);
    }

    [Test]
    public void AnEnumGivenAsANumberIsRefused()
    {
        var e = Assert.Throws<SpecException>(() => SpecJson.Read<SongSpec>("{ \"mood\": 1 }", "Song.json"));
        StringAssert.Contains("Song.json", e.Message);
    }

    [Test]
    public void AnUnknownFieldIsRefusedWithTheFileAndFieldNamed()
    {
        var e = Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("{ \"sustian\": 0.1 }", "shot.json"));
        StringAssert.Contains("shot.json", e.Message);
        StringAssert.Contains("sustian", e.Message);
    }

    [Test]
    public void ANumberOutsideItsRangeIsRefusedWithTheFileAndFieldNamed()
    {
        var e = Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("{ \"volume\": 1.5 }", "shot.json"));
        StringAssert.Contains("shot.json", e.Message);
        StringAssert.Contains("volume", e.Message);
        Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("{ \"decay\": -0.1 }", "shot.json"));
        Assert.Throws<SpecException>(() => SpecJson.Read<SongSpec>("{ \"bpm\": 0 }", "Song.json"));
    }

    [Test]
    public void BrokenJsonIsRefusedWithTheFileNamed()
    {
        var e = Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("{ \"wave\": ", "shot.json"));
        StringAssert.Contains("shot.json", e.Message);
        Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("null", "shot.json"));
    }

    [Test]
    public void FieldsLeftOutKeepTheirDefaults()
    {
        var p = SpecJson.Read<SfxrParams>("{ \"frequency\": 300 }", "test.json");
        var defaults = new SfxrParams();
        Assert.AreEqual(300f, p.frequency);
        Assert.AreEqual(defaults.lowPass, p.lowPass);
        Assert.AreEqual(defaults.repeat, p.repeat);
        Assert.AreEqual(defaults.wave, p.wave);
    }

    [Test]
    public void EverySoundAndSongSpecLoads()
    {
        string[] sounds = Directory.GetFiles(AudioBuild.SfxSource, "*.json"), songs = Directory.GetFiles(AudioBuild.MusicSource, "*.json");
        Assert.Greater(sounds.Length, 0);
        Assert.Greater(songs.Length, 0);
        foreach (string path in sounds) Assert.DoesNotThrow(() => AudioBuild.ReadSfx(path), path);
        foreach (string path in songs) Assert.DoesNotThrow(() => AudioBuild.ReadSong(path), path);
    }

    /// <summary>
    /// The 2026-11-15 bug: the build read <c>"wave": "Noise"</c> as Square, so 35 sounds shipped as square waves.
    /// A square (no filter, no phaser) only ever takes two values inside its sustain; the other waves take many.
    /// </summary>
    [Test]
    public void ASpecsWaveIsTheWaveThatIsRendered([Values] SfxrWave wave)
    {
        var p = SpecJson.Read<SfxrParams>($"{{ \"wave\": \"{wave}\", \"sustain\": 0.2, \"decay\": 0, \"frequency\": 440, \"volume\": 1 }}", "test.json");
        float[] samples = Sfxr.Render(p, 7);
        int levels = samples.Skip(100).Take(4000).Select(v => Math.Round(Math.Abs(v), 3)).Distinct().Count();
        if (wave == SfxrWave.Square) Assert.AreEqual(1, levels, "a square holds one level");
        else Assert.Greater(levels, 20, $"{wave} must not come out as a square");
    }

    /// <summary>Every built sound is exactly what its spec renders to now: a spec or synth change that wasn't rebuilt fails here.</summary>
    [Test]
    public void EveryBuiltSoundIsItsSpecRendered()
    {
        foreach (string path in Directory.GetFiles(AudioBuild.SfxSource, "*.json"))
        {
            string wav = $"{AudioBuild.SfxOutput}/{Path.GetFileNameWithoutExtension(path)}.wav";
            Assert.IsTrue(File.Exists(wav), $"{wav} is not built: run WAR-2D/Audio/Build SFX");
            Assert.IsTrue(File.ReadAllBytes(wav).AsSpan().SequenceEqual(AudioBuild.RenderSfx(AudioBuild.ReadSfx(path))),
                $"{wav} is not what {path} renders to: run WAR-2D/Audio/Build SFX");
        }
    }
}
