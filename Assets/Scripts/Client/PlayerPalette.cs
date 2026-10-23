using UnityEngine;

/// <summary>
/// Player colours for drawing: the current palette's (<see cref="Palettes"/>), by colour index, and each
/// player's colour index (their lobby colour). Units, buildings, the minimap and the HUD swatches all use them.
/// </summary>
public static class PlayerPalette
{
    /// <summary>A palette colour by index in the current palette (grey when unknown).</summary>
    public static Color32 Of(int colourIndex) => colourIndex < 0 ? new Color32(160, 160, 160, 255) : Palettes.Player(Palettes.Current, colourIndex);

    /// <summary>
    /// A player's colour index: the colour they picked in the lobby (public, <see cref="ClientPlayer.colourIndex"/>),
    /// AIs included, or for a player without an object (one who left), its place in the player order. -1 when unknown.
    /// </summary>
    public static int ColourIndexOf(int ownerId)
    {
        if (ClientPlayer.TryFind(ownerId, out ClientPlayer player)) return player.colourIndex;
        return GameCore.Instance != null ? GameCore.Instance.PlayerOrder.IndexOf(ownerId) : -1;
    }

    /// <summary>A player's colour (see <see cref="ColourIndexOf"/>).</summary>
    public static Color32 OfOwner(int ownerId) => Of(ColourIndexOf(ownerId));
}
