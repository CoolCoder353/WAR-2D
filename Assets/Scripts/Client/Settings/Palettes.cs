using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The approved colour palettes (Figma <c>Theme</c> modes: standard, colourblind, high-contrast). Player
/// colours here match <c>--color-player-N</c> in <c>Assets/UI/Theme.uss</c> under <c>:root</c>,
/// <c>.palette-colourblind</c> and <c>.palette-high-contrast</c>. Switching palette raises
/// <see cref="Changed"/>, and everything that draws player colours (units, buildings, minimap, the
/// UI's USS class on each document root) follows at once.
/// </summary>
public static class Palettes
{
    public const string Standard = "standard", Colourblind = "colourblind", HighContrast = "high-contrast";

    /// <summary>The palettes, in the order the settings show them.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { Standard, Colourblind, HighContrast };

    private static readonly Dictionary<string, Color32[]> Players = new Dictionary<string, Color32[]>
    {
        [Standard] = Hex("4DA3FF", "E5484D", "46C46B", "F2C230", "A970FF", "F07F2E", "2FC4C4", "E85FB5"),
        [Colourblind] = Hex("E69F00", "56B4E9", "009E73", "F0E442", "0072B2", "D55E00", "CC79A7", "BBBBBB"),
        [HighContrast] = Hex("3DA5FF", "FF5A5A", "3DFF7A", "FFE14D", "C38BFF", "FF9F3D", "3DFFF0", "FF7AD9"),
    };

    private static string current = Standard;

    /// <summary>The palette in use.</summary>
    public static string Current
    {
        get => current;
        set
        {
            string next = Players.ContainsKey(value ?? "") ? value : Standard;
            if (next == current) return;
            current = next;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    public static event Action Changed;

    /// <summary>A player colour in a palette (an unknown palette is standard).</summary>
    public static Color32 Player(string palette, int colourIndex)
    {
        if (!Players.TryGetValue(palette ?? "", out Color32[] colours)) colours = Players[Standard];
        return colours[((colourIndex % colours.Length) + colours.Length) % colours.Length];
    }

    /// <summary>The USS class that selects a palette's tokens (none for standard, which is <c>:root</c>).</summary>
    public static string UssClass(string palette) => palette == Standard ? null : "palette-" + palette;

    /// <summary>Puts the current palette's USS class on a UI root (and removes the others).</summary>
    public static void ApplyTo(UnityEngine.UIElements.VisualElement root)
    {
        if (root == null) return;
        foreach (string name in Names)
        {
            string uss = UssClass(name);
            if (uss != null) root.EnableInClassList(uss, name == current);
        }
    }

    private static Color32[] Hex(params string[] hex)
    {
        var colours = new Color32[hex.Length];
        for (int i = 0; i < hex.Length; i++)
        {
            ColorUtility.TryParseHtmlString("#" + hex[i], out Color c);
            colours[i] = c;
        }
        return colours;
    }
}
