using System;
using UnityEngine;

namespace WAR2D.Audio
{
    /// <summary>Oscillators of the layered synth (<see cref="SfxLayers"/>). The three noises don't follow the layer's pitch.</summary>
    public enum LayerWave { Square, Saw, Sine, Triangle, White, Pink, Brown }

    /// <summary>
    /// One layer of a layered sound (<c>"synth": 2</c>): an oscillator or a noise through its own envelope, pitch
    /// slide and filters, laid into the sound at <see cref="offset"/>. Times are seconds, frequencies and cutoffs Hz,
    /// slides octaves per second.
    /// </summary>
    [Serializable]
    public sealed class SfxLayer
    {
        public LayerWave wave = LayerWave.White;
        /// <summary>The layer's level in the mix, before the sound's own <see cref="SfxrParams.volume"/>.</summary>
        [Range(0f, 4f)] public float gain = 1f;
        /// <summary>Seconds into the sound at which the layer starts.</summary>
        [Min(0f)] public float offset = 0f;
        [Header("Envelope")]
        [Min(0f)] public float attack = 0.002f;
        [Min(0f)] public float sustain = 0f;
        [Min(0f)] public float decay = 0.2f;
        /// <summary>How sharply the decay falls: 0 is a straight line, 4 a natural exponential fall, 8 and up a click with a long faint tail.</summary>
        [Range(0f, 16f)] public float curve = 4f;
        [Header("Pitch")]
        [Range(1f, 20000f)] public float frequency = 220f;
        public float slide = 0f;
        /// <summary>Change of <see cref="slide"/> per second.</summary>
        public float deltaSlide = 0f;
        /// <summary>The slide never goes below this (Hz); 0 means no floor.</summary>
        [Min(0f)] public float minFrequency = 0f;
        [Range(0f, 1f)] public float vibratoDepth = 0f;
        [Min(0f)] public float vibratoSpeed = 0f;
        /// <summary>Square duty cycle.</summary>
        [Range(0.05f, 0.95f)] public float duty = 0.5f;
        [Header("Tone")]
        /// <summary>Low-pass cutoff at the layer's start (0 = off) and at its end (0 = the same as the start).</summary>
        [Range(0f, 20000f)] public float lowPass = 0f;
        [Range(0f, 20000f)] public float lowPassEnd = 0f;
        /// <summary>How much the low-pass rings at its cutoff: 0.7 is flat, 4 and up whistles.</summary>
        [Range(0.5f, 12f)] public float resonance = 0.7f;
        /// <summary>High-pass cutoff at the layer's start (0 = off) and at its end (0 = the same as the start).</summary>
        [Range(0f, 20000f)] public float highPass = 0f;
        [Range(0f, 20000f)] public float highPassEnd = 0f;
        /// <summary>Amplitude wobble: depth (0..1) and speed in Hz (a flame's flutter, a rattle).</summary>
        [Range(0f, 1f)] public float tremoloDepth = 0f;
        [Min(0f)] public float tremoloSpeed = 0f;
        /// <summary>Holds each sample for this many output samples (1 = off): a coarse, crunchy tone.</summary>
        [Range(1, 64)] public int crush = 1;

        /// <summary>The layer's length in seconds, without its offset.</summary>
        public float Length => Mathf.Max(0f, attack) + Mathf.Max(0f, sustain) + Mathf.Max(0f, decay);
    }

    /// <summary>
    /// The layered synth (asset generation spec §5.1): a sound is a few <see cref="SfxLayer"/>s mixed together, so an
    /// impact can be a click, a pitched body and a noise tail, each shaped on its own. Square and saw edges are
    /// band-limited (PolyBLEP), decays are exponential, noise is true white, pink or brown whatever the pitch, and the
    /// mix goes through an optional soft clipper and a short reverb tail, then a DC blocker and edge fades, so a
    /// built sound never clicks or sits off centre. Deterministic for a seed.
    /// </summary>
    public static class SfxLayers
    {
        /// <summary>Seconds the sound fades in and out over at its very ends, so it starts and stops at silence.</summary>
        public const float EdgeIn = 0.001f, EdgeOut = 0.004f;

