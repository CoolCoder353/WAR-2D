using System;
using UnityEngine;

namespace WAR2D.Audio
{
    /// <summary>Oscillator shapes of the <see cref="Sfxr"/> synth.</summary>
    public enum SfxrWave { Square, Saw, Sine, Noise }

    /// <summary>
    /// One sound's parameters for <see cref="Sfxr"/> (the JSON files under <c>Assets/Art/Audio/sfx/</c>).
    /// Times are seconds, frequencies Hz, slides octaves per second; filters are 0..1 like sfxr's.
    /// </summary>
    [Serializable]
    public sealed class SfxrParams
    {
        public SfxrWave wave = SfxrWave.Square;
        [Header("Envelope")]
        public float attack = 0f;
        public float sustain = 0.1f;
        /// <summary>Extra loudness at the start of the sustain, fading out over it (0..1).</summary>
        public float punch = 0f;
        public float decay = 0.2f;
        [Header("Pitch")]
        public float frequency = 440f;
        /// <summary>The slide never goes below this (Hz); 0 means no floor.</summary>
        public float minFrequency = 0f;
        public float slide = 0f;
        /// <summary>Change of <see cref="slide"/> per second.</summary>
        public float deltaSlide = 0f;
        /// <summary>Vibrato depth as a fraction of the pitch.</summary>
        public float vibratoDepth = 0f;
        public float vibratoSpeed = 0f;
        /// <summary>Arpeggio: after <see cref="changeTime"/> seconds the pitch is multiplied by this (1 = off).</summary>
        public float changeAmount = 1f;
        public float changeTime = 0f;
        [Header("Tone")]
        /// <summary>Square duty cycle (0..1) and its change per second.</summary>
        public float duty = 0.5f;
        public float dutySweep = 0f;
        /// <summary>Low-pass cutoff (1 = off), its sweep per second and resonance.</summary>
        public float lowPass = 1f;
        public float lowPassSweep = 0f;
        public float resonance = 0f;
        /// <summary>High-pass cutoff (0 = off) and its sweep per second.</summary>
        public float highPass = 0f;
        public float highPassSweep = 0f;
        /// <summary>Phaser delay (seconds) and its change per second.</summary>
        public float phaserOffset = 0f;
        public float phaserSweep = 0f;
        /// <summary>The rendered sound's peak level (0..1): filters and the phaser can't make it clip or vanish.</summary>
        public float volume = 0.5f;
        /// <summary>The seed <c>WAR-2D/Audio/Build SFX</c> renders this sound with.</summary>
        public int seed = 1;
    }

    /// <summary>
    /// A small sfxr-style synth (own implementation of the model: oscillator, slide, vibrato, arpeggio,
    /// duty sweep, low/high-pass, phaser, attack–sustain–decay envelope). Deterministic for a seed.
    /// </summary>
    public static class Sfxr
    {
        private const int PhaserSize = 1024, NoiseSize = 32;

        /// <summary>The sound's samples, mono in [-1, 1]; length is attack + sustain + decay.</summary>
        public static float[] Render(SfxrParams p, int seed, int sampleRate = 44100)
        {
            float attack = Mathf.Max(0f, p.attack), sustain = Mathf.Max(0f, p.sustain), decay = Mathf.Max(0f, p.decay);
            int length = Mathf.RoundToInt((attack + sustain + decay) * sampleRate);
            var output = new float[length];
            var random = new System.Random(seed);
            var noise = new float[NoiseSize];
            FillNoise(noise, random);
            var phaser = new float[PhaserSize];
            int phaserIndex = 0;
            double phase = 0;
            float lp = 0f, lpDelta = 0f, hp = 0f;
            float dt = 1f / sampleRate;

            for (int i = 0; i < length; i++)
            {
                float t = i * dt;

                // Pitch: exponential slide (with its own slide), vibrato, one arpeggio step, floor.
                float octaves = p.slide * t + 0.5f * p.deltaSlide * t * t;
                float frequency = p.frequency * Mathf.Pow(2f, octaves);
                if (p.vibratoDepth > 0f) frequency *= 1f + p.vibratoDepth * Mathf.Sin(2f * Mathf.PI * p.vibratoSpeed * t);
                if (p.changeTime > 0f && t >= p.changeTime) frequency *= p.changeAmount;
                if (p.minFrequency > 0f) frequency = Mathf.Max(frequency, p.minFrequency);
                frequency = Mathf.Clamp(frequency, 1f, sampleRate * 0.45f);

                phase += frequency * dt;
                if (phase >= 1.0)
                {
                    phase -= Math.Floor(phase);
                    if (p.wave == SfxrWave.Noise) FillNoise(noise, random);
                }
                float ph = (float)phase;
                float duty = Mathf.Clamp(p.duty + p.dutySweep * t, 0.05f, 0.95f);
                float sample = p.wave switch
                {
                    SfxrWave.Square => ph < duty ? 0.5f : -0.5f,
                    SfxrWave.Saw => 1f - 2f * ph,
                    SfxrWave.Sine => Mathf.Sin(2f * Mathf.PI * ph),
                    _ => noise[(int)(ph * NoiseSize) % NoiseSize],
                };

                // sfxr's resonant low-pass, then a one-pole high-pass.
                float cutoff = Mathf.Clamp01(p.lowPass + p.lowPassSweep * t);
                float previous = lp;
                if (cutoff < 1f)
                {
                    float w = cutoff * cutoff * cutoff * 0.1f;
                    float damping = Mathf.Min(0.8f, 5f / (1f + p.resonance * p.resonance * 20f) * (0.01f + w));
                    lpDelta += (sample - lp) * w;
                    lpDelta -= lpDelta * damping;
                    lp += lpDelta;
                }
                else
                {
                    lp = sample;
                    lpDelta = 0f;
                }
                float highCut = Mathf.Clamp01(p.highPass + p.highPassSweep * t);
                hp += lp - previous;
                hp -= hp * highCut * highCut * 0.1f;
                sample = highCut > 0f ? hp : lp;

                if (p.phaserOffset != 0f || p.phaserSweep != 0f)
                {
                    int offset = Mathf.Clamp(Mathf.RoundToInt((p.phaserOffset + p.phaserSweep * t) * sampleRate), 0, PhaserSize - 1);
                    phaser[phaserIndex] = sample;
                    sample += phaser[(phaserIndex - offset + PhaserSize) % PhaserSize];
                    phaserIndex = (phaserIndex + 1) % PhaserSize;
                }

                float value = sample * Envelope(t, attack, sustain, decay, p.punch);
                output[i] = float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
            }

            float peak = 0f;
            foreach (float v in output) peak = Mathf.Max(peak, Mathf.Abs(v));
            float gain = peak > 1e-6f ? Mathf.Clamp01(p.volume) / peak : 0f;
            for (int i = 0; i < length; i++) output[i] = Mathf.Clamp(output[i] * gain, -1f, 1f);
            return output;
        }

        /// <summary>Linear attack to 1, sustain (plus a fading punch), linear decay to 0.</summary>
        public static float Envelope(float t, float attack, float sustain, float decay, float punch)
        {
            if (t < attack) return t / attack;
            t -= attack;
            if (t < sustain) return 1f + punch * (1f - t / sustain);
            t -= sustain;
            return decay > 0f ? Mathf.Max(0f, 1f - t / decay) : 0f;
        }

        private static void FillNoise(float[] noise, System.Random random)
        {
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        }
    }
}
