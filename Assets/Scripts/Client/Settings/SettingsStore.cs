using WAR2D.Dev.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>When a health bar is drawn over a unit or building.</summary>
public enum HealthBarMode { Always, DamagedOnly, SelectedOnly }

/// <summary>How many attack tracers are drawn (visual only: the server sends the same events).</summary>
public enum TracerMode { Full, Reduced, Off }

/// <summary>How soon a hover card shows.</summary>
public enum TooltipDelayMode { Fast, Normal, Off }

/// <summary>The minimap's size (160, 224, 304 px at 1080p).</summary>
public enum MinimapSize { Small, Medium, Large }

/// <summary>
/// What the minimap draws on top of the terrain and fog; they change only what is drawn, never what is known.
/// <see cref="Hqs"/> (the HQ flags, playtest 2026-10-30) is kept out of <see cref="All"/> and the saved bits:
/// <see cref="GameSettings.MinimapHqs"/> holds it, so files saved before it existed still show the flags.
/// </summary>
[Flags]
public enum MinimapLayers { Gems = 1, Ghosts = 2, Army = 4, Buildings = 8, All = 15, Hqs = 16 }

/// <summary>The player's settings (Settings: Graphics, Audio, Controls, Gameplay). Saved as JSON on this machine.</summary>
[Serializable]
public sealed class GameSettings
{
    public int Width = 1920, Height = 1080;
    public bool Fullscreen = true, VSync;
    /// <summary>Frames per second cap; 0 = none.</summary>
    public int FpsCap = 144;
    /// <summary>Volumes, 0–1. Music starts at half (playtest 2026-10-30: it sat over the effects).</summary>
    public float Master = 1f, Music = 0.5f, Sfx = 1f;
    public bool MenuBattle = true;
    public string Palette = Palettes.Standard;
    public bool EdgeScroll = true;

    // ---- Gameplay tab (v0.15 QoL). Every value is clamped by Clamp(); old files without them keep the defaults.
    /// <summary>HUD and menu scale, 0.75–1.5.</summary>
    public float UiScale = 1f;
    /// <summary>A <see cref="HealthBarMode"/>.</summary>
    public int HealthBars = (int)HealthBarMode.DamagedOnly;
    /// <summary>A <see cref="TracerMode"/>.</summary>
    public int Tracers = (int)TracerMode.Full;
    /// <summary>A <see cref="TooltipDelayMode"/>.</summary>
    public int TooltipDelay = (int)TooltipDelayMode.Normal;
    /// <summary>Multipliers on the camera's pan, edge-scroll and zoom speeds (0.5–2) and the Shift pan multiplier (1.5–4).</summary>
    public float PanSpeed = 1f, EdgeScrollSpeed = 1f, ZoomSpeed = 1f, ShiftMultiplier = 2.5f;
    /// <summary>Per <see cref="AlertKind"/>: whether it shows in the feed and whether it plays its sound (null or short = all on).</summary>
    public bool[] AlertShow = AllOn(), AlertSound = AllOn();

    private static bool[] AllOn() { var on = new bool[AlertSlots]; for (int i = 0; i < on.Length; i++) on[i] = true; return on; }
    /// <summary>Whether demolishing and surrendering ask first (surrender always asks in a match with other humans).</summary>
    public bool ConfirmDemolish = true, ConfirmSurrender = true;
    /// <summary>A <see cref="MinimapSize"/>.</summary>
    public int MinimapScale = (int)MinimapSize.Medium;
    /// <summary>Minimap layers drawn, a <see cref="MinimapLayers"/> bit set.</summary>
    public int MinimapFilters = (int)MinimapLayers.All;
    /// <summary>Whether the minimap draws the HQ flags (its fifth filter, Figma "Playtest fixes" item 10).</summary>
    public bool MinimapHqs = true;
    /// <summary>The Input System's binding overrides (<see cref="Rebinding"/>).</summary>
    public string Bindings = "";

    /// <summary>The resolutions the settings offer.</summary>
    public static readonly Vector2Int[] Resolutions =
    {
        new Vector2Int(1280, 720), new Vector2Int(1366, 768), new Vector2Int(1600, 900), new Vector2Int(1920, 1080),
        new Vector2Int(2560, 1080), new Vector2Int(2560, 1440), new Vector2Int(3440, 1440), new Vector2Int(3840, 2160),
    };

