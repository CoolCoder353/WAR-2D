using UnityEngine;
using UnityEngine.Audio;

namespace WAR2D.Audio
{
    /// <summary>
    /// Client-side sound: effects through a <see cref="SfxVoicePool"/>, UI sounds, and music chosen from the
    /// game state (menu, calm or battle in a match, the end stingers). Lives on its own
    /// <c>DontDestroyOnLoad</c> object, made on first use, routed through <c>Main.mixer</c>'s groups so the
    /// settings sliders apply. Sounds only follow events the client already draws.
    /// </summary>
    public sealed class AudioService : MonoBehaviour
    {
        public const int Voices = 16;
        public const float PerSoundCooldown = 0.05f;

        private static AudioService instance;
        private SfxVoicePool pool;
        private AudioSource[] voices;
        private AudioSource ui;
        private MusicPlayer music;
        private MusicCue ending = MusicCue.None;
        private float battleUntil, deathWindowStart;
        private int deathsInWindow;

        /// <summary>The music cue playing now.</summary>
        public static MusicCue CurrentCue => instance != null ? instance.music.Cue : MusicCue.None;

        /// <summary>The camera sounds are placed against; null uses <c>Camera.main</c>.</summary>
        public static Camera View;

        private static AudioService Instance
        {
            get
            {
                if (instance != null || !Application.isPlaying) return instance;
                var go = new GameObject("AudioService");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<AudioService>();
                return instance;
            }
        }

        /// <summary>Plays sound effect <paramref name="id"/> at a world position, if the pool allows it.</summary>
        public static void Sfx(string id, Vector2 position)
        {
            AudioService s = Instance;
            if (s == null || s.pool.Cooling(id, Time.unscaledTime)) return; // most calls in a battle stop here
            Camera cam = View != null ? View : Camera.main;
            if (cam == null) return;
            float h = cam.orthographicSize * 2f, w = h * cam.aspect;
            var view = new Rect((Vector2)cam.transform.position - new Vector2(w, h) * 0.5f, new Vector2(w, h));
            int voice = s.pool.Claim(id, position, view, Time.unscaledTime);
            if (voice < 0) return;
            AudioClip clip = Clip(id);
            if (clip == null) return;
            s.voices[voice].PlayOneShot(clip, SfxVoicePool.Volume(position, view));
        }

        /// <summary>Plays an interface sound (never culled by position).</summary>
        public static void Ui(string id)
        {
            AudioService s = Instance;
            AudioClip clip = Clip(id);
            if (s != null && clip != null) s.ui.PlayOneShot(clip);
        }

        /// <summary>Plays an end-of-match stinger (Victory or Defeat); it holds until the match is left.</summary>
        public static void Music(MusicCue cue)
        {
            AudioService s = Instance;
            if (s == null) return;
            s.ending = cue;
            s.music.Play(cue);
        }

        /// <summary>Counts a death the player saw, for the switch to battle music.</summary>
        public static void ReportDeath()
        {
            AudioService s = Instance;
            if (s == null) return;
            float now = Time.unscaledTime;
            if (now - s.deathWindowStart >= 1f) { s.deathWindowStart = now; s.deathsInWindow = 0; }
            s.deathsInWindow++;
            var audio = Config.ConfigLoader.LoadConfig()?.Audio;
            if (audio != null && s.deathsInWindow >= audio.BattleDeathsPerSecond) s.battleUntil = now + audio.BattleHoldSeconds;
        }

        private static readonly System.Collections.Generic.Dictionary<string, AudioClip> Clips = new System.Collections.Generic.Dictionary<string, AudioClip>();

        /// <summary>A sound effect clip, loaded once.</summary>
        private static AudioClip Clip(string id)
        {
            if (Clips.TryGetValue(id, out AudioClip clip)) return clip;
            clip = Resources.Load<AudioClip>($"Audio/Sfx/{id}");
            Clips[id] = clip;
            return clip;
        }

        private void Awake()
        {
            AudioMixer mixer = Resources.Load<AudioMixer>("Main");
            AudioMixerGroup sfx = Group(mixer, "SFX"), musicGroup = Group(mixer, "Music");
            pool = new SfxVoicePool(Voices, PerSoundCooldown, id => Clip(id) is AudioClip c ? c.length : 0.5f);
            voices = new AudioSource[Voices];
            for (int i = 0; i < Voices; i++) voices[i] = Source(sfx);
            ui = Source(sfx);
            music = new MusicPlayer(gameObject, musicGroup);
        }

        private AudioSource Source(AudioMixerGroup group)
        {
            AudioSource s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.outputAudioMixerGroup = group;
            return s;
        }

        private static AudioMixerGroup Group(AudioMixer mixer, string name)
        {
            AudioMixerGroup[] groups = mixer != null ? mixer.FindMatchingGroups(name) : null;
            return groups != null && groups.Length > 0 ? groups[0] : null;
        }

        private void Update()
        {
            music.Play(Choose());
            music.Update(Time.unscaledDeltaTime);
        }

        /// <summary>The cue for the current game state.</summary>
        private MusicCue Choose()
        {
            GameCore core = GameCore.Instance;
            if (core == null || core.CurrentState == GameState.Lobby)
            {
                ending = MusicCue.None;
                return MusicCue.Menu;
            }
            if (ending != MusicCue.None) return ending;
            return Time.unscaledTime < battleUntil ? MusicCue.MatchBattle : MusicCue.MatchCalm;
        }

        /// <summary>Starts the music with the game (the menu track plays from the first scene).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Application.isBatchMode) return; // headless servers and the perf harness stay silent
            _ = Instance;
        }
    }
}
