using System;
using System.Collections.Generic;
using UnityEngine;

namespace WAR2D.Audio
{
    /// <summary>
    /// Decides which sound effects play: at most <c>voices</c> at once, one per id within the cooldown,
    /// and only those on screen (plus a margin). Pure logic; <see cref="AudioService"/> plays what it allows.
    /// </summary>
    public sealed class SfxVoicePool
    {
        /// <summary>Margin around the camera view (in view widths/heights) in which sounds still play.</summary>
        public const float ViewMargin = 0.25f;

        private readonly int voices;
        private readonly float cooldown;
        private readonly Func<string, float> lengthOf;
        private readonly float[] busyUntil;
        private readonly Dictionary<string, float> lastPlayed = new Dictionary<string, float>();

        /// <summary>
        /// <paramref name="lengthOf"/> gives a sound's length in seconds (how long it holds its voice);
        /// null treats every sound as 0.5 s.
        /// </summary>
        public SfxVoicePool(int voices, float perSoundCooldown, Func<string, float> lengthOf = null)
        {
            this.voices = Math.Max(1, voices);
            cooldown = perSoundCooldown;
            this.lengthOf = lengthOf ?? (_ => 0.5f);
            busyUntil = new float[this.voices];
        }

        /// <summary>The voice index the sound may use, or -1 when it is dropped.</summary>
        public int Claim(string id, Vector2 pos, Rect cameraView, float now)
        {
            if (!Audible(pos, cameraView)) return -1;
            if (lastPlayed.TryGetValue(id, out float last) && now - last < cooldown) return -1;
            for (int v = 0; v < voices; v++)
            {
                if (busyUntil[v] > now) continue;
                busyUntil[v] = now + lengthOf(id);
                lastPlayed[id] = now;
                return v;
            }
            return -1;
        }

        /// <summary>True when <paramref name="id"/> played less than the cooldown ago (a cheap check before placing it).</summary>
        public bool Cooling(string id, float now) => lastPlayed.TryGetValue(id, out float last) && now - last < cooldown;

        /// <summary>True when the sound may play now (see <see cref="Claim"/>).</summary>
        public bool TryPlay(string id, Vector2 pos, Rect cameraView, float now) => Claim(id, pos, cameraView, now) >= 0;

        /// <summary>Voices playing at <paramref name="now"/>.</summary>
        public int Active(float now)
        {
            int n = 0;
            foreach (float t in busyUntil) if (t > now) n++;
            return n;
        }

        /// <summary>True inside the camera view grown by <see cref="ViewMargin"/> on each side.</summary>
        public static bool Audible(Vector2 pos, Rect view)
        {
            Rect grown = Rect.MinMaxRect(view.xMin - view.width * ViewMargin, view.yMin - view.height * ViewMargin,
                view.xMax + view.width * ViewMargin, view.yMax + view.height * ViewMargin);
            return grown.Contains(pos);
        }

        /// <summary>1 at the view's centre, falling linearly to 0.3 at the edge of the audible area.</summary>
        public static float Volume(Vector2 pos, Rect view)
        {
            Vector2 half = view.size * (0.5f + ViewMargin);
            if (half.x <= 0f || half.y <= 0f) return 1f;
            Vector2 d = pos - view.center;
            float r = Mathf.Max(Mathf.Abs(d.x) / half.x, Mathf.Abs(d.y) / half.y);
            return Mathf.Lerp(1f, 0.3f, Mathf.Clamp01(r));
        }
    }
}
