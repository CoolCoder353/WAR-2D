using System;
using System.Collections.Generic;

namespace WAR2D.Audio.Chiptune
{
    /// <summary>The four voices of the chip: two pulse waves, a triangle and a noise channel.</summary>
    public enum Channel { Lead, Arp, Bass, Drums }

    /// <summary>Drum sounds on the noise channel (stored in <see cref="Note.Midi"/>).</summary>
    public static class Drum
    {
        public const int Kick = 0, Snare = 1, Hat = 2;
    }

    /// <summary>How a track feels: sets note density, drum patterns and the arpeggio speed.</summary>
    public enum Mood { Calm, Battle, Fanfare, Sombre }

    /// <summary>
    /// What <see cref="SongWriter"/> composes from (the JSON files under <c>Assets/Art/Audio/music/</c>).
    /// <see cref="sections"/> is a form such as "AABA": each letter is <see cref="barsPerSection"/> bars on
    /// its own chord progression and melody; a repeated letter repeats that material.
    /// </summary>
    [Serializable]
    public sealed class SongSpec
    {
        public int seed = 1;
        /// <summary>The tonic as a MIDI note (57 = A3).</summary>
        public int root = 57;
        public bool minor = true;
        public float bpm = 110f;
        public Mood mood = Mood.Calm;
        public string sections = "AABA";
        public int barsPerSection = 4;
        /// <summary>Loops seamlessly (match music); stingers play once.</summary>
        public bool loop = true;

        /// <summary>The key's scale as semitones above the root.</summary>
        public int[] Scale => minor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 };
    }

    /// <summary>One note: a start and length in sixteenth-note steps, a MIDI pitch (or <see cref="Drum"/>) and a velocity.</summary>
    public struct Note
    {
        public Channel Channel;
        public int Step, Length, Midi;
        public float Velocity;
    }

    /// <summary>A composed song: notes on a sixteenth-note grid, 4/4.</summary>
    public sealed class Song
    {
        public const int BeatsPerBar = 4, StepsPerBeat = 4, StepsPerBar = BeatsPerBar * StepsPerBeat;

        public SongSpec Spec;
        public int Bars;
        public readonly List<Note> Notes = new List<Note>();

        public int Steps => Bars * StepsPerBar;

        /// <summary>Samples per sixteenth note, rounded so the song is a whole number of them.</summary>
        public int SamplesPerStep(int sampleRate) => (int)Math.Round(sampleRate * 60.0 / Spec.bpm / StepsPerBeat);

        /// <summary>Samples per beat (four steps).</summary>
        public int SamplesPerBeat(int sampleRate) => SamplesPerStep(sampleRate) * StepsPerBeat;
    }
}