        /// <summary>The sound's samples, mono in [-1, 1], peaking at <see cref="SfxrParams.volume"/>.</summary>
        public static float[] Render(SfxrParams p, int seed, int sampleRate = 44100)
        {
            SfxLayer[] layers = p.layers ?? Array.Empty<SfxLayer>();
            float seconds = 0f;
            foreach (SfxLayer layer in layers) seconds = Mathf.Max(seconds, layer.offset + layer.Length);
            float tail = p.tail > 0f ? Mathf.Max(0.05f, p.tailSeconds) : 0f;
            int body = Mathf.RoundToInt(seconds * sampleRate), length = body + Mathf.RoundToInt(tail * sampleRate);
            var mix = new float[length];
            for (int i = 0; i < layers.Length; i++) AddLayer(mix, layers[i], new System.Random(seed + i * 7919), sampleRate);

            if (p.drive > 0f)
            {
                // Soft clipper: tanh, scaled so a quiet signal keeps its level and a loud one is rounded off.
                float peakIn = Peak(mix);
                if (peakIn > 1e-6f)
                    for (int i = 0; i < length; i++) mix[i] = (float)Math.Tanh(mix[i] / peakIn * (1f + p.drive * 4f));
            }
            if (p.tail > 0f) Reverb(mix, p.tail, tail, sampleRate);

            // DC blocker (a one-pole high-pass at 20 Hz), then fades at the two ends.
            float r = 1f - 2f * Mathf.PI * 20f / sampleRate, x1 = 0f, y1 = 0f;
            for (int i = 0; i < length; i++)
            {
                float x = mix[i];
                y1 = x - x1 + r * y1;
                x1 = x;
                mix[i] = y1;
            }
            int fadeIn = Mathf.Min(length / 2, Mathf.RoundToInt(EdgeIn * sampleRate)), fadeOut = Mathf.Min(length / 2, Mathf.RoundToInt(EdgeOut * sampleRate));
            for (int i = 0; i < fadeIn; i++) mix[i] *= i / (float)fadeIn;
            for (int i = 0; i < fadeOut; i++) mix[length - 1 - i] *= i / (float)fadeOut;

            float peak = Peak(mix);
            float gain = peak > 1e-6f ? Mathf.Clamp01(p.volume) / peak : 0f;
            for (int i = 0; i < length; i++)
            {
                float v = mix[i] * gain;
                mix[i] = float.IsNaN(v) || float.IsInfinity(v) ? 0f : Mathf.Clamp(v, -1f, 1f);
            }
            return mix;
        }

        private static float Peak(float[] samples)
        {
            float peak = 0f;
            foreach (float v in samples) peak = Mathf.Max(peak, Mathf.Abs(v));
            return peak;
        }

        /// <summary>The layer's loudness over its life: a linear attack, the sustain, then the curved decay to exactly 0.</summary>
        public static float Envelope(SfxLayer l, float t)
        {
            if (t < 0f) return 0f;
            if (t < l.attack) return t / l.attack;
            t -= l.attack;
            if (t < l.sustain) return 1f;
            t -= l.sustain;
            if (l.decay <= 0f || t >= l.decay) return 0f;
            float x = t / l.decay;
            if (l.curve < 0.01f) return 1f - x;
            float end = Mathf.Exp(-l.curve);
            return (Mathf.Exp(-l.curve * x) - end) / (1f - end);
        }

