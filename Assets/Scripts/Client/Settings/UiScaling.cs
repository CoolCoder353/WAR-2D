using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Settings › Gameplay › UI scale: multiplies the scale of every UI Toolkit panel (the menus, the HUD and the
/// notices share <c>Assets/UI/PanelSettings.asset</c>, which scales with the screen from 1920×1080). Applied
/// live by <see cref="SettingsStore.Apply"/>, and again whenever a scene loads.
/// </summary>
public static class UiScaling
{
    /// <summary>The scale last applied (1 = the designed size).</summary>
    public static float Current { get; private set; } = 1f;

    private static bool hooked;

    /// <summary>The panels' scales before the first change, so the editor can put the shared asset back.</summary>
    private static readonly Dictionary<PanelSettings, float> Original = new Dictionary<PanelSettings, float>();

    /// <summary>Sets the scale of every panel in use (only while playing; the settings clamp it to 0.75–1.5).</summary>
    public static void Apply(float scale)
    {
        Current = scale;
        if (!Application.isPlaying) return;
        if (!hooked)
        {
            hooked = true;
            SceneManager.sceneLoaded += (_, _) => ApplyToPanels();
#if UNITY_EDITOR
            // PanelSettings is an asset: changes made in Play mode would otherwise stay after it.
            UnityEditor.EditorApplication.playModeStateChanged += state =>
            {
                if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
                foreach (KeyValuePair<PanelSettings, float> p in Original) if (p.Key != null) p.Key.scale = p.Value;
                Original.Clear();
                Current = 1f;
            };
#endif
        }
        ApplyToPanels();
    }

    private static void ApplyToPanels()
    {
        foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include))
        {
            PanelSettings panel = document.panelSettings;
            if (panel == null) continue;
            if (!Original.ContainsKey(panel)) Original[panel] = panel.scale;
            if (!Mathf.Approximately(panel.scale, Current)) panel.scale = Current;
        }
    }
}
