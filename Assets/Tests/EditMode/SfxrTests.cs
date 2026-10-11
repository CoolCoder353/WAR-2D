using System;
using System.Text;
using NUnit.Framework;
using WAR2D.Audio;

/// <summary>The sfxr-style synth and the WAV writer.</summary>
public class SfxrTests
{
    private static SfxrParams Busy() => new SfxrParams
    {
        wave = SfxrWave.Noise, attack = 0.01f, sustain = 0.1f, punch = 0.6f, decay = 0.3f, frequency = 900f, slide = -2f,
        deltaSlide = 1f, vibratoDepth = 0.2f, vibratoSpeed = 12f, changeAmount = 1.5f, changeTime = 0.05f, duty = 0.3f,
        dutySweep = 0.5f, lowPass = 0.6f, lowPassSweep = -0.5f, resonance = 0.8f, highPass = 0.1f, highPassSweep = 0.1f,
        phaserOffset = 0.002f, phaserSweep = 0.004f, volume = 1f,
    };

    [Test]
    public void SameParamsAndSeedGiveTheSameSamples()
    {
        CollectionAssert.AreEqual(Sfxr.Render(Busy(), 3), Sfxr.Render(Busy(), 3));
        CollectionAssert.AreNotEqual(Sfxr.Render(Busy(), 3), Sfxr.Render(Busy(), 4), "noise follows the seed");
    }

    [Test]
    public void SamplesStayInRangeWithoutNaN([Values] SfxrWave wave)
    {
        SfxrParams p = Busy();
        p.wave = wave;
        foreach (float s in Sfxr.Render(p, 1))
        {
            Assert.IsFalse(float.IsNaN(s));
            Assert.LessOrEqual(Math.Abs(s), 1f);
        }
    }

    [Test]
    public void PeakIsTheVolume()
    {
        SfxrParams p = Busy();
        p.volume = 0.6f;
        float peak = 0f;
        foreach (float s in Sfxr.Render(p, 1)) peak = Math.Max(peak, Math.Abs(s));
        Assert.AreEqual(0.6f, peak, 1e-4f);
    }

    [Test]
    public void LengthIsTheEnvelope()
    {
        SfxrParams p = Busy();
        Assert.AreEqual((p.attack + p.sustain + p.decay) * 44100f, Sfxr.Render(p, 1).Length, 1f);
        Assert.AreEqual((p.attack + p.sustain + p.decay) * 22050f, Sfxr.Render(p, 1, 22050).Length, 1f);
    }

    [Test]
    public void WavHeaderDescribesMono16BitPcm()
    {
        var samples = new[] { 0f, 1f, -1f, 0.5f };
        byte[] wav = Wav.Encode(samples, 44100);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.AreEqual(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.AreEqual("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.AreEqual("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.AreEqual(16, BitConverter.ToInt32(wav, 16));
        Assert.AreEqual(1, BitConverter.ToInt16(wav, 20), "PCM");
        Assert.AreEqual(1, BitConverter.ToInt16(wav, 22), "mono");
        Assert.AreEqual(44100, BitConverter.ToInt32(wav, 24));
        Assert.AreEqual(16, BitConverter.ToInt16(wav, 34));
        Assert.AreEqual("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.AreEqual(samples.Length * 2, BitConverter.ToInt32(wav, 40));

        // Round trip through a minimal parser.
        for (int i = 0; i < samples.Length; i++)
            Assert.AreEqual(samples[i], BitConverter.ToInt16(wav, 44 + 2 * i) / (float)short.MaxValue, 1e-4f);
    }
}