        private static void AddLayer(float[] mix, SfxLayer l, System.Random random, int sampleRate)
        {
            int start = Mathf.RoundToInt(l.offset * sampleRate), count = Mathf.Min(Mathf.RoundToInt(l.Length * sampleRate), mix.Length - start);
            if (count <= 0) return;
            float dt = 1f / sampleRate, seconds = l.Length;
            double phase = 0;
            float tri = 0f;
            // Filter states: a state-variable low-pass (two integrators) and a one-pole high-pass.
            float ic1 = 0f, ic2 = 0f, hpLow = 0f;
            // Noise states: Paul Kellett's pink filter and a leaky integrator for brown.
            float b0 = 0f, b1 = 0f, b2 = 0f, brown = 0f;
            float held = 0f;
            int crush = Mathf.Max(1, l.crush);

            for (int i = 0; i < count; i++)
            {
                float t = i * dt, life = seconds > 0f ? t / seconds : 0f;
                float octaves = l.slide * t + 0.5f * l.deltaSlide * t * t;
                float frequency = l.frequency * Mathf.Pow(2f, octaves);
                if (l.vibratoDepth > 0f) frequency *= 1f + l.vibratoDepth * Mathf.Sin(2f * Mathf.PI * l.vibratoSpeed * t);
                if (l.minFrequency > 0f) frequency = Mathf.Max(frequency, l.minFrequency);
                frequency = Mathf.Clamp(frequency, 1f, sampleRate * 0.45f);
                float step = frequency * dt;
                phase += step;
                if (phase >= 1.0) phase -= Math.Floor(phase);
                float ph = (float)phase;

                float sample;
                switch (l.wave)
                {
                    case LayerWave.Square:
                    {
                        float fall = ph - l.duty;
                        if (fall < 0f) fall += 1f;
                        sample = (ph < l.duty ? 1f : -1f) + Blep(ph, step) - Blep(fall, step);
                        sample -= 2f * l.duty - 1f; // a narrow pulse sits off centre: take its mean out
                        break;
                    }
                    case LayerWave.Saw:
                        sample = 2f * ph - 1f - Blep(ph, step);
                        break;
                    case LayerWave.Sine:
                        sample = Mathf.Sin(2f * Mathf.PI * ph);
                        break;
                    case LayerWave.Triangle:
                    {
                        // A band-limited square, integrated: a triangle without the naive one's hiss.
                        float half = ph - 0.5f;
                        if (half < 0f) half += 1f;
                        float square = (ph < 0.5f ? 1f : -1f) + Blep(ph, step) - Blep(half, step);
                        tri += 4f * step * square;
                        tri *= 0.9995f;
                        sample = tri;
                        break;
                    }
                    case LayerWave.Pink:
                    {
                        float white = (float)(random.NextDouble() * 2.0 - 1.0);
                        b0 = 0.99765f * b0 + white * 0.0990460f;
                        b1 = 0.96300f * b1 + white * 0.2965164f;
                        b2 = 0.57000f * b2 + white * 1.0526913f;
                        sample = (b0 + b1 + b2 + white * 0.1848f) * 0.25f;
                        break;
                    }
                    case LayerWave.Brown:
                    {
                        float white = (float)(random.NextDouble() * 2.0 - 1.0);
                        brown = 0.995f * brown + white * 0.06f;
                        sample = brown * 1.6f;
                        break;
                    }
                    default:
                        sample = (float)(random.NextDouble() * 2.0 - 1.0);
                        break;
                }

                if (l.lowPass > 0f)
                {
                    float cutoff = Sweep(l.lowPass, l.lowPassEnd, life, sampleRate);
                    float g = Mathf.Tan(Mathf.PI * cutoff / sampleRate), k = 1f / Mathf.Max(0.5f, l.resonance);
                    float a1 = 1f / (1f + g * (g + k)), a2 = g * a1, a3 = g * a2;
                    float v3 = sample - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
                    ic1 = 2f * v1 - ic1;
                    ic2 = 2f * v2 - ic2;
                    sample = v2;
                }
                if (l.highPass > 0f)
                {
                    float cutoff = Sweep(l.highPass, l.highPassEnd, life, sampleRate);
                    hpLow += (sample - hpLow) * (1f - Mathf.Exp(-2f * Mathf.PI * cutoff / sampleRate));
                    sample -= hpLow;
                }
                if (crush > 1)
                {
                    if (i % crush == 0) held = sample;
                    sample = held;
                }
                if (l.tremoloDepth > 0f) sample *= 1f - l.tremoloDepth * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * l.tremoloSpeed * t));
                mix[start + i] += sample * l.gain * Envelope(l, t);
            }
        }

        /// <summary>A cutoff moving from <paramref name="from"/> to <paramref name="to"/> (0 = no move) evenly in pitch over the layer's life.</summary>
        private static float Sweep(float from, float to, float life, int sampleRate)
        {
            float cutoff = to > 0f ? from * Mathf.Pow(to / from, life) : from;
            return Mathf.Clamp(cutoff, 10f, sampleRate * 0.45f);
        }

        /// <summary>PolyBLEP: the correction that rounds off an edge at phase 0, <paramref name="step"/> of a cycle wide.</summary>
        private static float Blep(float phase, float step)
        {
            if (phase < step)
            {
                float x = phase / step;
                return x + x - x * x - 1f;
            }
            if (phase > 1f - step)
            {
                float x = (phase - 1f) / step;
                return x * x + x + x + 1f;
            }
            return 0f;
        }

        // Comb and all-pass delays (in samples at 44.1 kHz) of a small Schroeder reverb.
        private static readonly int[] Combs = { 1116, 1188, 1277, 1356 }, AllPasses = { 556, 441 };

        /// <summary>Adds a reverb tail: <paramref name="amount"/> of it under the dry sound, ringing for about <paramref name="seconds"/>.</summary>
        private static void Reverb(float[] samples, float amount, float seconds, int sampleRate)
        {
            var wet = new float[samples.Length];
            float scale = sampleRate / 44100f;
            foreach (int comb in Combs)
            {
                int delay = Mathf.Max(1, Mathf.RoundToInt(comb * scale));
                // Feedback that falls 60 dB over the tail's length.
                float feedback = Mathf.Pow(0.001f, delay / (seconds * sampleRate));
                var line = new float[delay];
                float damp = 0f;
                for (int i = 0; i < samples.Length; i++)
                {
                    float o = line[i % delay];
                    damp = o * 0.6f + damp * 0.4f; // the tail darkens as it rings
                    line[i % delay] = samples[i] + damp * feedback;
                    wet[i] += o * 0.25f;
                }
            }
            foreach (int allPass in AllPasses)
            {
                int delay = Mathf.Max(1, Mathf.RoundToInt(allPass * scale));
                var line = new float[delay];
                for (int i = 0; i < wet.Length; i++)
                {
                    float o = line[i % delay], x = wet[i];
                    line[i % delay] = x + o * 0.5f;
                    wet[i] = o - x * 0.5f;
                }
            }
            for (int i = 0; i < samples.Length; i++) samples[i] += wet[i] * amount;
        }
    }
}
