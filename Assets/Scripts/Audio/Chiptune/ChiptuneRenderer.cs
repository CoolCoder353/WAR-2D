using System;

namespace WAR2D.Audio.Chiptune
{
    /// <summary>
    /// Renders a <see cref="Song"/> on a four-voice chip: pulse lead (50 % duty), pulse arpeggio (12.5 %),
    /// a 4-bit stepped triangle bass and an LFSR noise channel for drums. Every note gets a short attack and
    /// release inside its own length, so the song starts and ends at silence and loops without a click.
    /// </summary>
    public static class ChiptuneRenderer
    {
        /// <summary>The mix's peak level.</summary>
        public const float Peak = 0.85f;

        private const float Attack = 0.003f, Release = 0.012f;

        /// <summary>Mono samples, exactly <c>Steps × SamplesPerStep</c> long; the loop starts at <paramref name="loopStartSample"/>.</summary>
        public static float[] Render(Song song, int sampleRate, out int loopStartSample)
        {
            int perStep = song.SamplesPerStep(sampleRate);
            var mix = new float[song.Steps * perStep];
            loopStartSample = 0;
            foreach (Note note in song.Notes)
            {
                int start = note.Step * perStep;
                int length = Math.Min(note.Length * perStep, mix.Length - start);
                if (length <= 0) continue;
                switch (note.Channel)
                {
                    case Channel.Lead: Tone(mix, start, length, note, sampleRate, 0.5f, 0.30f, decay: 1.5f); break;
                    case Channel.Arp: Tone(mix, start, length, note, sampleRate, 0.125f, 0.16f, decay: 6f); break;
                    case Channel.Bass: Triangle(mix, start, length, note, sampleRate, 0.38f); break;
                    default: Noise(mix, start, length, note, sampleRate); break;
                }
            }

            float peak = 0f;
            foreach (float v in mix) peak = Math.Max(peak, Math.Abs(v));
            if (peak > 0f)
                for (int i = 0; i < mix.Length; i++) mix[i] *= Peak / peak;
            return mix;
        }

        private static float Gate(int i, int length, int sampleRate)
        {
            float t = i / (float)sampleRate, left = (length - i) / (float)sampleRate;
            return Math.Min(1f, Math.Min(t / Attack, left / Release));
        }

        private static float Frequency(int midi) => 440f * (float)Math.Pow(2.0, (midi - 69) / 12.0);

        private static void Tone(float[] mix, int start, int length, Note note, int sampleRate, float duty, float volume, float decay)
        {
            float f = Frequency(note.Midi) / sampleRate;
            double phase = 0;
            for (int i = 0; i < length; i++)
            {
                phase += f;
                phase -= Math.Floor(phase);
                float envelope = Gate(i, length, sampleRate) * (0.55f + 0.45f * (float)Math.Exp(-decay * i / (double)sampleRate));
                mix[start + i] += (phase < duty ? 1f : -1f) * volume * note.Velocity * envelope;
            }
        }

        private static void Triangle(float[] mix, int start, int length, Note note, int sampleRate, float volume)
        {
            float f = Frequency(note.Midi) / sampleRate;
            double phase = 0;
            for (int i = 0; i < length; i++)
            {
                phase += f;
                phase -= Math.Floor(phase);
                float tri = phase < 0.5 ? (float)(4 * phase - 1) : (float)(3 - 4 * phase);
                tri = (float)Math.Round(tri * 7.5f) / 7.5f; // 16 steps, like the chip's DAC
                mix[start + i] += tri * volume * note.Velocity * Gate(i, length, sampleRate);
            }
        }

        private static void Noise(float[] mix, int start, int length, Note note, int sampleRate)
        {
            // Kick: low, slow-clocked noise with a falling clock; snare: mid; hat: high and short.
            float clock = note.Midi switch { Drum.Kick => 900f, Drum.Snare => 6000f, _ => 16000f };
            float decay = note.Midi switch { Drum.Kick => 30f, Drum.Snare => 22f, _ => 60f };
            float volume = note.Midi switch { Drum.Kick => 0.42f, Drum.Snare => 0.26f, _ => 0.12f };
            ushort lfsr = 1;
            double phase = 0;
            float value = 1f;
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)sampleRate;
                float rate = note.Midi == Drum.Kick ? clock * (float)Math.Exp(-t * 20f) + 60f : clock;
                phase += rate / sampleRate;
                while (phase >= 1)
                {
                    phase -= 1;
                    int bit = (lfsr ^ (lfsr >> 1)) & 1;
                    lfsr = (ushort)((lfsr >> 1) | (bit << 14));
                    value = (lfsr & 1) != 0 ? 1f : -1f;
                }
                mix[start + i] += value * volume * note.Velocity * (float)Math.Exp(-decay * t) * Gate(i, length, sampleRate);
            }
        }
    }
}