    /// <summary>
    /// The resolutions to list: the common ones, plus the monitor's own and the one in use when they are not among them,
    /// smallest first.
    /// </summary>
    public static List<Vector2Int> ResolutionChoices(Vector2Int current)
    {
        var choices = new List<Vector2Int>(Resolutions);
        void Include(Vector2Int r) { if (r.x >= 640 && r.y >= 360 && !choices.Contains(r)) choices.Add(r); }
        if (!Application.isBatchMode)
        {
            Resolution desktop = Screen.currentResolution;
            Include(new Vector2Int(desktop.width, desktop.height));
        }
        Include(current);
        choices.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return choices;
    }

    /// <summary>The FPS caps the settings offer (0 = none).</summary>
    public static readonly int[] FpsCaps = { 60, 120, 144, 0 };

    /// <summary>Puts out-of-range values back in range (a hand-edited or old file).</summary>
    public void Clamp()
    {
        Width = Mathf.Clamp(Width, 640, 7680);
        Height = Mathf.Clamp(Height, 360, 4320);
        FpsCap = FpsCap <= 0 ? 0 : Mathf.Clamp(FpsCap, 30, 1000);
        Master = Clamp01(Master);
        Music = Clamp01(Music);
        Sfx = Clamp01(Sfx);
        bool known = false;
        foreach (string name in Palettes.Names) known |= name == Palette;
        if (!known) Palette = Palettes.Standard;
        Bindings ??= "";
        UiScale = Finite(UiScale, 1f, 0.75f, 1.5f);
        HealthBars = Mathf.Clamp(HealthBars, 0, 2);
        Tracers = Mathf.Clamp(Tracers, 0, 2);
        TooltipDelay = Mathf.Clamp(TooltipDelay, 0, 2);
        PanSpeed = Finite(PanSpeed, 1f, 0.5f, 2f);
        EdgeScrollSpeed = Finite(EdgeScrollSpeed, 1f, 0.5f, 2f);
        ZoomSpeed = Finite(ZoomSpeed, 1f, 0.5f, 2f);
        ShiftMultiplier = Finite(ShiftMultiplier, 2.5f, 1.5f, 4f);
        MinimapScale = Mathf.Clamp(MinimapScale, 0, 2);
        MinimapFilters &= (int)MinimapLayers.All;
        AlertShow = AlertFlags(AlertShow);
        AlertSound = AlertFlags(AlertSound);
        // The two alerts that lose games when missed keep at least one of Show and Sound.
        foreach (AlertKind must in MustHear)
            if (!AlertShow[(int)must] && !AlertSound[(int)must]) AlertShow[(int)must] = true;
    }

    /// <summary>Alert kinds the settings never let the player silence completely.</summary>
    public static readonly AlertKind[] MustHear = { AlertKind.UnderAttack, AlertKind.UpkeepUnpaid };

    /// <summary>Slots in the per-alert switches (room for kinds added later).</summary>
    public const int AlertSlots = 16;

    private static bool[] AlertFlags(bool[] flags)
    {
        var fixedUp = new bool[AlertSlots];
        for (int i = 0; i < fixedUp.Length; i++) fixedUp[i] = flags == null || i >= flags.Length || flags[i];
        return fixedUp;
    }

    private static float Finite(float v, float fallback, float min, float max) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : Mathf.Clamp(v, min, max);

    /// <summary>True when alerts of this kind show in the feed.</summary>
    public bool ShowsAlert(AlertKind kind) => AlertShow == null || (int)kind >= AlertShow.Length || AlertShow[(int)kind];

    /// <summary>True when alerts of this kind play their sound.</summary>
    public bool SoundsAlert(AlertKind kind) => AlertSound == null || (int)kind >= AlertSound.Length || AlertSound[(int)kind];

    /// <summary>True when the minimap draws this layer.</summary>
    public bool ShowsLayer(MinimapLayers layer) => layer == MinimapLayers.Hqs ? MinimapHqs : (MinimapFilters & (int)layer) != 0;

