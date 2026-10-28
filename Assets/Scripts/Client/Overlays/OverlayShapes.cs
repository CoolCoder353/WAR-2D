using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client.Render;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// Theme colours the world overlays use that <see cref="Palettes"/> doesn't carry (danger, readout gold, success),
    /// per palette as <c>Assets/UI/Theme.uss</c>'s <c>--color-*</c> tokens.
    /// </summary>
    public static class OverlayColours
    {
        /// <summary><c>--color-danger</c>.</summary>
        public static Color32 Danger(string palette) => palette switch
        {
            Palettes.Colourblind => new Color32(0xF0, 0x6B, 0xB8, 0xFF),
            Palettes.HighContrast => new Color32(0xFF, 0x4D, 0x4D, 0xFF),
            _ => new Color32(0xFF, 0x52, 0x63, 0xFF),
        };

        /// <summary><c>--color-readout</c>: the gold of the HUD readouts and the Builder's "help build" flag.</summary>
        public static Color32 Readout(string palette) => palette switch
        {
            Palettes.Colourblind => new Color32(0xF0, 0xE4, 0x42, 0xFF),
            Palettes.HighContrast => new Color32(0xFF, 0xE1, 0x4D, 0xFF),
            _ => new Color32(0xFF, 0xC8, 0x57, 0xFF),
        };

        /// <summary><c>--color-success</c>.</summary>
        public static Color32 Success(string palette) => palette switch
        {
            Palettes.Colourblind => new Color32(0x56, 0xB4, 0xE9, 0xFF),
            Palettes.HighContrast => new Color32(0x3D, 0xFF, 0x7A, 0xFF),
            _ => new Color32(0x5B, 0xE3, 0x7D, 0xFF),
        };

        /// <summary>The colour with its alpha scaled to <paramref name="a"/> (0–1).</summary>
        public static Color32 Alpha(Color32 c, float a)
        {
            c.a = (byte)math.round(255f * math.saturate(a));
            return c;
        }
    }

    /// <summary>Shapes the v0.15 overlays add on top of <see cref="OverlayMesh"/>'s quads: dashed rings, discs, hatches, flags, brackets.</summary>
    public static class OverlayShapes
    {
        /// <summary>A dashed ring: dashes of about <paramref name="dash"/> tiles of rim with equal gaps (16 to 256 dashes).</summary>
        public static void DashedRing(OverlayMesh m, float2 centre, float radius, float width, Color32 colour, float dash)
        {
            int dashes = math.clamp((int)math.round(2f * math.PI * radius / math.max(1e-3f, dash * 2f)), 16, 256);
            float inner = radius - width * 0.5f, outer = radius + width * 0.5f;
            for (int d = 0; d < dashes; d++)
            {
                float a0 = 2f * math.PI * d / dashes, a1 = a0 + math.PI / dashes;
                float2 d0 = new float2(math.cos(a0), math.sin(a0)), d1 = new float2(math.cos(a1), math.sin(a1));
                m.Quad(centre + d0 * inner, centre + d0 * outer, centre + d1 * outer, centre + d1 * inner, colour);
            }
        }

        /// <summary>A filled disc (a fan of degenerate quads).</summary>
        public static void Disc(OverlayMesh m, float2 centre, float radius, Color32 colour, int segments = 48)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = 2f * math.PI * i / segments, a1 = 2f * math.PI * (i + 1) / segments;
                float2 p0 = centre + new float2(math.cos(a0), math.sin(a0)) * radius, p1 = centre + new float2(math.cos(a1), math.sin(a1)) * radius;
                m.Quad(centre, p0, p1, centre, colour);
            }
        }

        /// <summary>Diagonal hatch lines clipped to a circle, <paramref name="spacing"/> tiles apart.</summary>
        public static void Hatch(OverlayMesh m, float2 centre, float radius, float spacing, float width, Color32 colour)
        {
            float2 normal = math.normalize(new float2(1f, -1f));
            for (float o = -radius + spacing * 0.5f; o < radius; o += spacing)
                if (OverlayRules.Chord(centre, radius, normal, o, out float2 a, out float2 b)) m.Segment(a, b, width, colour);
        }

        /// <summary>A small flag: a pole with a pennant to its right, its foot at <paramref name="foot"/>, <paramref name="h"/> tiles high.</summary>
        public static void Flag(OverlayMesh m, float2 foot, float h, float width, Color32 pole, Color32 pennant)
        {
            m.Segment(foot, foot + new float2(0f, h), width, pole);
            float2 top = foot + new float2(width * 0.5f, h);
            m.Quad(top, top + new float2(h * 0.55f, -h * 0.2f), top + new float2(0f, -h * 0.4f), top + new float2(0f, -h * 0.4f), pennant);
        }

        /// <summary>Four corner brackets around a point (the "can fire at" mark on a seen enemy).</summary>
        public static void Brackets(OverlayMesh m, float2 centre, float half, float arm, float width, Color32 colour)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                float2 corner = centre + new float2(sx * half, sy * half);
                m.Segment(corner, corner - new float2(sx * arm, 0f), width, colour);
                m.Segment(corner, corner - new float2(0f, sy * arm), width, colour);
            }
        }

        /// <summary>Crossed swords, drawn as an X with short guards (the Attack-move goal).</summary>
        public static void Swords(OverlayMesh m, float2 centre, float half, float width, Color32 colour)
        {
            m.Cross(centre, half, width, colour);
            float g = half * 0.45f;
            float2 p = centre + new float2(-half, -half) * 0.55f, q = centre + new float2(half, -half) * 0.55f;
            m.Segment(p + new float2(-g, g) * 0.5f, p + new float2(g, -g) * 0.5f, width, colour);
            m.Segment(q + new float2(-g, -g) * 0.5f, q + new float2(g, g) * 0.5f, width, colour);
        }
    }
}
