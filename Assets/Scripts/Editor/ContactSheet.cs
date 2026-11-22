using System;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// A contact sheet (asset-generation spec §3.3): a sprite as it stands in a match, on real floor and on rock, in four
    /// player colours and up to four facings, at 1 px per pixel. The review page shows it at near, default and far zoom,
    /// which is how a sprite is judged before the owner is asked. The team tint is the shaders' rule in gamma space
    /// (the mask step over the middle step, times the owner's colour), close to but not exactly the linear-space result.
    /// </summary>
    public static class ContactSheet
    {
        /// <summary>Pixels of ground round each sprite.</summary>
        public const int Margin = 12;

        /// <summary>Player colours shown (the first of the standard palette).</summary>
        public const int Colours = 4;

        /// <summary>
        /// Sprite <paramref name="id"/> of <paramref name="atlas"/> over the ground of <paramref name="tiles"/>: the top band on
        /// floor, the bottom on rock; a column per facing (or frame, for a sprite with one facing), repeated per colour.
        /// Null when the atlas has no such sprite.
        /// </summary>
        public static Color32[] Render(ArtBuild.BuiltAtlas atlas, ArtBuild.BuiltAtlas tiles, string id, out int width, out int height)
        {
            width = height = 0;
            AtlasSprite sprite = atlas.Data.sprites.Find(s => s.id == id);
            if (sprite == null) return null;
            int cells = sprite.frames * sprite.directions, cw = 0, ch = 0;
            for (int k = 0; k < cells; k++)
            {
                cw = Math.Max(cw, atlas.Data.frames[sprite.first + k].w);
                ch = Math.Max(ch, atlas.Data.frames[sprite.first + k].h);
            }
            // Four facings a quarter turn apart, or the first four frames of a sprite that has one facing.
            int shown = sprite.directions >= 4 ? 4 : Math.Min(4, sprite.frames);
            int columns = shown * Colours, stepX = cw + Margin, bandH = ch + 2 * Margin;
            width = columns * stepX + Margin;
            height = 2 * bandH;
            var sheet = new Color32[width * height];
            Ground(sheet, width, 0, bandH, tiles, nameof(TileType.Border)); // bottom band: rock
            Ground(sheet, width, bandH, bandH, tiles, nameof(TileType.Ground)); // top band: floor
            for (int band = 0; band < 2; band++)
                for (int column = 0; column < columns; column++)
                {
                    int view = column % shown, colour = column / shown;
                    int cell = sprite.directions >= 4 ? view * (sprite.directions / 4) * sprite.frames : view;
                    AtlasFrame f = atlas.Data.frames[sprite.first + cell];
                    Color32 team = Palettes.Player(Palettes.Standard, colour);
                    int ox = Margin + column * stepX + (cw - f.w) / 2, oy = band * bandH + Margin + (ch - f.h) / 2;
                    for (int y = 0; y < f.h; y++)
                        for (int x = 0; x < f.w; x++)
                        {
                            int at = (f.y + y) * atlas.Data.width + f.x + x;
                            Color32 c = atlas.Pixels[at];
                            if (c.a == 0) continue;
                            if (atlas.MaskPixels[at].r > 127) c = Tint(c, team);
                            int to = (oy + y) * width + ox + x;
                            sheet[to] = Over(c, sheet[to]);
                        }
                }
            return sheet;
        }

        /// <summary>A team pixel in the owner's colour: its grey step over the middle step, times the colour.</summary>
        public static Color32 Tint(Color32 grey, Color32 team)
        {
            float step = grey.r / (float)ArtStyle.TeamGrey[ArtStyle.TeamGrey.Length / 2].r;
            return new Color32(Channel(team.r * step), Channel(team.g * step), Channel(team.b * step), grey.a);
        }

        private static byte Channel(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);

        private static Color32 Over(Color32 top, Color32 under)
        {
            if (top.a == 255) return top;
            float a = top.a / 255f;
            return new Color32(Channel(top.r * a + under.r * (1 - a)), Channel(top.g * a + under.g * (1 - a)), Channel(top.b * a + under.b * (1 - a)), 255);
        }

        /// <summary>Fills rows <paramref name="y0"/> to <paramref name="y0"/> + <paramref name="h"/> with the tile sprite's frames, tiled.</summary>
        private static void Ground(Color32[] sheet, int width, int y0, int h, ArtBuild.BuiltAtlas tiles, string tile)
        {
            AtlasSprite sprite = tiles.Data.sprites.Find(s => s.id == tile);
            int variants = sprite.frames * sprite.directions;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < width; x++)
                {
                    int tx = x / ArtStyle.TileSize, ty = y / ArtStyle.TileSize;
                    AtlasFrame f = tiles.Data.frames[sprite.first + (tx * 7 + ty * 3) % variants];
                    Color32 c = tiles.Pixels[(f.y + y % f.h) * tiles.Data.width + f.x + x % f.w];
                    c.a = 255;
                    sheet[(y0 + y) * width + x] = c;
                }
        }
    }
}
