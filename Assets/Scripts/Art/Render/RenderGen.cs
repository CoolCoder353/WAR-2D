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
        private static Material Plate => new Material(C(StylePalette.Body3) * 1.15f, 0.35f, 36);
        private static Material Steel => new Material(C(StylePalette.Body3) * 1.55f, 0.6f, 60);
        private static Material Dark => new Material(C(StylePalette.Body0), 0.15f, 12);
        private static Material Team => new Material(Color.white, 0.45f, 40, team: true);
        private static Material Lamp => new Material(C(StylePalette.Lamp) * 0.9f, 0.1f, 20, glow: 0.45f);
        private static Material Concrete => new Material(C(StylePalette.Body3) * 1.3f, 0.05f, 6);

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

        /// <summary>
        /// The Miner (1×1): a small tracked rig whose boom reaches east to a big spiral auger at the gem face,
        /// with ore spilling beside it and a hopper on the rig. Frames turn the auger.
        /// </summary>
        public static LitCanvas Miner(int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            // Tracks and rig body (west), with a team cab and a hopper of ore.
            foreach (float s in new[] { -1f, 1f })
            {
                c.Shape((x, y) => Box(x, y, o - 14, o + s * 12, 11, 4, 2), Rubber, 6, 1.5f, 2);
                c.Groove((x, y) => Box(x, y, o - 14, o + s * 12, 10, 3) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.7f, 1, 0.8f);
            }
            c.Shape((x, y) => Box(x, y, o - 14, o, 10, 9, 2.5f), Hull, 12, 2, 2.5f);
            c.Shape((x, y) => Box(x, y, o - 18, o + 4, 5, 5, 1.5f), Team, 16, 1.5f, 2);
            c.Shape((x, y) => Box(x, y, o - 11, o - 4, 5, 4, 1), Dark, 9, 1, 1);
            c.Shape((x, y) => Polygon(x, y, o - 12, o - 4, 2.4f, 5, 0.2f), Ore, 12, 1.2f, 1.5f);
            c.Shape((x, y) => Polygon(x, y, o - 9, o - 3, 2f, 6, 0.9f), Ore, 11.5f, 1.2f, 1.5f);
            // Boom with a hydraulic ram.
            c.Shape((x, y) => Capsule(x, y, o - 6, o, o + 16, o, 3.4f), Plate, 15, 1.5f, 2);
            c.Shape((x, y) => Capsule(x, y, o - 4, o - 5, o + 10, o - 2, 1.4f), Steel, 16, 0.8f, 1);
            // The auger: a big steel disc with spiral flutes, turning with the frame.
            float ax = o + 20, ay = o;
            c.Shape((x, y) => Circle(x, y, ax, ay, 11), Steel, 20, 6, 8);
            float phase = frame * Mathf.PI / 2;
            c.Groove((x, y) =>
            {
                float px = x - ax, py = y - ay, r = Mathf.Sqrt(px * px + py * py);
                return r > 10.5f || r < 2.5f ? 9 : Mathf.Abs(Mathf.Sin(Mathf.Atan2(py, px) * 2 - r * 0.45f + phase)) - 0.45f;
            }, 2.2f, 1.2f);
            c.Shape((x, y) => Circle(x, y, ax, ay, 2.6f), Dark, 21, 1, 1);
            // Ore spilling beside the auger.
            foreach (var (gx, gy, r) in new[] { (o + 8f, o - 12f, 2.4f), (o + 13f, o - 15f, 1.8f), (o + 10f, o + 13f, 2.1f) })
                c.Shape((x, y) => Polygon(x, y, gx, gy, r, 6, gx), Ore, 5, 1.2f, 1.5f);
            c.Shape((x, y) => Circle(x, y, o - 22, o - 7, 1.4f), Lamp, 13, 0.5f, 0.3f);
            c.Grime(11, 0.2f, 0.08f, 6);
            return c;
        }

        private static Material Ore => new Material(C(StylePalette.Gem1), 0.8f, 70, glow: 0.3f);
        private static Material Hazard => new Material(C(StylePalette.Warn) * 0.95f, 0.15f, 16);
        private static Material Glass => new Material(new Color(0.22f, 0.36f, 0.42f), 0.9f, 90);

        /// <summary>Hazard stripes (warning yellow and dark) inside <paramref name="sdf"/>.</summary>
        private static void Stripes(LitCanvas c, Func<float, float, float> sdf, float top)
        {
            c.Shape(sdf, Hazard, top, 0.8f, 0.8f);
            c.Shape((x, y) => Mathf.Max(sdf(x, y), Mathf.Abs(Mod(x + y, 12) - 6) - 3), Dark, top, 0.4f, 0.3f);
        }

        /// <summary>
        /// The Small Unit Spawner (2×2): a vehicle factory. A hall with a sawtooth roof (lit slopes and
        /// skylights), an office annex, a tall smokestack, fuel tanks outside, and a striped loading apron
        /// in front of the roller door, where vehicles come out. Frame 1 dims the door lamp.
        /// </summary>
        public static LitCanvas Spawner(int frame)
        {
            int s = 2 * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f;
            // Loading apron with stripes along its front edge.
            c.Shape((x, y) => Box(x, y, o - 14, o - 42, 22, 14, 2), Concrete, 3, 1, 1.5f);
            Stripes(c, (x, y) => Box(x, y, o - 14, o - 53, 20, 3), 3.5f);
            // Hall: a sawtooth roof, rising in each bay toward a skylight strip.
            float hx = o - 14, hy = o + 10, period = 14;
            Func<float, float, float> hall = (x, y) => Box(x, y, hx, hy, 34, 32, 2);
            c.Shape(hall, Plate, 20, 1.5f, 3);
            c.Displace((x, y) => hall(x, y) > -2 ? 0 : 7 * Mod(y - (hy - 32), period) / period);
            c.Tint((x, y, col) => hall(x, y) > -2 ? col : Mod(y - (hy - 32), period) > period - 3 ? Glass.Albedo : col);
            c.Groove((x, y) => hall(x, y) > -2 ? 9 : Mathf.Abs(Mod(y - (hy - 32), period) - period + 0.5f) - 0.5f, 1.5f, 1f);
            // Roller door at the hall's south wall, and its lamp.
            c.Shape((x, y) => Box(x, y, hx, hy - 34, 15, 3, 1), Dark, 10, 1, 1);
            c.Groove((x, y) => Box(x, y, hx, hy - 34, 14, 2) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.7f, 0.8f, 0.8f);
            c.Shape((x, y) => Circle(x, y, hx + 19, hy - 33, 1.8f), frame == 0 ? Lamp : Plate, 14, 0.6f, 0.4f);
            // Team stripe along the hall's west wall.
            c.Shape((x, y) => Box(x, y, hx - 33, hy, 2.5f, 28, 1), Team, 22, 1, 1);
            // Office annex (east) with an AC unit and a team roof plate.
            c.Shape((x, y) => Box(x, y, o + 34, o + 24, 14, 18, 2.5f), Hull, 16, 2, 2.5f);
            c.Shape((x, y) => Box(x, y, o + 34, o + 32, 9, 5, 1.5f), Team, 17.5f, 1, 1.2f);
            c.Shape((x, y) => Box(x, y, o + 34, o + 14, 5, 4, 1), Plate, 19, 1, 1.5f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 34, o + 14, 2.5f)) - 0.2f, 0.8f, 0.6f);
            // Smokestack (north-east of the hall), tall, with a dark mouth and a band.
            c.Shape((x, y) => Circle(x, y, o + 16, o + 50, 8), Steel, 40, 2.5f, 4);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 16, o + 50, 6.5f)) - 0.3f, 1, 0.8f);
            c.Shape((x, y) => Circle(x, y, o + 16, o + 50, 4.5f), Dark, 34, 1, 1);
            // Fuel tanks (south-east) on short legs, piped to the annex.
            foreach (float ty in new[] { o - 14f, o - 38f })
            {
                c.Shape((x, y) => Circle(x, y, o + 38, ty, 10), Plate, 22, 8, 12);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 38, ty, 6)) - 0.2f, 0.8f, 0.6f);
            }
            c.Shape((x, y) => Mathf.Min(Capsule(x, y, o + 38, o - 38, o + 38, o + 6, 1.8f), Capsule(x, y, o + 28, o - 14, o + 20, o - 14, 1.8f)), Steel, 12, 1, 1.2f);
            c.Grime(23, 0.2f, 0.08f, 8);
            return c;
        }

        /// <summary>
        /// The HQ (3×3): a command compound. A cross-shaped command building with the team's roof marking and
        /// AC units, a lattice comms tower with a dish and a beacon, a round helipad, and a garage annex,
        /// joined by walkways, with crates and fuel drums.
        /// </summary>
        public static LitCanvas Base()
        {
            int s = 3 * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f;
            // Walkways between the parts.
            c.Shape((x, y) => Mathf.Min(Mathf.Min(
                Capsule(x, y, o - 8, o + 4, o - 56, o + 46, 4),
                Capsule(x, y, o - 8, o + 4, o + 50, o - 40, 4)),
                Capsule(x, y, o - 8, o + 4, o + 54, o + 52, 4)), Concrete, 2.5f, 1, 1);
            // Helipad (north-west).
            c.Shape((x, y) => Circle(x, y, o - 56, o + 46, 24), Concrete, 4, 1.5f, 2);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o - 56, o + 46, 19)) - 0.3f, 0.8f, 1f);
            c.Shape((x, y) => Mathf.Min(Mathf.Min(Box(x, y, o - 63, o + 46, 2.2f, 10), Box(x, y, o - 49, o + 46, 2.2f, 10)), Box(x, y, o - 56, o + 46, 7, 2)), Hazard, 4.6f, 0.4f, 0.4f);
            // Command building: a cross of two blocks with a parapet, the team chevron and AC units.
            Func<float, float, float> hq = (x, y) => Mathf.Min(Box(x, y, o - 8, o + 4, 38, 20, 3), Box(x, y, o - 8, o + 4, 20, 36, 3));
            c.Shape(hq, Hull, 26, 3, 4);
            c.Groove((x, y) => hq(x, y) + 4, 1.5f, 1.2f);
            c.Shape((x, y) => Box(x, y, o - 8, o + 4, 13, 13, 2), Team, 27.5f, 1.5f, 1.5f);
            c.Groove((x, y) => Box(x, y, o - 8, o + 4, 10, 10) > 0 ? 9 : Mathf.Abs(Mathf.Abs(x - (o - 8)) * 0.9f + (y - o - 4) - 2) - 1.5f, 1.6f, 1.4f);
            foreach (var (ax, ay) in new[] { (o - 36f, o + 10f), (o + 20f, o + 10f), (o - 8f, o + 32f) })
            {
                c.Shape((x, y) => Box(x, y, ax, ay, 6, 5, 1.2f), Plate, 30, 1.2f, 2);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, ax, ay, 3)) - 0.3f, 0.9f, 0.7f);
            }
            // Comms tower (north-east): a lattice square with cross braces, a dish and a beacon.
            float tx = o + 54, ty = o + 52;
            c.Shape((x, y) => Mathf.Max(Box(x, y, tx, ty, 11, 11, 1), -Box(x, y, tx, ty, 8, 8)), Steel, 34, 0.8f, 1.5f);
            foreach (float sgn in new[] { -1f, 1f })
                c.Shape((x, y) => Capsule(x, y, tx - 9, ty - 9 * sgn, tx + 9, ty + 9 * sgn, 1.4f), Steel, 33, 0.8f, 1);
            c.Shape((x, y) => Circle(x, y, tx - 14, ty - 12, 11), Plate, 30, 10, 7);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, tx - 14, ty - 12, 6)) - 0.3f, 0.8f, 0.6f);
            c.Shape((x, y) => Circle(x, y, tx, ty, 2.5f), Lamp, 36, 0.8f, 0.5f);
            // Garage (south-east) with a roller door and a striped apron.
            c.Shape((x, y) => Box(x, y, o + 50, o - 40, 20, 15, 2.5f), Hull, 18, 2, 2.5f);
            c.Shape((x, y) => Box(x, y, o + 50, o - 40, 14, 9, 1.5f), Plate, 19, 1, 1);
            c.Shape((x, y) => Box(x, y, o + 50, o - 56, 15, 2.5f, 0.6f), Dark, 9, 0.6f, 0.6f);
            c.Groove((x, y) => Box(x, y, o + 50, o - 56, 14, 2) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.7f, 0.8f, 0.8f);
            Stripes(c, (x, y) => Box(x, y, o + 50, o - 64, 17, 3.5f), 3.5f);
            // Supply crates (south-west) and fuel drums (by the helipad).
            foreach (var (bx, by) in new[] { (-50f, -46f), (-38f, -46f), (-44f, -34f) })
            {
                c.Shape((x, y) => Box(x, y, o + bx, o + by, 5.5f, 5.5f, 1), new Material(new Color(0.42f, 0.33f, 0.22f), 0.08f, 8), 10, 1, 1.5f);
                c.Groove((x, y) => Box(x, y, o + bx, o + by, 4, 4) > 0 ? 9 : Mathf.Min(Mathf.Abs(x - o - bx - (y - o - by)), Mathf.Abs(x - o - bx + (y - o - by))) - 0.6f, 0.8f, 0.6f);
            }
            foreach (var (fx, fy) in new[] { (-76f, 12f), (-66f, 12f), (-71f, 3f) })
            {
                c.Shape((x, y) => Circle(x, y, o + fx, o + fy, 4.5f), new Material(new Color(0.55f, 0.18f, 0.14f), 0.4f, 30), 9, 2, 2.5f);
                c.Rivet(o + fx, o + fy, 1f, 0.6f);
            }
            c.Grime(31, 0.2f, 0.08f, 10);
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
