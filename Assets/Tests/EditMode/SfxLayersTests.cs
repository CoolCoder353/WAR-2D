using System;
using System.Linq;
using NUnit.Framework;
using WAR2D.Art;
using WAR2D.Audio;

/// <summary>The layered synth (asset generation spec §5.1): deterministic, clean at its edges and centred, whatever the layers.</summary>
public class SfxLayersTests
{
    private static SfxrParams Blast() => new SfxrParams
    {
        synth = 2, volume = 0.8f, drive = 0.4f, tail = 0.3f, tailSeconds = 0.3f, seed = 3,
        layers = new[]
        {
            new SfxLayer { wave = LayerWave.White, attack = 0f, decay = 0.01f, highPass = 2000f },
            new SfxLayer { wave = LayerWave.Sine, frequency = 90f, slide = -3f, decay = 0.3f, gain = 1.2f },
            new SfxLayer { wave = LayerWave.Brown, offset = 0.01f, decay = 0.5f, lowPass = 1800f, lowPassEnd = 200f },
        },
    };

    private static SfxrParams One(LayerWave wave) => new SfxrParams
    {
        synth = 2, volume = 1f,
        layers = new[] { new SfxLayer { wave = wave, attack = 0f, sustain = 0.2f, decay = 0.1f, frequency = 3000f, duty = 0.2f } },
    };

    [Test]
    public void SameSpecAndSeedGiveTheSameSamples()
    {
        CollectionAssert.AreEqual(Sfxr.Render(Blast(), 3), Sfxr.Render(Blast(), 3));
        CollectionAssert.AreNotEqual(Sfxr.Render(Blast(), 3), Sfxr.Render(Blast(), 4), "noise follows the seed");
    }

    [Test]
    public void EveryWaveIsCleanAtItsEdgesCentredAndPeaksAtTheVolume([Values] LayerWave wave)
    {
        float[] samples = Sfxr.Render(One(wave), 5);
        Assert.AreEqual("", AudioLintTests.Lint(samples), "no clipping, DC offset or click, even with no attack and a narrow pulse");
        Assert.AreEqual(1f, samples.Max(v => Math.Abs(v)), 1e-3f);
        Assert.IsFalse(samples.Any(v => float.IsNaN(v)));
    }

    [Test]
    public void ALayeredBlastPassesTheLint() => Assert.AreEqual("", AudioLintTests.Lint(Sfxr.Render(Blast(), 3)));

    [Test]
    public void LengthIsTheLongestLayerPlusTheTail()
    {
        SfxrParams p = Blast();
        Assert.AreEqual(Math.Round((0.01f + 0.002f + 0.5f + 0.3f) * 44100), Sfxr.Render(p, 1).Length, 2);
        p.tail = 0f;
        Assert.AreEqual(Math.Round((0.01f + 0.002f + 0.5f) * 44100), Sfxr.Render(p, 1).Length, 2);
    }

    [Test]
    public void TheDecayCurveEndsAtSilence([Values(0f, 4f, 12f)] float curve)
    {
        var l = new SfxLayer { attack = 0.01f, sustain = 0.02f, decay = 0.1f, curve = curve };
        Assert.AreEqual(0.5f, SfxLayers.Envelope(l, 0.005f), 1e-4f);
        Assert.AreEqual(1f, SfxLayers.Envelope(l, 0.02f), 1e-4f);
        Assert.AreEqual(0f, SfxLayers.Envelope(l, 0.13f), 1e-4f);
        Assert.Less(SfxLayers.Envelope(l, 0.08f), SfxLayers.Envelope(l, 0.04f));
    }

    /// <summary>A band-limited square has far less energy folded back below its pitch than a naive one.</summary>
    [Test]
    public void SquareEdgesAreBandLimited()
    {
        // A 5 kHz square at 44.1 kHz: a naive one jumps between two values; the band-limited one rounds its edges.
        float[] samples = Sfxr.Render(new SfxrParams
        {
            synth = 2, volume = 1f,
            layers = new[] { new SfxLayer { wave = LayerWave.Square, sustain = 0.1f, decay = 0f, frequency = 5000f } },
        }, 1);
        int levels = samples.Skip(500).Take(2000).Select(v => Math.Round(v, 2)).Distinct().Count();
        Assert.Greater(levels, 8);
    }

    [Test]
    public void ALayeredSpecIsReadAndItsLayersAreRangeChecked()
    {
        var p = SpecJson.Read<SfxrParams>(
            "{ \"synth\": 2, \"volume\": 0.6, \"layers\": [ { \"wave\": \"Brown\", \"decay\": 0.4, \"lowPass\": 900 }, { \"wave\": \"Sine\", \"frequency\": 80 } ] }", "blast.json");
        Assert.AreEqual(2, p.layers.Length);
        Assert.AreEqual(LayerWave.Brown, p.layers[0].wave);
        Assert.AreEqual(900f, p.layers[0].lowPass);
        var e = Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("{ \"synth\": 2, \"layers\": [ { \"gain\": 9 } ] }", "blast.json"));
        StringAssert.Contains("layers[0]", e.Message);
        StringAssert.Contains("gain", e.Message);
        Assert.Throws<SpecException>(() => SpecJson.Read<SfxrParams>("{ \"synth\": 2, \"layers\": [ { \"wave\": \"Noise\" } ] }", "blast.json"), "the layered synth names its noises");
    }
}
