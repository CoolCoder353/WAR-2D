using System;
using System.IO;
using UnityEngine;

/// <summary>The player's settings (Settings: Graphics, Audio, Controls). Saved as JSON on this machine.</summary>
[Serializable]
public sealed class GameSettings
{
    public int Width = 1920, Height = 1080;
    public bool Fullscreen = true, VSync;
    /// <summary>Frames per second cap; 0 = none.</summary>
    public int FpsCap = 144;
    public float Master = 1f, Music = 1f, Sfx = 1f;
    public bool MenuBattle = true;
    public string Palette = Palettes.Standard;
    public bool EdgeScroll = true;
    /// <summary>The Input System's binding overrides (<see cref="Rebinding"/>).</summary>
    public string Bindings = "";

    /// <summary>The resolutions the settings offer.</summary>
    public static readonly Vector2Int[] Resolutions = { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440) };

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

    /// <summary>The default path, or the given one.</summary>
    public static string PathOf(string path) => path ?? System.IO.Path.Combine(Application.persistentDataPath, FileName);

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
            Debug.LogWarning($"[Settings] {path} could not be read ({e.Message}); using the defaults.");
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
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(settings, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Settings] could not save {path}: {e.Message}");
        }
    }

    /// <summary>Applies the settings: display, frame rate, volume, palette, bindings and the menu battle.</summary>
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
        AudioListener.volume = s.Master;
        Palettes.Current = s.Palette;
        Rebinding.Load(GameInput.Map, s.Bindings);
    }

    /// <summary>Loads and applies the saved settings once at start (players only; tests and headless runs keep the defaults).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyOnStart()
    {
        if (Application.isBatchMode || Application.isEditor) return;
        Apply(Current);
    }
}