    /// <summary>Every layer the minimap draws now, the HQ flags included.</summary>
    public MinimapLayers ShownLayers => (MinimapLayers)(MinimapFilters & (int)MinimapLayers.All) | (MinimapHqs ? MinimapLayers.Hqs : 0);

    /// <summary>Turns a minimap layer on or off.</summary>
    public void ToggleLayer(MinimapLayers layer)
    {
        if (layer == MinimapLayers.Hqs) MinimapHqs = !MinimapHqs;
        else MinimapFilters ^= (int)layer;
    }

    private static float Clamp01(float v) => float.IsNaN(v) ? 1f : Mathf.Clamp01(v);
}

/// <summary>Loads, saves and applies <see cref="GameSettings"/> (JSON in <c>persistentDataPath</c>).</summary>
public static class SettingsStore
{
    private const string FileName = "settings.json";

    /// <summary>The settings in use (loaded on first use).</summary>
    public static GameSettings Current
    {
        get => current ??= Load();
        set => current = value;
    }

    private static GameSettings current;

    /// <summary>Test seam: where settings go instead of the player's real file (the editor shares it with builds).</summary>
    internal static string PathForTests;

    /// <summary>The given path, or the test path, or the player's settings file.</summary>
    public static string PathOf(string path) => path ?? PathForTests ?? System.IO.Path.Combine(Application.persistentDataPath, FileName);

    /// <summary>Reads the settings; a missing file gives the defaults, a corrupt one the defaults and a warning.</summary>
    public static GameSettings Load(string path = null)
    {
        path = PathOf(path);
        var settings = new GameSettings();
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                settings = JsonUtility.FromJson<GameSettings>(json) ?? new GameSettings();
            }
        }
        catch (Exception e)
        {
            WarLog.Warn(LogCategory.UI, $"{path} could not be read ({e.Message}); using the defaults.");
            settings = new GameSettings();
        }
        settings.Clamp();
        return settings;
    }

    /// <summary>Writes the settings.</summary>
    public static void Save(GameSettings settings, string path = null)
    {
        path = PathOf(path);
        try
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonUtility.ToJson(settings, true));
        }
        catch (Exception e)
        {
            WarLog.Warn(LogCategory.UI, $"could not save {path}: {e.Message}");
        }
    }

    /// <summary>Applies the settings: display, frame rate, volume, palette, bindings, the menu battle and the UI scale.</summary>
    public static void Apply(GameSettings s)
    {
        if (!Application.isBatchMode)
        {
            FullScreenMode mode = s.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (Screen.width != s.Width || Screen.height != s.Height || Screen.fullScreenMode != mode)
                Screen.SetResolution(s.Width, s.Height, mode);
            QualitySettings.vSyncCount = s.VSync ? 1 : 0;
            Application.targetFrameRate = s.FpsCap > 0 ? s.FpsCap : -1;
        }
        UnityEngine.Audio.AudioMixer mixer = Mixer;
        if (mixer != null)
        {
            mixer.SetFloat("MasterVolume", Decibels(s.Master));
            mixer.SetFloat("MusicVolume", Decibels(s.Music));
            mixer.SetFloat("SFXVolume", Decibels(s.Sfx));
        }
        Palettes.Current = s.Palette;
        Rebinding.Load(GameInput.Map, s.Bindings);
        UiScaling.Apply(s.UiScale);
    }

    private static UnityEngine.Audio.AudioMixer mixer;

    /// <summary>The game's mixer (<c>Assets/Resources/Main.mixer</c>: Master, Music and SFX groups); route new audio sources to its groups.</summary>
    public static UnityEngine.Audio.AudioMixer Mixer => mixer != null ? mixer : mixer = Resources.Load<UnityEngine.Audio.AudioMixer>("Main");

    /// <summary>A 0–1 slider value as mixer decibels (silent at 0).</summary>
    public static float Decibels(float volume) => volume <= 0.0001f ? -80f : 20f * Mathf.Log10(volume);

    /// <summary>Loads and applies the saved settings once at start (players only; tests and headless runs keep the defaults).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyOnStart()
    {
        if (Application.isBatchMode || Application.isEditor) return;
        Apply(Current);
    }
}
