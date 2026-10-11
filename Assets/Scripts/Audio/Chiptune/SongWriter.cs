using System;
using System.Collections.Generic;

namespace WAR2D.Audio.Chiptune
{
    /// <summary>
    /// Composes a <see cref="Song"/> from a <see cref="SongSpec"/>, deterministically for its seed: a chord
    /// progression per section, then a bass line, an arpeggio of each chord, a lead melody (chord tones on
    /// strong beats, steps through the scale between) and drums, all shaped by the mood.
    /// </summary>
    public static class SongWriter
    {
        // Progressions as scale degrees (0 = tonic), one chord per bar.
        private static readonly int[][] MinorProgressions =
        {
            new[] { 0, 5, 2, 6 }, new[] { 0, 3, 4, 0 }, new[] { 0, 6, 5, 4 }, new[] { 0, 3, 6, 2 }, new[] { 5, 3, 0, 4 },
        };
        private static readonly int[][] MajorProgressions =
        {
            new[] { 0, 4, 5, 3 }, new[] { 0, 3, 4, 0 }, new[] { 0, 5, 3, 4 }, new[] { 3, 4, 0, 0 }, new[] { 0, 1, 4, 0 },
        };

        public static Song Compose(SongSpec spec)
        {
            var random = new Random(spec.seed);
            var song = new Song { Spec = spec, Bars = spec.sections.Length * spec.barsPerSection };
            int[][] progressions = spec.minor ? MinorProgressions : MajorProgressions;
            var material = new Dictionary<char, (int[] chords, List<Note> melody)>();

            for (int s = 0; s < spec.sections.Length; s++)
            {
                char letter = spec.sections[s];
                if (!material.TryGetValue(letter, out var m))
                {
                    int[] chords = new int[spec.barsPerSection];
                    int[] progression = progressions[random.Next(progressions.Length)];
                    for (int b = 0; b < chords.Length; b++) chords[b] = progression[b % progression.Length];
                    m = (chords, Melody(spec, chords, random));
                    material[letter] = m;
                }
                int sectionStep = s * spec.barsPerSection * Song.StepsPerBar;
                for (int b = 0; b < spec.barsPerSection; b++)
                {
                    int barStep = sectionStep + b * Song.StepsPerBar;
                    int[] chord = Triad(spec, m.chords[b]);
                    Bass(song, spec, chord, barStep);
                    Arpeggio(song, spec, chord, barStep);
                    Drums(song, spec, barStep, b == spec.barsPerSection - 1);
                }
                foreach (Note n in m.melody)
                {
                    Note note = n;
                    note.Step += sectionStep;
                    song.Notes.Add(note);
                }
            }
            return song;
        }

        /// <summary>The MIDI notes of the triad on scale degree <paramref name="degree"/>, in the root's octave.</summary>
        public static int[] Triad(SongSpec spec, int degree) => new[] { Degree(spec, degree), Degree(spec, degree + 2), Degree(spec, degree + 4) };

        /// <summary>The MIDI note of a scale degree (wraps into higher octaves).</summary>
        public static int Degree(SongSpec spec, int degree)
        {
            int[] scale = spec.Scale;
            int octave = (int)Math.Floor(degree / (double)scale.Length);
            return spec.root + 12 * octave + scale[degree - octave * scale.Length];
        }

