using System;
using NUnit.Framework;
using WAR2D.Audio.Chiptune;

/// <summary>The music composer and the chip renderer.</summary>
public class ChiptuneTests
{
    private const int Rate = 22050;

    private static SongSpec Spec(Mood mood = Mood.Battle, bool minor = true) =>
        new SongSpec { seed = 5, root = 57, minor = minor, bpm = 137f, mood = mood, sections = "AB", barsPerSection = 2 };

    [Test]
    public void ComposingIsDeterministicPerSeed()
    {
        float[] a = ChiptuneRenderer.Render(SongWriter.Compose(Spec()), Rate, out _);
        float[] b = ChiptuneRenderer.Render(SongWriter.Compose(Spec()), Rate, out _);
        CollectionAssert.AreEqual(a, b);
        SongSpec other = Spec();
        other.seed = 6;
        CollectionAssert.AreNotEqual(a, ChiptuneRenderer.Render(SongWriter.Compose(other), Rate, out _));
    }

    [Test]
    public void LengthIsAWholeNumberOfBeatsAndLoopsWithoutAClick([Values] Mood mood)
    {
        Song song = SongWriter.Compose(Spec(mood));
        float[] samples = ChiptuneRenderer.Render(song, Rate, out int loopStart);
        Assert.AreEqual(song.Bars * Song.BeatsPerBar * song.SamplesPerBeat(Rate), samples.Length);
        Assert.LessOrEqual(Math.Abs(samples[samples.Length - 1] - samples[loopStart]), 0.1f);
    }

    [Test]
    public void PeakStaysBelowPointNine([Values] Mood mood)
    {
        float peak = 0f;
        foreach (float s in ChiptuneRenderer.Render(SongWriter.Compose(Spec(mood)), Rate, out _)) peak = Math.Max(peak, Math.Abs(s));
        Assert.LessOrEqual(peak, 0.9f);
        Assert.Greater(peak, 0.5f, "not silent");
    }

    [Test]
    public void LeadNotesAreInTheKey([Values] bool minor)
    {
        SongSpec spec = Spec(Mood.Calm, minor);
        Song song = SongWriter.Compose(spec);
        int leads = 0;
        foreach (Note n in song.Notes)
        {
            if (n.Channel != Channel.Lead) continue;
            leads++;
            int pc = ((n.Midi - spec.root) % 12 + 12) % 12;
            CollectionAssert.Contains(spec.Scale, pc, $"MIDI {n.Midi}");
        }
        Assert.Greater(leads, 0);
    }
}
