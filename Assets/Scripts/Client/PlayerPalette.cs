using UnityEngine;

/// <summary>
/// Player colours, in <c>GameCore.PlayerOrder</c> order: the approved Figma tokens <c>color/player/1–8</c>
/// (standard palette; the same values as <c>--color-player-N</c> in <c>Assets/UI/Theme.uss</c>). Units,
/// the minimap and the HUD swatches all use them.
/// </summary>
public static class PlayerPalette
{
    public static readonly Color32[] Standard =
    {
        new Color32(0x4D, 0xA3, 0xFF, 255), new Color32(0xE5, 0x48, 0x4D, 255),
        new Color32(0x46, 0xC4, 0x6B, 255), new Color32(0xF2, 0xC2, 0x30, 255),
        new Color32(0xA9, 0x70, 0xFF, 255), new Color32(0xF0, 0x7F, 0x2E, 255),
        new Color32(0x2F, 0xC4, 0xC4, 255), new Color32(0xE8, 0x5F, 0xB5, 255),
    };

    /// <summary>The colour of the player at <paramref name="slot"/> in the player order (grey when unknown).</summary>
    public static Color32 Of(int slot) => slot < 0 ? new Color32(160, 160, 160, 255) : Standard[slot % Standard.Length];
}