        private static List<Note> Melody(SongSpec spec, int[] chords, Random random)
        {
            var notes = new List<Note>();
            // Note lengths in steps by mood: battle runs in eighths, calm music breathes.
            int[] lengths = spec.mood switch
            {
                Mood.Battle => new[] { 2, 2, 2, 4 },
                Mood.Fanfare => new[] { 2, 4, 4, 8 },
                Mood.Sombre => new[] { 4, 8, 8, 12 },
                _ => new[] { 2, 4, 4, 6, 8 },
            };
            int degree = 7 + chords[0]; // an octave above the root, on the first chord
            for (int b = 0; b < chords.Length; b++)
            {
                int step = 0;
                while (step < Song.StepsPerBar)
                {
                    int length = Math.Min(lengths[random.Next(lengths.Length)], Song.StepsPerBar - step);
                    bool strong = step % Song.StepsPerBeat == 0 && (step / Song.StepsPerBeat) % 2 == 0;
                    if (strong)
                    {
                        // Land on the nearest chord tone (degrees chord, +2, +4 in any octave).
                        int best = degree, bestDistance = int.MaxValue;
                        for (int d = degree - 3; d <= degree + 3; d++)
                        {
                            int rel = ((d - chords[b]) % 7 + 7) % 7;
                            if (rel != 0 && rel != 2 && rel != 4) continue;
                            if (Math.Abs(d - degree) < bestDistance) { bestDistance = Math.Abs(d - degree); best = d; }
                        }
                        degree = best;
                    }
                    else
                    {
                        degree += random.Next(-2, 3);
                    }
                    degree = Math.Max(5, Math.Min(13, degree)); // keep the lead in a singable band
                    bool rest = !strong && random.NextDouble() < (spec.mood == Mood.Sombre ? 0.3 : 0.15);
                    if (!rest)
                        notes.Add(new Note { Channel = Channel.Lead, Step = b * Song.StepsPerBar + step, Length = length, Midi = Degree(spec, degree), Velocity = strong ? 1f : 0.8f });
                    step += length;
                }
            }
            return notes;
        }

        private static void Bass(Song song, SongSpec spec, int[] chord, int barStep)
        {
            int root = chord[0] - 12;
            if (spec.mood == Mood.Battle)
            {
                // Driving eighths, octave jumps on the off-beats.
                for (int i = 0; i < 8; i++)
                    song.Notes.Add(new Note { Channel = Channel.Bass, Step = barStep + i * 2, Length = 2, Midi = i % 2 == 0 ? root : root + 12, Velocity = 1f });
            }
            else
            {
                // Root on 1, fifth on 3.
                song.Notes.Add(new Note { Channel = Channel.Bass, Step = barStep, Length = 8, Midi = root, Velocity = 1f });
                song.Notes.Add(new Note { Channel = Channel.Bass, Step = barStep + 8, Length = 8, Midi = chord[2] - 12, Velocity = 0.9f });
            }
        }

        private static void Arpeggio(Song song, SongSpec spec, int[] chord, int barStep)
        {
            int rate = spec.mood == Mood.Battle || spec.mood == Mood.Fanfare ? 1 : 2; // sixteenths or eighths
            int[] pattern = { 0, 1, 2, 1 };
            for (int step = 0, k = 0; step < Song.StepsPerBar; step += rate, k++)
                song.Notes.Add(new Note { Channel = Channel.Arp, Step = barStep + step, Length = rate, Midi = chord[pattern[k % 4]] + 12, Velocity = 0.6f });
        }

        private static void Drums(Song song, SongSpec spec, int barStep, bool fill)
        {
            if (spec.mood == Mood.Sombre) return;
            for (int step = 0; step < Song.StepsPerBar; step++)
            {
                int drum = -1;
                switch (spec.mood)
                {
                    case Mood.Battle:
                        if (step % 8 == 0 || step == 6 || step == 10) drum = Drum.Kick;
                        else if (step % 8 == 4) drum = Drum.Snare;
                        else drum = Drum.Hat;
                        break;
                    case Mood.Fanfare:
                        if (step % 4 == 0) drum = step % 8 == 0 ? Drum.Kick : Drum.Snare;
                        break;
                    default:
                        if (step == 0 || step == 10) drum = Drum.Kick;
                        else if (step == 4 || step == 12) drum = Drum.Snare;
                        else if (step % 2 == 0) drum = Drum.Hat;
                        break;
                }
                if (fill && step >= 12) drum = Drum.Snare; // a short fill into the next section
                if (drum >= 0) song.Notes.Add(new Note { Channel = Channel.Drums, Step = barStep + step, Length = 1, Midi = drum, Velocity = drum == Drum.Hat ? 0.5f : 1f });
            }
        }
    }
}
