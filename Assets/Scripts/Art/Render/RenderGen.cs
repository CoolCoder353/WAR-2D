using System;
using UnityEngine;
using WAR2D.Art.Style;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// Sprites in the owner's chosen look (2026-10-11): "Factorio combined with the current style", in the
    /// Charcoal palette. 64 px per tile, lit from the top left, with bevelled plates, panel lines, rivets,
    /// grime and drop shadows. Units are rendered per facing (the light stays fixed, so facings are not
    /// rotated copies). Deterministic per seed.
    /// </summary>
    public static class RenderGen
    {
        public const int TileSize = 64;
        public const int North = 1, East = 2, South = 4, West = 8;

        private static StylePalette P => StylePalettes.Charcoal;
        private static Color C(byte role) => P.Colours[role];

        // Materials.
        private static Material Rubber => new Material(C(StylePalette.Body0) * 0.85f, 0.08f, 8);
        private static Material Hull => new Material(C(StylePalette.Body1) * 1.1f, 0.3f, 28);
        private static Material Plate => new Material(C(StylePalette.Body2) * 1.05f, 0.4f, 36);
        private static Material Steel => new Material(C(StylePalette.Body3), 0.7f, 60);
        private static Material Dark => new Material(C(StylePalette.Body0), 0.15f, 12);
        private static Material Team => new Material(Color.white, 0.45f, 40, team: true);
        private static Material Lamp => new Material(C(StylePalette.Lamp) * 0.9f, 0.1f, 20, glow: 0.45f);
        private static Material Concrete => new Material(C(StylePalette.Floor2) * 1.08f, 0.05f, 6);

        // ---------- Signed distance helpers (negative inside) ----------

        public static float Box(float x, float y, float cx, float cy, float hx, float hy, float r = 0)
        {
            float qx = Mathf.Abs(x - cx) - (hx - r), qy = Mathf.Abs(y - cy) - (hy - r);
            return new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
        }

        public static float Circle(float x, float y, float cx, float cy, float r) => new Vector2(x - cx, y - cy).magnitude - r;

        public static float Capsule(float x, float y, float ax, float ay, float bx, float by, float r)
        {
            var pa = new Vector2(x - ax, y - ay);
            var ba = new Vector2(bx - ax, by - ay);
            float t = Mathf.Clamp01(Vector2.Dot(pa, ba) / ba.sqrMagnitude);
            return (pa - ba * t).magnitude - r;
        }

        /// <summary>A regular polygon with <paramref name="sides"/> sides, circumradius <paramref name="r"/>, rotated by <paramref name="angle"/>.</summary>
        public static float Polygon(float x, float y, float cx, float cy, float r, int sides, float angle)
        {
            float px = x - cx, py = y - cy;
            float a = Mathf.Atan2(py, px) - angle, seg = 2 * Mathf.PI / sides;
            float k = Mathf.Cos(Mathf.Floor(0.5f + a / seg) * seg - a) * new Vector2(px, py).magnitude;
            return k - r * Mathf.Cos(Mathf.PI / sides);
        }

        // ---------- Units ----------

        /// <summary>The Tank facing <paramref name="angle"/> (radians, 0 = east). Frames 0–1 move, 2–3 fire.</summary>
        public static LitCanvas Tank(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle), o = TileSize / 2f;
            // Local (tank) coordinates of an output point: x forward, y left.
            Func<float, float, float> L(Func<float, float, float> local) =>
                (x, y) => local((x - o) * cos + (y - o) * sin, -(x - o) * sin + (y - o) * cos);
            float tread = frame == 1 ? 2f : 0f, recoil = frame == 2 ? 3f : 0f;

            // Tracks, with link grooves that advance with the move frame.
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape(L((x, y) => Box(x, y, 0, side * 17, 25, 6.5f, 3)), Rubber, 6, 2, 3);
                c.Groove(L((x, y) => Box(x, y, 0, side * 17, 24, 5.5f) > 0 ? 9 : Mathf.Abs(Mod(x + tread, 4) - 2) - 1.2f), 1.2f, 0.7f);
            }
            // Hull, with a raised front plate and a rear engine grille.
            c.Shape(L((x, y) => Box(x, y, 0, 0, 22, 13, 5)), Hull, 10, 3, 3.5f);
            c.Shape(L((x, y) => Box(x, y, 14, 0, 7, 10, 3)), Plate, 1.2f, 1, 1, stack: true);
            c.Groove(L((x, y) => Box(x, y, -16, 0, 4, 8) > 0 ? 9 : Mathf.Abs(Mod(x, 2) - 1) - 0.45f), 1, 0.45f);
            foreach (float s in new[] { -1f, 1f })
                for (int k = -1; k <= 1; k++)
                {
                    float lx = k * 8, ly = s * 11;
                    c.Rivet(o + lx * cos - ly * sin, o + lx * sin + ly * cos, 0.8f, 0.7f);
                }
            // Rear lamps.
            foreach (float s in new[] { -1f, 1f }) c.Shape(L((x, y) => Circle(x, y, -21, s * 9, 1.6f)), Lamp, 10.5f, 0.6f, 0.4f);
            // Turret (team colour) with a hatch, and the barrel.
            c.Shape(L((x, y) => Box(x, y, -3, 0, 11, 10, 5)), Team, 17, 3, 4);
            c.Shape(L((x, y) => Circle(x, y, -6, 4, 3.4f)), Plate, 18, 1, 1);
            c.Groove(L((x, y) => Circle(x, y, -6, 4, 2.2f)), 0.6f, 0.4f);
            c.Shape(L((x, y) => Box(x, y, 18 - recoil, 0, 12, 2.1f, 1.8f)), Steel, 16, 1.8f, 2.2f);
            c.Shape(L((x, y) => Box(x, y, 29 - recoil, 0, 2, 3, 1)), Steel, 16, 1, 1.5f);
            c.Grime(7, 0.3f, 0.12f, 6);
            if (frame >= 2)
            {
                float size = frame == 2 ? 5f : 3f;
                c.Shape(L((x, y) => Circle(x, y, 33 - recoil + size * 0.5f, 0, size)), new Material(C(StylePalette.Warn), 0, 1, glow: 2.2f), 18, 2, 0);
                c.Shape(L((x, y) => Circle(x, y, 33 - recoil + size * 0.5f, 0, size * 0.45f)), new Material(Color.white, 0, 1, glow: 2.5f), 18.5f, 1, 0);
            }
            return c;
        }

        /// <summary>The Miner (1×1), its drill arm facing east; frames turn the drill's spiral.</summary>
        public static LitCanvas Miner(int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            foreach (var (fx, fy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                c.Shape((x, y) => Capsule(x, y, o, o, o + fx * 21, o + fy * 21, 3.2f), Dark, 6, 2, 2);
                c.Shape((x, y) => Circle(x, y, o + fx * 22, o + fy * 22, 5.5f), Plate, 8, 2, 3);
                c.Rivet(o + fx * 22, o + fy * 22, 1.6f, 1f, 0.6f);
            }
            c.Shape((x, y) => Box(x, y, o, o, 15, 15, 5), Hull, 11, 3, 3);
            foreach (float s in new[] { -1f, 1f }) c.Shape((x, y) => Box(x, y, o - 6, o + s * 10, 7, 3, 1.5f), Team, 12.5f, 1, 1);
            // Arm toward the gem (east).
            c.Shape((x, y) => Box(x, y, o + 20, o, 11, 4, 2), Plate, 12, 1.5f, 2);
            c.Shape((x, y) => Circle(x, y, o + 29, o, 4.5f), Steel, 13, 2, 3);
            // The drill head: a spiral of grooves turning a quarter per frame.
            c.Shape((x, y) => Circle(x, y, o, o, 9), Steel, 18, 6, 6);
            float phase = frame * Mathf.PI / 2;
            c.Groove((x, y) =>
            {
                float dx = x - o, dy = y - o, r = Mathf.Sqrt(dx * dx + dy * dy);
                return r > 8.5f ? 9 : Mathf.Abs(Mathf.Sin(Mathf.Atan2(dy, dx) * 3 + r * 0.7f + phase)) - 0.35f;
            }, 1.2f, 0.4f);
            c.Shape((x, y) => Circle(x, y, o - 12, o - 11, 1.5f), Lamp, 11.5f, 0.5f, 0.3f);
            c.Grime(11, 0.3f, 0.12f, 6);
            return c;
        }

        /// <summary>The Small Unit Spawner (2×2): a factory block with roof vents, a stack and a lit bay door; frame 1 dims the bay.</summary>
        public static LitCanvas Spawner(int frame)
        {
            int s = 2 * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f;
            c.Shape((x, y) => Box(x, y, o, o, 60, 60, 8), Concrete, 3, 2, 2);
            c.Groove((x, y) => Box(x, y, o, o, 56, 56, 6), 0.8f, 0.6f);
            c.Shape((x, y) => Box(x, y, o, o + 6, 50, 44, 7), Hull, 22, 4, 5);
            // Roof seams.
            c.Groove((x, y) => Mathf.Abs(x - o) - 0.1f, 1, 0.7f);
            c.Groove((x, y) => Mathf.Abs(y - (o + 18)) - 0.1f, 1, 0.7f);
            // Vents with grilles.
            foreach (float vx in new[] { o - 26, o + 26 })
            {
                c.Shape((x, y) => Box(x, y, vx, o + 30, 12, 9, 3), Plate, 25, 2, 2);
                c.Groove((x, y) => Box(x, y, vx, o + 30, 10, 7) > 0 ? 9 : Mathf.Abs(Mod(y, 3) - 1.5f) - 0.6f, 1, 0.5f);
            }
            // Team panels on the roof.
            c.Shape((x, y) => Box(x, y, o - 30, o - 2, 14, 10, 2.5f), Team, 24, 1.5f, 1.5f);
            c.Shape((x, y) => Box(x, y, o + 30, o - 2, 14, 10, 2.5f), Team, 24, 1.5f, 1.5f);
            // Stack.
            c.Shape((x, y) => Circle(x, y, o + 40, o + 40, 8), Steel, 32, 3, 3);
            c.Shape((x, y) => Circle(x, y, o + 40, o + 40, 4.5f), Dark, 27, 1, 1);
            // Bay door (south) with its lamp.
            c.Shape((x, y) => Box(x, y, o, o - 38, 22, 9, 2), Dark, 9, 1.5f, 1.5f);
            c.Groove((x, y) => Box(x, y, o, o - 38, 20, 7) > 0 ? 9 : Mathf.Abs(Mod(x, 6) - 3) - 0.5f, 0.8f, 0.5f);
            c.Shape((x, y) => Box(x, y, o, o - 26, 10, 2.2f, 1), frame == 0 ? Lamp : Plate, 23, 1, 1);
            for (int k = -2; k <= 2; k++) { c.Rivet(o + k * 20, o + 47, 1.1f, 1); c.Rivet(o + k * 20, o - 35 + 0, 0.9f, 0.8f); }
            c.Grime(23, 0.35f, 0.14f, 8);
            return c;
        }

        /// <summary>The HQ (3×3): a command hull with a team-coloured dome, four lamp towers and a landing pad.</summary>
        public static LitCanvas Base()
        {
            int s = 3 * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f;
            c.Shape((x, y) => Mathf.Max(Box(x, y, o, o, 92, 92, 10), Polygon(x, y, o, o, 100, 8, Mathf.PI / 8)), Concrete, 3, 2, 2);
            c.Groove((x, y) => Mathf.Max(Box(x, y, o, o, 86, 86, 8), Polygon(x, y, o, o, 94, 8, Mathf.PI / 8)), 0.8f, 0.7f);
            c.Shape((x, y) => Box(x, y, o, o, 66, 66, 16), Hull, 18, 6, 6);
            c.Groove((x, y) => Box(x, y, o, o, 56, 56, 12), 1.2f, 0.8f);
            for (int k = 0; k < 4; k++)
            {
                float a = k * Mathf.PI / 2 + Mathf.PI / 4;
                c.Groove((x, y) => Capsule(x, y, o + Mathf.Cos(a) * 34, o + Mathf.Sin(a) * 34, o + Mathf.Cos(a) * 62, o + Mathf.Sin(a) * 62, 0.1f), 1, 0.7f);
            }
            // Four lamp towers.
            foreach (var (tx, ty) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                float cx = o + tx * 66, cy = o + ty * 66;
                c.Shape((x, y) => Circle(x, y, cx, cy, 13), Plate, 28, 4, 5);
                c.Shape((x, y) => Circle(x, y, cx, cy, 5), Lamp, 30, 2, 1);
                for (int k = 0; k < 6; k++) c.Rivet(cx + Mathf.Cos(k * 1.047f) * 9.5f, cy + Mathf.Sin(k * 1.047f) * 9.5f, 1, 0.8f);
            }
            // The dome, in team colour, with rings.
            c.Shape((x, y) => Circle(x, y, o, o, 34), Team, 44, 26, 24);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o, o, 22)) - 0.1f, 1, 0.8f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o, o, 11)) - 0.1f, 1, 0.8f);
            c.Shape((x, y) => Circle(x, y, o, o, 5), Steel, 48, 3, 2);
            // Antenna mast toward the north-east.
            c.Shape((x, y) => Capsule(x, y, o + 20, o + 20, o + 44, o + 44, 2.2f), Steel, 34, 1.5f, 1.5f);
            c.Shape((x, y) => Circle(x, y, o + 44, o + 44, 3.5f), Lamp, 35, 1, 1);
            c.Grime(31, 0.4f, 0.14f, 10);
            return c;
        }

        // ---------- Terrain ----------

        /// <summary>Stone floor: mottled, pebbled and cracked; tiles seamlessly.</summary>
        public static LitCanvas Floor(int seed, int variant)
        {
            var c = new LitCanvas(TileSize, TileSize);
            c.Ground(new Material(C(StylePalette.Floor1) * 1.15f, 0.06f, 8));
            int s = seed + variant * 977;
            c.Grime(s, 0.9f, 0.16f, 4, 5);
            // Fine cracks along a noise contour, broken up by a second noise so they don't form loops.
            c.Groove((x, y) => Noise.Fbm(x, y, TileSize, 2, 2, s + 9) < 0.55f ? 9 : (Mathf.Abs(Noise.Fbm(x, y, TileSize, 5, 3, s + 5) - 0.5f) - 0.006f) * 60, 0.9f, 0.45f);
            var rng = new Generators.ArtRandom(seed, "pebbles" + variant);
            for (int k = 0; k < 6; k++) c.Rivet(4 + rng.Range(TileSize - 8), 4 + rng.Range(TileSize - 8), 0.8f + rng.Range(2) * 0.6f, 0.9f, 0.05f);
            return c;
        }

        /// <summary>
        /// Rock for a 4-neighbour mask (bit set = that neighbour is rock): a craggy raised mass whose cliffs
        /// face the open sides, with rounded outside corners and a noisy (but tile-matching) edge.
        /// </summary>
        public static LitCanvas Rock(int seed, int mask)
        {
            LitCanvas c = Floor(seed, mask & 3);
            var rock = new Material(C(StylePalette.Rock2) * 1.35f, 0.08f, 10);
            float Inside(float x, float y) => EdgeDistance(x, y, mask) - 6 - 5 * Noise.Fbm(x, y, TileSize, 4, 3, seed + 41);
            c.Shape((x, y) => -Inside(x, y), rock, 22, 12, 20);
            // Craggy top: ridged noise lit from above, darker in the cracks.
            float Ridge(float x, float y) => 1 - Mathf.Abs(Noise.Fbm(x, y, TileSize, 4, 4, seed + 77) * 2 - 1);
            c.Displace((x, y) => Inside(x, y) <= 0 ? 0 : Mathf.Min(1, Inside(x, y) / 10) * 10 * Ridge(x, y));
            // Crevices darker and browner, ridges lighter.
            Color crevice = C(StylePalette.Rock0) * 1.4f;
            c.Tint((x, y, col) => Inside(x, y) <= 0 ? col
                : Color.Lerp(crevice, col * (0.9f + 0.25f * Noise.Fbm(x, y, TileSize, 6, 3, seed + 99)), Mathf.SmoothStep(0.15f, 0.75f, Ridge(x, y))));
            return c;
        }

        /// <summary>Rock with glowing faceted gem crystals set into it.</summary>
        public static LitCanvas Gem(int seed, int variant, int mask = North | East | South | West)
        {
            LitCanvas c = Rock(seed, mask);
            var rng = new Generators.ArtRandom(seed, "crystals" + variant);
            int count = 3 + rng.Range(3);
            for (int k = 0; k < count; k++)
            {
                float cx = 14 + rng.Range(TileSize - 28), cy = 14 + rng.Range(TileSize - 28);
                if (EdgeDistance(cx, cy, mask) < 14) continue;
                float r = 4 + rng.Range(4), a = rng.Range(60) * Mathf.Deg2Rad;
                Color tint = Color.Lerp(C(StylePalette.Gem1), C(StylePalette.Gem2), rng.Range(100) / 100f);
                c.Shape((x, y) => Polygon(x, y, cx, cy, r, 6, a), new Material(tint, 0.9f, 80, glow: 0.35f), 34 + r, r, r * 1.6f);
            }
            return c;
        }

        /// <summary>Distance (px) to the nearest side open to floor, with rounded outside corners; large when enclosed.</summary>
        private static float EdgeDistance(float x, float y, int mask)
        {
            float s = TileSize, d = 999;
            bool n = (mask & North) == 0, e = (mask & East) == 0, so = (mask & South) == 0, w = (mask & West) == 0;
            if (n) d = Mathf.Min(d, s - y);
            if (so) d = Mathf.Min(d, y);
            if (w) d = Mathf.Min(d, x);
            if (e) d = Mathf.Min(d, s - x);
            const float r = 24;
            float Corner(float cx, float cy, bool left, bool down)
            {
                float ox = left ? cx - x : x - cx, oy = down ? cy - y : y - cy;
                if (ox <= 0 || oy <= 0) return 999;
                return r - Mathf.Sqrt(ox * ox + oy * oy);
            }
            if (n && w) d = Mathf.Min(d, Corner(r, s - r, true, false));
            if (n && e) d = Mathf.Min(d, Corner(s - r, s - r, false, false));
            if (so && w) d = Mathf.Min(d, Corner(r, r, true, true));
            if (so && e) d = Mathf.Min(d, Corner(s - r, r, false, true));
            return d;
        }

        private static float Mod(float a, float m) => a - m * Mathf.Floor(a / m);
    }
}
