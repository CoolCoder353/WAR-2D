using System;
using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Style;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// The art lint (asset-generation spec §3.7, A11): mechanical checks on a built unit or building sprite. Each rule has
    /// a short name, and <see cref="Lint"/> returns the names a sprite breaks:
    /// <c>orphan</c> (an opaque pixel with no opaque 4-neighbour), <c>outline</c> (an edge pixel that isn't the outline
    /// colour), <c>team</c> (team pixels under 8 % or over 60 % of a team sprite) and <c>dark</c> (the non-team, non-outline pixels'
    /// mean OKLab lightness less than <see cref="MinLift"/> above the floor's, §4.2).
    /// </summary>
    public static class ArtLint
    {
        /// <summary>Team pixels as a share of a team sprite's opaque pixels.</summary>
        public const float MinTeam = 0.08f, MaxTeam = 0.60f;
        /// <summary>How far above the floor's mean OKLab lightness a sprite's non-team pixels must sit.</summary>
        public const float MinLift = 0.12f;
        /// <summary>Edge pixels that may miss the outline colour before the rule fails (a share of the sprite's edge).</summary>
        public const float OutlineSlack = 0.02f;

        /// <summary>What <see cref="Measure"/> found in one sprite, over all its cells.</summary>
        public struct Measured
        {
            public int Opaque, Team, Orphans, Edge, OpenEdge;
            public float Lightness;
            public float TeamShare => Opaque > 0 ? (float)Team / Opaque : 0f;
            public float OpenShare => Edge > 0 ? (float)OpenEdge / Edge : 0f;
        }

        /// <summary>The mean OKLab lightness of the open floor tile (every variant of <c>Ground</c>) in <paramref name="tiles"/>.</summary>
        public static float FloorLightness(ArtBuild.BuiltAtlas tiles)
        {
            AtlasSprite floor = tiles.Data.sprites.Find(s => s.id == nameof(TileType.Ground));
            double sum = 0;
            int n = 0;
            for (int k = 0; k < floor.frames * floor.directions; k++)
            {
                AtlasFrame f = tiles.Data.frames[floor.first + k];
                for (int y = 0; y < f.h; y++)
                    for (int x = 0; x < f.w; x++)
                    {
                        sum += PaletteQuantizer.OkLab(tiles.Pixels[(f.y + y) * tiles.Data.width + f.x + x]).x;
                        n++;
                    }
            }
            return (float)(sum / n);
        }

        /// <summary>Counts sprite <paramref name="id"/>'s pixels in <paramref name="atlas"/>.</summary>
        public static Measured Measure(ArtBuild.BuiltAtlas atlas, string id)
        {
            var m = new Measured();
            AtlasSprite sprite = atlas.Data.sprites.Find(s => s.id == id);
            if (sprite == null) return m;
            Color32 outline = StylePalettes.Charcoal.Colours[StylePalette.Outline];
            double light = 0;
            int plain = 0;
            for (int k = 0; k < sprite.frames * sprite.directions; k++)
            {
                AtlasFrame f = atlas.Data.frames[sprite.first + k];
                bool Solid(int x, int y) => x >= 0 && y >= 0 && x < f.w && y < f.h && atlas.Pixels[(f.y + y) * atlas.Data.width + f.x + x].a == 255;
                for (int y = 0; y < f.h; y++)
                    for (int x = 0; x < f.w; x++)
                    {
                        if (!Solid(x, y)) continue;
                        int at = (f.y + y) * atlas.Data.width + f.x + x;
                        Color32 c = atlas.Pixels[at];
                        m.Opaque++;
                        bool team = atlas.MaskPixels[at].r > 127;
                        if (team) m.Team++;
                        bool isOutline = c.r == outline.r && c.g == outline.g && c.b == outline.b;
                        // The outline is dark by design: the body inside it is what must stand off the floor.
                        if (!team && !isOutline) { light += PaletteQuantizer.OkLab(c).x; plain++; }
                        int neighbours = (Solid(x - 1, y) ? 1 : 0) + (Solid(x + 1, y) ? 1 : 0) + (Solid(x, y - 1) ? 1 : 0) + (Solid(x, y + 1) ? 1 : 0);
                        if (neighbours == 0) m.Orphans++;
                        if (neighbours == 4) continue;
                        m.Edge++;
                        if (!isOutline) m.OpenEdge++;
                    }
            }
            m.Lightness = plain > 0 ? (float)(light / plain) : 0f;
            return m;
        }

        /// <summary>The rules <paramref name="m"/> breaks, in a fixed order, separated by spaces ("" when clean).</summary>
        public static string Lint(Measured m, bool teamSprite, float floorLightness)
        {
            var broken = new List<string>();
            if (m.Orphans > 0) broken.Add("orphan");
            if (m.OpenShare > OutlineSlack) broken.Add("outline");
            if (teamSprite && (m.TeamShare < MinTeam || m.TeamShare > MaxTeam)) broken.Add("team");
            if (m.Lightness < floorLightness + MinLift) broken.Add("dark");
            return string.Join(" ", broken);
        }
    }
}
