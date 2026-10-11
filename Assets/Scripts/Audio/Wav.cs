using System;
using UnityEngine;

namespace WAR2D.Audio
{
    /// <summary>Writes mono samples as a 16-bit PCM WAV file.</summary>
    public static class Wav
    {
        /// <summary>The WAV bytes (RIFF, PCM, 1 channel, 16 bits) of <paramref name="mono"/> in [-1, 1].</summary>
        public static byte[] Encode(float[] mono, int sampleRate)
        {
            int dataBytes = mono.Length * 2;
            var bytes = new byte[44 + dataBytes];
            int at = 0;
            void Text(string s) { foreach (char c in s) bytes[at++] = (byte)c; }
            void U32(uint v) { bytes[at++] = (byte)v; bytes[at++] = (byte)(v >> 8); bytes[at++] = (byte)(v >> 16); bytes[at++] = (byte)(v >> 24); }
            void U16(ushort v) { bytes[at++] = (byte)v; bytes[at++] = (byte)(v >> 8); }

            Text("RIFF"); U32((uint)(36 + dataBytes)); Text("WAVE");
            Text("fmt "); U32(16); U16(1); U16(1); U32((uint)sampleRate); U32((uint)(sampleRate * 2)); U16(2); U16(16);
            Text("data"); U32((uint)dataBytes);
            foreach (float s in mono)
            {
                short v = (short)Mathf.RoundToInt(Mathf.Clamp(s, -1f, 1f) * short.MaxValue);
                U16(unchecked((ushort)v));
            }
            return bytes;
        }
    }
}
