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
    public static partial class RenderGen
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
        /// The Miner (1×1): a small tracked rig whose boom reaches out to a big spiral auger at the gem face,
        /// with ore spilling beside it and a hopper on the rig. Frames turn the auger. <paramref name="facing"/>
        /// turns the rig's ground plan in 90° steps anticlockwise from east (the building's rotation), so the
        /// light and the baked shadow stay the same way round in every facing.
        /// </summary>
        public static LitCanvas Miner(int frame, int facing = 0)
        {
            var c = new LitCanvas(TileSize, TileSize + Headroom / 3, TileSize / 2f);
            float o = TileSize / 2f;
            // Canvas point → the rig's own plan (drawn facing east), and back for point features.
            float angle = -(((facing % 4) + 4) % 4) * Mathf.PI / 2f, cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            Func<float, float, float> R(Func<float, float, float> f) => (x, y) =>
                f(o + (x - o) * cos - (y - o) * sin, o + (x - o) * sin + (y - o) * cos);
            (float x, float y) At(float lx, float ly) => (o + (lx - o) * cos + (ly - o) * sin, o - (lx - o) * sin + (ly - o) * cos);

            // A bolted skid with four anchor feet (the rig is stationary), then the body with a team cab and a hopper of ore.
            c.Shape(R((x, y) => Box(x, y, o - 8, o, 20, 15, 2)), Dark, 5, 1.2f, 1.5f);
            foreach (var (fx, fy) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
            {
                c.Shape(R((x, y) => Box(x, y, o - 8 + fx * 20, o + fy * 15, 4, 4, 1)), Plate, 6, 1, 1.5f);
                var (rx, ry) = At(o - 8 + fx * 20, o + fy * 15);
                c.Rivet(rx, ry, 1.4f, 1f, 0.4f);
            }
            c.Shape(R((x, y) => Box(x, y, o - 14, o, 10, 9, 2.5f)), Hull, 12, 2, 2.5f);
            c.Shape(R((x, y) => Box(x, y, o - 18, o + 4, 5, 5, 1.5f)), Team, 16, 1.5f, 2);
            c.Shape(R((x, y) => Box(x, y, o - 11, o - 4, 5, 4, 1)), Dark, 9, 1, 1);
            c.Shape(R((x, y) => Polygon(x, y, o - 12, o - 4, 2.4f, 5, 0.2f)), Ore, 12, 1.2f, 1.5f);
            c.Shape(R((x, y) => Polygon(x, y, o - 9, o - 3, 2f, 6, 0.9f)), Ore, 11.5f, 1.2f, 1.5f);
            // Motor housing, then a long fluted bit lying out into the rock (its tip at the tile's edge);
            // the flutes travel along it with the frame.
            c.Shape(R((x, y) => Box(x, y, o - 1, o, 6, 7, 2)), Hull, 15, 2, 2);
            c.Shape(R((x, y) => Box(x, y, o + 6, o, 3, 5, 1)), Steel, 14, 1, 1.5f);
            float x0 = o + 8, x1 = o + 32;
            Func<float, float, float> bit = (x, y) =>
            {
                float t = Mathf.Clamp01((x - x0) / (x1 - x0));
                return Mathf.Max(Mathf.Abs(y - o) - Mathf.Lerp(6f, 0.8f, t), Mathf.Max(x0 - x, x - x1));
            };
            c.Shape(R(bit), Steel, 14, 4, 7);
            c.Groove(R((x, y) => bit(x, y) > 0 ? 9 : Mathf.Abs(Mod(x - x0 + (y - o) * 0.9f - frame * 2.5f, 5) - 2.5f) - 0.9f), 1.6f, 1f);
            // Ore spilling beside the auger.
            foreach (var (gx, gy, r) in new[] { (o + 22f, o - 10f, 2.4f), (o + 27f, o - 13f, 1.8f), (o + 24f, o + 11f, 2.1f) })
                c.Shape(R((x, y) => Polygon(x, y, gx, gy, r, 6, gx)), Ore, 5, 1.2f, 1.5f);
            c.Shape(R((x, y) => Circle(x, y, o - 22, o - 7, 1.4f)), Lamp, 13, 0.5f, 0.3f);
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
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
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
        /// The Builder: a small tracked bulldozer (owner, 2026-10-17, to match its icon) with a team cab, an
        /// engine deck and exhaust at the back, and a wide blade with a striped edge on push arms in front.
        /// Frames 0–1 move, 2–3 work the blade (raised and pushed out, then back down).
        /// </summary>
        public static LitCanvas Builder(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle), o = TileSize / 2f;
            Func<float, float, float> L(Func<float, float, float> local) =>
                (x, y) => local((x - o) * cos + (y - o) * sin, -(x - o) * sin + (y - o) * cos);
            float tread = frame == 1 ? 2f : 0f, reach = frame == 2 ? 2.5f : frame == 3 ? 1f : 0f, lift = frame == 2 ? 3f : 0f;
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape(L((x, y) => Box(x, y, -3, side * 12, 17, 5, 2.5f)), Rubber, 5, 2, 3);
                c.Groove(L((x, y) => Box(x, y, -3, side * 12, 16, 4) > 0 ? 9 : Mathf.Abs(Mod(x + tread, 4) - 2) - 1.1f), 1.1f, 0.7f);
            }
            // Hull, then the engine deck with its grille and exhaust, then the cab.
            c.Shape(L((x, y) => Box(x, y, -4, 0, 15, 9, 3.5f)), Hull, 9, 3, 3);
            c.Groove(L((x, y) => Box(x, y, -13, 0, 4, 6) > 0 ? 9 : Mathf.Abs(Mod(y, 2) - 1) - 0.45f), 1, 0.45f);
            c.Shape(L((x, y) => Circle(x, y, -9, -6, 1.8f)), Steel, 15, 0.8f, 1);
            c.Shape(L((x, y) => Circle(x, y, -9, -6, 0.9f)), Dark, 14, 0.4f, 0.4f);
            c.Shape(L((x, y) => Box(x, y, 1, 0, 6, 7, 2.5f)), Team, 15, 2.5f, 3);
            c.Shape(L((x, y) => Box(x, y, 6, 0, 1.2f, 5.5f, 0.6f)), Glass, 0.6f, 0.5f, 0.5f, stack: true);
            // Push arms and the blade, wider than the tracks, with hazard stripes along its top edge.
            foreach (float side in new[] { -1f, 1f })
                c.Shape(L((x, y) => Capsule(x, y, 6, side * 12, 16 + reach, side * 12, 1.8f)), Steel, 8 + lift, 1, 1.2f);
            Func<float, float, float> blade = L((x, y) => Box(x, y, 20 + reach, 0, 4.5f, 18, 1.5f));
            c.Shape(blade, Plate, 11 + lift, 1.5f, 2);
            // Stripes in the dozer's own frame (not the canvas's, as Stripes draws), so they cross the blade in every facing.
            Func<float, float, float> edge = (x, y) => Box(x, y, 22 + reach, 0, 2.2f, 17, 0.6f);
            c.Shape(L(edge), Hazard, 11.5f + lift, 0.8f, 0.8f);
            c.Shape(L((x, y) => Mathf.Max(edge(x, y), Mathf.Abs(Mod(y + x, 8) - 4) - 2)), Dark, 11.5f + lift, 0.4f, 0.3f);
            c.Rivet(o + (-4f) * cos, o + (-4f) * sin, 1.2f, 0.8f);
            c.Grime(11, 0.3f, 0.12f, 6);
            return c;
        }

        /// <summary>The Digger: a squat drill carrier. Frames 0–1 move, 2–3 turn the drill's spiral.</summary>
        public static LitCanvas Digger(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle), o = TileSize / 2f;
            Func<float, float, float> L(Func<float, float, float> local) =>
                (x, y) => local((x - o) * cos + (y - o) * sin, -(x - o) * sin + (y - o) * cos);
            float tread = frame == 1 ? 2f : 0f, turn = frame >= 2 ? (frame - 2) * 2.2f : 0f;
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape(L((x, y) => Box(x, y, -2, side * 16, 21, 6.5f, 3)), Rubber, 6, 2, 3);
                c.Groove(L((x, y) => Box(x, y, -2, side * 16, 20, 5.5f) > 0 ? 9 : Mathf.Abs(Mod(x + tread, 4) - 2) - 1.2f), 1.2f, 0.7f);
            }
            c.Shape(L((x, y) => Box(x, y, -3, 0, 18, 12, 4)), Hull, 11, 3, 3.5f);
            c.Shape(L((x, y) => Box(x, y, -9, 0, 7, 9, 3)), Team, 15, 2, 3);
            c.Groove(L((x, y) => Box(x, y, 3, 0, 5, 8) > 0 ? 9 : Mathf.Abs(Mod(y, 3) - 1.5f) - 0.5f), 0.8f, 0.5f);
            // The drill nose, with a spiral groove that turns.
            c.Shape(L((x, y) => Capsule(x, y, 14, 0, 27, 0, 6)), Steel, 14, 3, 4);
            c.Groove(L((x, y) => Capsule(x, y, 14, 0, 27, 0, 6) > 0 ? 9 : Mathf.Abs(Mod(x + y * 0.7f + turn, 5) - 2.5f) - 0.8f), 1f, 0.6f);
            c.Grime(17, 0.35f, 0.14f, 6);
            return c;
        }

        /// <summary>The Bomb (1×1): a low disc half sunk in the floor with a team ring; frame 1 blinks the lamp.</summary>
        public static LitCanvas Bomb(int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            c.Shape((x, y) => Circle(x, y, o, o, 20), Dark, 6, 2, 3);
            c.Shape((x, y) => Mathf.Abs(Circle(x, y, o, o, 14)) - 2.4f, Team, 8, 1, 1.2f, stack: true);
            c.Shape((x, y) => Circle(x, y, o, o, 7), Plate, 9, 2, 2);
            c.Shape((x, y) => Circle(x, y, o, o, 3.2f), frame == 0 ? Lamp : Dark, 10, 0.8f, 0.6f);
            for (int k = 0; k < 4; k++)
            {
                float a = k * Mathf.PI / 2f + Mathf.PI / 4f;
                c.Rivet(o + Mathf.Cos(a) * 17, o + Mathf.Sin(a) * 17, 1f, 0.8f);
            }
            c.Grime(5, 0.25f, 0.1f, 6);
            return c;
        }

        /// <summary>The Radar (2×2): a concrete pad and equipment block under a tall mast with a dish; frames turn the dish.</summary>
        public static LitCanvas Radar(int frame)
        {
            int s = 2 * TileSize;
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
            float o = s / 2f;
            c.Shape((x, y) => Box(x, y, o, o - 8, 52, 40, 3), Concrete, 3, 1, 1.5f);
            c.Shape((x, y) => Box(x, y, o - 20, o - 20, 22, 16, 2.5f), Hull, 16, 2, 3);
            c.Shape((x, y) => Box(x, y, o - 20, o - 8, 14, 4, 1), Team, 17, 1, 1.2f, stack: true);
            c.Groove((x, y) => Box(x, y, o + 22, o - 24, 18, 12) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.6f, 0.8f, 0.6f);
            c.Shape((x, y) => Box(x, y, o + 22, o - 24, 18, 12, 2), Plate, 14, 1.5f, 2);
            // Mast and dish.
            float shift = frame == 0 ? -5f : 5f;
            c.Shape((x, y) => Box(x, y, o, o + 6, 4, 26, 1.5f), Steel, 30, 1.5f, 2);
            c.Shape((x, y) => Circle(x, y, o + shift, o + 30, 24), Steel, 44, 3, 5);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + shift, o + 30, 16)) - 0.35f, 1f, 0.7f);
            c.Shape((x, y) => Circle(x, y, o + shift, o + 30, 6), Team, 46, 1.5f, 2.5f);
            c.Shape((x, y) => Circle(x, y, o + shift, o + 30, 2.2f), Lamp, 48, 0.8f, 0.5f);
            c.Grime(29, 0.2f, 0.08f, 8);
            return c;
        }

        // ---------- Player walls ----------

        /// <summary>
        /// A player wall for a 4-neighbour mask (bit set = that neighbour is wall or rock): a team-banded block that
        /// joins its neighbours. <paramref name="damage"/> 0 is whole, 1 cracked, 2 crumbling.
        /// </summary>
        public static LitCanvas PlayerWall(int seed, int mask, int damage)
        {
            LitCanvas c = Floor(seed, mask & 3);
            Func<float, float, float> block = (x, y) => EdgeDistance(x, y, mask) - 5;
            c.Shape((x, y) => -block(x, y), Hull, 20, 4, 6);
            c.Shape((x, y) => -(EdgeDistance(x, y, mask) - 14), Team, 22.5f, 1.5f, 1.5f, stack: true);
            c.Groove((x, y) => block(x, y) <= 0 ? 9 : Mathf.Abs(Mod(x + y, 8) - 4) - 0.35f, 0.7f, 0.5f);
            if (damage > 0)
            {
                float width = 0.006f * (1 + damage);
                c.Groove((x, y) => block(x, y) <= 0 ? 9 : (Mathf.Abs(Noise.Fbm(x, y, TileSize, 3, 3, seed + 5 + damage * 13) - 0.5f) - width) * 60, 1.4f, 0.8f);
            }
            if (damage > 1) c.Grime(seed + 3, 0.5f, 0.22f, 5);
            return c;
        }

        /// <summary>A planned wall segment: the floor with a dashed team outline where the wall will stand.</summary>
        public static LitCanvas WallBlueprint(int mask)
        {
            LitCanvas c = Floor(7, mask & 3);
            c.Shape((x, y) => Mod(x + y, 10) < 5 ? Mathf.Abs(EdgeDistance(x, y, mask) - 8) - 1.4f : 9f, Team, 3, 0.8f, 0.8f);
            return c;
        }

        /// <summary>The HQ's footprint in tiles (GameConfig.xml's Base Width and Height).</summary>
        public const int HqTiles = 5;

        /// <summary>Headroom (output px at 64 px per tile) above a building's footprint for the 3/4 view.</summary>
        public const int Headroom = 72;
        /// <summary>How far up the screen a unit of height lifts a building in the 3/4 view.</summary>
        public const float Lift = 0.5f;

        /// <summary>
        /// The HQ (5×5; owner, 2026-10-11): a command complex. A podium with two low wings and a big main block, topped by a tall
        /// command tower with banded windows, a dish and an antenna mast with a beacon; the team marking on the
        /// main roof and stripes on the wings; a helipad on the podium and garage doors in the wings' fronts.
        /// Drawn for the 3/4 view (<see cref="Lift"/>), so its canvas has headroom above the footprint. The
        /// design is laid out on a 192-unit square and drawn at the 5×5 footprint (<see cref="LitCanvas.Scale"/>).
        /// </summary>
        public static LitCanvas Base()
        {
            const float design = 192f;
            int s = HqTiles * TileSize;
            float scale = s / design;
            var c = new LitCanvas(s, s + Mathf.CeilToInt(Headroom * scale), s / 2f) { Scale = scale };
            float o = design / 2f;
            // Podium: a chamfered platform over most of the footprint, with a striped front edge.
            c.Shape((x, y) => Mathf.Max(Box(x, y, o, o, 90, 84, 6), Polygon(x, y, o, o, 104, 8, Mathf.PI / 8)), Concrete, 5, 2, 2);
            c.Groove((x, y) => Mathf.Max(Box(x, y, o, o, 84, 78, 4), Polygon(x, y, o, o, 97, 8, Mathf.PI / 8)), 1, 0.9f);
            Stripes(c, (x, y) => Box(x, y, o, o - 80, 30, 3.5f), 5.5f);
            // Helipad (south-west on the podium).
            c.Shape((x, y) => Circle(x, y, o - 54, o - 50, 20), Plate, 6.5f, 1, 1);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o - 54, o - 50, 16)) - 0.3f, 0.8f, 0.9f);
            c.Shape((x, y) => Mathf.Min(Mathf.Min(Box(x, y, o - 60, o - 50, 2.2f, 9), Box(x, y, o - 48, o - 50, 2.2f, 9)), Box(x, y, o - 54, o - 50, 6, 2)), Hazard, 7, 0.4f, 0.4f);
            // Wings (east and west): low blocks with team stripes and garage doors in their fronts.
            foreach (float side in new[] { -1f, 1f })
            {
                float wx = o + side * 60;
                c.Shape((x, y) => Box(x, y, wx, o + 10, 20, 34, 3), Hull, 24, 2, 2.5f);
                c.Shape((x, y) => Box(x, y, wx, o + 40, 18, 2.5f, 1), Team, 25, 1, 1);
                c.Groove((x, y) => Mathf.Abs(Mod(y - o, 12) - 6) - 0.5f > 0 || Box(x, y, wx, o + 10, 17, 31) > 0 ? 9 : 0, 0.8f, 0.6f);
                c.Shape((x, y) => Box(x, y, wx, o - 22, 12, 2.5f, 0.6f), Glass, 24.5f, 0.6f, 0.6f); // the facade row: windows
            }
            // Main block, with the team marking on its roof and AC units.
            c.Shape((x, y) => Box(x, y, o, o + 6, 40, 44, 5), Hull, 40, 3, 3);
            c.Groove((x, y) => Box(x, y, o, o + 6, 36, 40, 3), 1.4f, 1.1f);
            c.Shape((x, y) => Box(x, y, o, o - 37, 34, 2.5f, 0.8f), Glass, 40.5f, 0.6f, 0.6f);
            c.Shape((x, y) => Box(x, y, o - 18, o - 18, 14, 14, 2), Team, 41.5f, 1.5f, 1.5f);
            c.Groove((x, y) => Box(x, y, o - 18, o - 18, 11, 11) > 0 ? 9 : Mathf.Abs(Mathf.Abs(x - (o - 18)) * 0.9f + (y - o + 18) - 2) - 1.5f, 1.6f, 1.4f);
            foreach (var (ax, ay) in new[] { (o + 20f, o - 22f), (o + 20f, o - 6f) })
            {
                c.Shape((x, y) => Box(x, y, ax, ay, 6, 5, 1.2f), Plate, 44, 1.2f, 2);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, ax, ay, 3)) - 0.3f, 0.9f, 0.7f);
            }
            // Command tower: tall, windowed on its front, with a dish and an antenna mast.
            c.Shape((x, y) => Box(x, y, o - 4, o + 26, 22, 18, 4), Plate, 74, 2.5f, 3);
            c.Shape((x, y) => Box(x, y, o - 4, o + 9, 20, 2, 0.6f), Glass, 74.5f, 0.6f, 0.6f);
            c.Groove((x, y) => Box(x, y, o - 4, o + 26, 18, 14, 3), 1.2f, 1);
            c.Shape((x, y) => Circle(x, y, o + 8, o + 30, 8), Steel, 84, 7, 5);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 8, o + 30, 4.5f)) - 0.3f, 0.8f, 0.6f);
            c.Shape((x, y) => Circle(x, y, o - 16, o + 32, 2.6f), Steel, 110, 1, 1);
            c.Shape((x, y) => Circle(x, y, o - 16, o + 32, 1.6f), Lamp, 112, 0.6f, 0.4f);
            c.Grime(31, 0.2f, 0.08f, 10);
            return c;
        }

        /// <summary>
        /// A stand-in for art that doesn't exist yet (the v0.7 kit): a team-coloured block with a hazard band,
        /// <paramref name="tiles"/> tiles square, so the atlases always hold every manifest id.
        /// </summary>
        public static LitCanvas Placeholder(int tiles = 1)
        {
            int s = tiles * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f, r = s * 0.34f;
            c.Shape((x, y) => Box(x, y, o, o, r, r, 3), Hull, 6, 1.5f, 2);
            c.Shape((x, y) => Box(x, y, o, o, r * 0.62f, r * 0.62f, 2), Team, 9, 1.2f, 1.5f);
            Stripes(c, (x, y) => Box(x, y, o, o - r * 0.8f, r * 0.9f, 2.5f), 7);
            return c;
        }

        // ---------- Terrain ----------

        /// <summary>Stone floor: mottled, pebbled and cracked; tiles seamlessly.</summary>
        public static LitCanvas Floor(int seed, int variant)
        {
            var c = new LitCanvas(TileSize, TileSize);
            // 0.95: dark enough that every high-contrast player colour stands out 4.5:1 (PalettesTests).
            c.Ground(new Material(C(StylePalette.Floor1) * 0.95f, 0.06f, 8));
            int s = seed + variant * 977;
            c.Grime(s, 0.9f, 0.16f, 4, 5);
            // Fine cracks along a noise contour, broken up by a second noise so they don't form loops.
            c.Groove((x, y) => Noise.Fbm(x, y, TileSize, 2, 2, s + 9) < 0.55f ? 9 : (Mathf.Abs(Noise.Fbm(x, y, TileSize, 5, 3, s + 5) - 0.5f) - 0.006f) * 60, 0.9f, 0.45f);
            var rng = new ArtRandom(seed, "pebbles" + variant);
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
            var rng = new ArtRandom(seed, "crystals" + variant);
            int count = 3 + rng.Range(3), placed = 0;
            void Crystal(float cx, float cy, float r, float a, float t)
            {
                Color tint = Color.Lerp(C(StylePalette.Gem1), C(StylePalette.Gem2), t);
                c.Shape((x, y) => Polygon(x, y, cx, cy, r, 6, a), new Material(tint, 0.9f, 80, glow: 0.35f), 34 + r, r, r * 1.6f);
                placed++;
            }
            for (int k = 0; k < count; k++)
            {
                float cx = 14 + rng.Range(TileSize - 28), cy = 14 + rng.Range(TileSize - 28);
                float r = 4 + rng.Range(4), a = rng.Range(60) * Mathf.Deg2Rad, t = rng.Range(100) / 100f;
                // Only on the rock's top: the cliff and its bevel reach about 23 px in from an open side.
                if (EdgeDistance(cx, cy, mask) < 23 + r) continue;
                Crystal(cx, cy, r, a, t);
            }
            if (placed == 0)
            {
                // Narrow rock (most sides open): one crystal at the deepest point, so every gem tile shows gems.
                float bx = TileSize / 2f, by = TileSize / 2f, best = -1;
                for (int y = 8; y <= TileSize - 8; y += 4)
                for (int x = 8; x <= TileSize - 8; x += 4)
                {
                    float d = EdgeDistance(x, y, mask);
                    if (d > best) { best = d; bx = x; by = y; }
                }
                Crystal(bx, by, Mathf.Clamp(best - 23, 3, 6), rng.Range(60) * Mathf.Deg2Rad, 0.5f);
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
