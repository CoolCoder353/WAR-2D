using UnityEngine;
using UnityEngine.Audio;

namespace WAR2D.Audio
{
    /// <summary>The tracks under <c>Resources/Audio/Music</c>.</summary>
    public enum MusicCue { None, Menu, MatchCalm, MatchBattle, Victory, Defeat }

    /// <summary>Plays one music cue at a time on two sources, crossfading between them; stingers play once.</summary>
    public sealed class MusicPlayer
    {
        /// <summary>Crossfade length in seconds.</summary>
        public const float FadeSeconds = 2f;

        private readonly AudioSource[] sources = new AudioSource[2];
        private int current;

        public MusicCue Cue { get; private set; }

        public MusicPlayer(GameObject host, AudioMixerGroup group)
        {
            for (int i = 0; i < 2; i++)
            {
                sources[i] = host.AddComponent<AudioSource>();
                sources[i].playOnAwake = false;
                sources[i].outputAudioMixerGroup = group;
                sources[i].volume = 0f;
            }
        }

        /// <summary>Starts crossfading to <paramref name="cue"/> (nothing when it is already playing).</summary>
        public void Play(MusicCue cue)
        {
            if (cue == Cue) return;
            Cue = cue;
            current = 1 - current;
            AudioSource next = sources[current];
            next.Stop();
            next.clip = cue == MusicCue.None ? null : Resources.Load<AudioClip>($"Audio/Music/{cue}");
            next.loop = cue != MusicCue.Victory && cue != MusicCue.Defeat;
            next.volume = 0f;
            if (next.clip != null) next.Play();
        }

        /// <summary>Moves the crossfade on by <paramref name="dt"/> seconds.</summary>
        public void Update(float dt)
        {
            float step = dt / FadeSeconds;
            for (int i = 0; i < 2; i++)
            {
                AudioSource s = sources[i];
                s.volume = Mathf.MoveTowards(s.volume, i == current && s.clip != null ? 1f : 0f, step);
                if (i != current && s.volume <= 0f && s.isPlaying) s.Stop();
            }
        }
    }
}
