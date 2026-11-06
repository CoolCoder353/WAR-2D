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

        /// <summary>
        /// Inside-corner bits for rock: set when both sides of that corner are rock but the diagonal tile between them is
        /// open, so the corner is cut back to meet the two neighbours' cliffs instead of sticking out (playtest 2026-10-31).
        /// </summary>
        public const int NorthEast = 1, SouthEast = 2, SouthWest = 4, NorthWest = 8;

        // Before RockShapes: static fields initialise in order, and MakeRockShapes fills this.
        private static readonly int[] RockShapeIndex = new int[256];

        /// <summary>
        /// Every rock shape: a 4-neighbour mask with the inside corners it can have (a corner bit only where both of its
        /// sides are rock): 47 in all. A rock tile's frame is its shape's index (<see cref="RockShape"/>).
        /// </summary>
        public static readonly (int mask, int corners)[] RockShapes = MakeRockShapes();

        /// <summary>The inside corners a 4-neighbour mask allows (both sides of the corner rock).</summary>
        public static int AllowedCorners(int mask)
        {
            int c = 0;
            if ((mask & North) != 0 && (mask & East) != 0) c |= NorthEast;
            if ((mask & South) != 0 && (mask & East) != 0) c |= SouthEast;
            if ((mask & South) != 0 && (mask & West) != 0) c |= SouthWest;
            if ((mask & North) != 0 && (mask & West) != 0) c |= NorthWest;
            return c;
        }

        /// <summary>The <see cref="RockShapes"/> index of a mask and its inside corners (corners it can't have are ignored).</summary>
        public static int RockShape(int mask, int corners) => RockShapeIndex[(mask & 15) | ((corners & AllowedCorners(mask)) << 4)];

        private static (int, int)[] MakeRockShapes()
        {
            var shapes = new System.Collections.Generic.List<(int, int)>();
            for (int mask = 0; mask < 16; mask++)
            for (int corners = 0; corners < 16; corners++)
            {
                if ((corners & ~AllowedCorners(mask)) != 0) continue;
                RockShapeIndex[mask | (corners << 4)] = shapes.Count;
                shapes.Add((mask, corners));
            }
            return shapes.ToArray();
        }

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
        public static LitCanvas Tank(float angle, int frame) => Tank(angle, frame, angle);

        /// <summary>The Tank with its turret turned to <paramref name="turret"/> (radians, 0 = east).</summary>
        public static LitCanvas Tank(float angle, int frame, float turret, UnitLayer layer = UnitLayer.Whole)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle), o = TileSize / 2f;
            float tCos = Mathf.Cos(turret), tSin = Mathf.Sin(turret);
            Func<float, float, float> T(Func<float, float, float> local) =>
                (x, y) => local((x - o) * tCos + (y - o) * tSin, -(x - o) * tSin + (y - o) * tCos);
            // Local (tank) coordinates of an output point: x forward, y left.
            Func<float, float, float> L(Func<float, float, float> local) =>
                (x, y) => local((x - o) * cos + (y - o) * sin, -(x - o) * sin + (y - o) * cos);
            float tread = frame == 1 ? 2f : 0f, recoil = frame == 2 ? 3f : 0f;

            if (layer == UnitLayer.Turret) c.Floor(10);
            else
            {
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
            }
            if (layer != UnitLayer.Hull)
            {
                // Turret (team colour) with a hatch, and the barrel.
                c.Shape(T((x, y) => Box(x, y, -3, 0, 11, 10, 5)), Team, 17, 3, 4);
                c.Shape(T((x, y) => Circle(x, y, -6, 4, 3.4f)), Plate, 18, 1, 1);
                c.Groove(T((x, y) => Circle(x, y, -6, 4, 2.2f)), 0.6f, 0.4f);
                c.Shape(T((x, y) => Box(x, y, 18 - recoil, 0, 12, 2.1f, 1.8f)), Steel, 16, 1.8f, 2.2f);
                c.Shape(T((x, y) => Box(x, y, 29 - recoil, 0, 2, 3, 1)), Steel, 16, 1, 1.5f);
            }
            c.Grime(7, 0.3f, 0.12f, 6);
            if (frame >= 2 && layer == UnitLayer.Whole)
            {
                float size = frame == 2 ? 5f : 3f;
                c.Shape(T((x, y) => Circle(x, y, 33 - recoil + size * 0.5f, 0, size)), new Material(C(StylePalette.Warn), 0, 1, glow: 2.2f), 18, 2, 0);
                c.Shape(T((x, y) => Circle(x, y, 33 - recoil + size * 0.5f, 0, size * 0.45f)), new Material(Color.white, 0, 1, glow: 2.5f), 18.5f, 1, 0);
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
        //
        // The tiles themselves are plain (owner, 2026-10-31): seamless stone floor and a calm rock top, so a field of them
        // doesn't read as a grid. The features (cracks, stains, craters, boulders; crevices, crags, veins on rock) are
        // separate stamps (FloorFeature, RockFeature) that the Terrain shader scatters over open floor and deep rock at
        // any position and scale. A stamp is drawn in FeatureFrames rotations with the light kept fixed, so a turned
        // crater is still lit from the top left.

        private static Material FloorMaterial => new Material(C(StylePalette.Floor1) * 0.95f, 0.06f, 8);
        private static Material RockMaterial => new Material(C(StylePalette.Rock2) * 1.35f, 0.08f, 10);

        /// <summary>
        /// Stone floor: mottled and lightly pebbled; tiles seamlessly. The variants differ only away from the edges
        /// (their mottling blends to variant 0's within 16 px of each side), so any two meet without a seam.
        /// </summary>
        public static LitCanvas Floor(int seed, int variant)
        {
            var c = new LitCanvas(TileSize, TileSize);
            // 0.95: dark enough that every high-contrast player colour stands out 4.5:1 (PalettesTests).
            c.Ground(FloorMaterial);
            int vs = seed + variant * 977;
            float Mix(float x, float y, int cells, int octaves, int salt) => variant == 0
                ? Noise.Fbm(x, y, TileSize, cells, octaves, seed + salt)
                : Mathf.Lerp(Noise.Fbm(x, y, TileSize, cells, octaves, seed + salt), Noise.Fbm(x, y, TileSize, cells, octaves, vs + salt), Interior(x, y));
            c.Displace((x, y) => (Mix(x, y, 4, 5, 0) - 0.5f) * 1.8f);
            c.Tint((x, y, col) => col * (1 + (Mix(x, y, 8, 3, 7) - 0.5f) * 0.32f));
            var rng = new ArtRandom(seed, "pebbles" + variant);
            for (int k = 0; k < 4; k++) c.Rivet(10 + rng.Range(TileSize - 20), 10 + rng.Range(TileSize - 20), 0.7f + rng.Range(2) * 0.4f, 0.7f, 0.04f);
            return c;
        }

        /// <summary>Floor variants (<see cref="Floor"/>): plain, picked at random per tile.</summary>
        public const int FloorVariants = 4;

        /// <summary>
        /// 0 below <paramref name="e0"/>, 1 above <paramref name="e1"/>, smooth between (a shader's smoothstep; Unity's
        /// <c>Mathf.SmoothStep</c> interpolates between its first two arguments instead).
        /// </summary>
        private static float Step(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        /// <summary>0 at a tile's edges rising to 1 from 16 px in: where a variant may differ without making a seam.</summary>
        private static float Interior(float x, float y) =>
            Mathf.SmoothStep(0f, 1f, Mathf.Min(Mathf.Min(x, TileSize - x), Mathf.Min(y, TileSize - y)) / 16f);

        /// <summary>
        /// Rock for a 4-neighbour mask (bit set = that neighbour is rock) and its inside <paramref name="corners"/>: a raised
        /// mass whose cliffs face the open sides, with rounded outside corners, cut-back inside corners and a noisy (but
        /// tile-matching) edge. The top is craggy near a cliff and calms to a
        /// fine, even grain from 44 px in, so rock enclosed on all four sides is plain and joins its neighbours.
        /// </summary>
        public static LitCanvas Rock(int seed, int mask, int corners = 0)
        {
            LitCanvas c = Floor(seed, mask & 3);
            float Inside(float x, float y) => EdgeDistance(x, y, mask, corners) - 6 - 5 * Noise.Fbm(x, y, TileSize, 4, 3, seed + 41);
            c.Shape((x, y) => -Inside(x, y), RockMaterial, 22, 12, 20);
            float Crag(float x, float y) => 1 - Step(20, 44, EdgeDistance(x, y, mask, corners));
            RockTop(c, seed, TileSize, (x, y) => Inside(x, y) <= 0 ? 0 : Mathf.Min(1, Inside(x, y) / 10), Crag);
            return c;
        }

        /// <summary>
        /// The rock top's relief and shading over a canvas <paramref name="size"/> px square (tiling at that size):
        /// <paramref name="on"/> is how much of it a point gets (0 off the rock), <paramref name="crag"/> how craggy it is
        /// there (ridged and darker in the cracks) rather than calm.
        /// </summary>
        private static void RockTop(LitCanvas c, int seed, int size, Func<float, float, float> on, Func<float, float, float> crag)
        {
            int k = size / TileSize;
            float Ridge(float x, float y) => 1 - Mathf.Abs(Noise.Fbm(x, y, size, 4 * k, 4, seed + 77) * 2 - 1);
            float Calm(float x, float y) => Noise.Fbm(x, y, size, 8 * k, 3, seed + 83);
            c.Displace((x, y) => on(x, y) * (2.5f * Calm(x, y) + 10 * crag(x, y) * Ridge(x, y)));
            Color crevice = C(StylePalette.Rock0) * 1.4f;
            c.Tint((x, y, col) =>
            {
                if (on(x, y) <= 0) return col;
                Color calm = col * (0.8f + 0.16f * Calm(x, y));
                Color craggy = Color.Lerp(crevice, col * (0.9f + 0.25f * Noise.Fbm(x, y, size, 6 * k, 3, seed + 99)), Mathf.SmoothStep(0.15f, 0.75f, Ridge(x, y)));
                return Color.Lerp(calm, craggy, crag(x, y));
            });
        }

        // ---------- Terrain features (stamps) ----------

        /// <summary>A feature stamp's canvas in tiles across (at scale 1; the shader draws it from about 0.35 to 2 tiles).</summary>
        public const int FeatureTiles = 2;

        /// <summary>Frames per feature kind: rotations about 30° apart, each with its own random size and layout.</summary>
        public const int FeatureFrames = 12;

        /// <summary>Floor feature kinds (<see cref="FloorFeature"/>).</summary>
        public const int FloorFeatureKinds = 6;

        /// <summary>Rock feature kinds (<see cref="RockFeature"/>).</summary>
        public const int RockFeatureKinds = 5;

        private const int FeatureSize = FeatureTiles * TileSize;

        /// <summary>The plain floor a floor stamp is drawn on; the stamp keeps only the pixels its feature changed.</summary>
        public static LitCanvas FloorFeatureBase(int seed)
        {
            var c = new LitCanvas(FeatureSize, FeatureSize);
            c.Ground(FloorMaterial);
            c.Displace((x, y) => (Noise.Fbm(x, y, FeatureSize, 4 * FeatureTiles, 5, seed) - 0.5f) * 1.8f);
            c.Tint((x, y, col) => col * (1 + (Noise.Fbm(x, y, FeatureSize, 8 * FeatureTiles, 3, seed + 7) - 0.5f) * 0.32f));
            return c;
        }

        /// <summary>The plain rock top a rock stamp is drawn on.</summary>
        public static LitCanvas RockFeatureBase(int seed)
        {
            LitCanvas c = FloorFeatureBase(seed);
            c.Shape((x, y) => -1000f, RockMaterial, 22, 0, 0);
            RockTop(c, seed, FeatureSize, (x, y) => 1, (x, y) => 0);
            return c;
        }

        /// <summary>
        /// Floor feature <paramref name="kind"/> (0 crack, 1 damp stain, 2 sandy patch, 3 gravel, 4 crater, 5 boulders),
        /// frame <paramref name="frame"/> (turned frame × 30°, give or take 15°), on <see cref="FloorFeatureBase"/>. Kept within 56 px of the
        /// centre and faded out towards it, so a stamp has no square edge.
        /// </summary>
        public static LitCanvas FloorFeature(int seed, int kind, int frame)
        {
            LitCanvas c = FloorFeatureBase(seed);
            var rng = new ArtRandom(seed, $"floor feature {kind} {frame}");
            float a = (frame * 30 + rng.Range(30) - 15) * Mathf.Deg2Rad;
            float o = FeatureSize / 2f, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            float U(float x, float y) => (x - o) * ca + (y - o) * sa;
            float V(float x, float y) => -(x - o) * sa + (y - o) * ca;
            float Blob(float x, float y, float r, int salt) =>
                Step(0.15f, 0.55f, 1 - new Vector2(U(x, y) / 1.25f, V(x, y)).magnitude / r + 0.45f * (Noise.Fbm(x, y, FeatureSize, 6, 3, seed + salt) - 0.5f));
            int ns = seed + kind * 31 + frame * 7;
            switch (kind)
            {
                case 0:
                {
                    // A crack: a long tapering wandering line with a short branch off one side.
                    float len = 22 + rng.Range(34), bu = (rng.Range(60) - 30) / 100f * len, ba = (30 + rng.Range(40)) * Mathf.Deg2Rad;
                    float wander = 4 + rng.Range(7), branch = rng.Chance(0.6f) ? 10 + rng.Range(14) : 0;
                    float Wander(float u) => wander * (Mathf.PerlinNoise(u * 0.045f + ns * 0.13f, 0.5f) - 0.5f);
                    float Crack(float x, float y)
                    {
                        float u = U(x, y), v = V(x, y) - Wander(u);
                        float t = Mathf.Abs(u) / len;
                        return t >= 1 ? 9 : Mathf.Abs(v) / (1 - t * t) - 0.35f;
                    }
                    float Branch(float x, float y)
                    {
                        float u = U(x, y) - bu, v = V(x, y) - Wander(bu);
                        float bu2 = u * Mathf.Cos(ba) + v * Mathf.Sin(ba), bv = -u * Mathf.Sin(ba) + v * Mathf.Cos(ba);
                        float t = bu2 / Mathf.Max(branch, 1);
                        return branch <= 0 || t <= 0 || t >= 1 ? 9 : Mathf.Abs(bv) / (1 - t) - 0.3f;
                    }
                    c.Groove((x, y) => Mathf.Min(Crack(x, y), Branch(x, y)), 2.6f, 1.5f);
                    break;
                }
                case 1:
                    // A damp stain: a dark soft-edged blob.
                    c.Tint((x, y, col) => col * (1f - 0.38f * Blob(x, y, 14 + rng.Range(26), 31)));
                    break;
                case 2:
                {
                    // A sandy patch: lighter grit drifted into a blob.
                    float r = 14 + rng.Range(26);
                    c.Tint((x, y, col) => Color.Lerp(col, C(StylePalette.Floor2) * 1.05f, 0.65f * Blob(x, y, r, 37)));
                    c.Displace((x, y) => -0.6f * Blob(x, y, r, 37));
                    break;
                }
                case 3:
                {
                    // Gravel: a lighter patch thick with small stones.
                    float r = 16 + rng.Range(24);
                    c.Tint((x, y, col) => Color.Lerp(col, C(StylePalette.Floor2), 0.35f * Blob(x, y, r, 43)));
                    for (int k = 0; k < (int)(r * 2); k++)
                    {
                        float gx = 12 + rng.Range(FeatureSize - 24), gy = 12 + rng.Range(FeatureSize - 24);
                        if (Blob(gx, gy, r, 43) > 0.3f) c.Rivet(gx, gy, 0.8f + rng.Range(3) * 0.45f, 1f, 0.1f);
                    }
                    break;
                }
                case 4:
                {
                    // A crater: a dished, darker floor inside a raised rim, with thrown-out stones.
                    float r = 7 + rng.Range(18);
                    float D(float x, float y) => new Vector2(x - o, y - o).magnitude / r;
                    c.Displace((x, y) => D(x, y) < 1 ? -3f * (1 - D(x, y) * D(x, y)) : 1.8f * Mathf.Max(0, 1 - (D(x, y) - 1) * 2.2f));
                    c.Tint((x, y, col) => col * (1f - 0.35f * Mathf.Clamp01(1.1f - D(x, y))));
                    for (int k = 0; k < 3 + (int)(r / 3); k++)
                    {
                        float t = rng.Range(360) * Mathf.Deg2Rad, d = r * (1.3f + rng.Range(60) / 100f);
                        c.Rivet(o + Mathf.Cos(t) * d, o + Mathf.Sin(t) * d, 0.9f + rng.Range(3) * 0.4f, 1.1f, 0.1f);
                    }
                    break;
                }
                default:
                {
                    // Boulders: one large fallen rock and two or three smaller ones beside it.
                    var rock = new Material(C(StylePalette.Rock1) * 1.1f, 0.08f, 10);
                    int n = 1 + rng.Range(4);
                    float big = 4 + rng.Range(9);
                    for (int k = 0; k < n; k++)
                    {
                        float d = k == 0 ? 0 : big + 3 + rng.Range(12), t = a + k * 2.1f;
                        float bx = o + Mathf.Cos(t) * d, by = o + Mathf.Sin(t) * d, br = k == 0 ? big : 2 + rng.Range(4);
                        int sides = 5 + rng.Range(3);
                        float turn = rng.Range(70) * Mathf.Deg2Rad;
                        c.Shape((x, y) => Polygon(x, y, bx, by, br, sides, turn) - 0.8f * Noise.Fbm(x, y, FeatureSize, 16, 2, ns + k), rock, 3 + br * 0.9f, br * 0.7f, br);
                    }
                    break;
                }
            }
            return c;
        }

        /// <summary>
        /// Rock feature <paramref name="kind"/> (0 crevice, 1 crag, 2 mineral veins, 3 outcrop, 4 pit), frame
        /// <paramref name="frame"/> (turned frame × 30°, give or take 15°), on <see cref="RockFeatureBase"/>.
        /// </summary>
        public static LitCanvas RockFeature(int seed, int kind, int frame)
        {
            LitCanvas c = RockFeatureBase(seed);
            var rng = new ArtRandom(seed, $"rock feature {kind} {frame}");
            float a = (frame * 30 + rng.Range(30) - 15) * Mathf.Deg2Rad;
            float o = FeatureSize / 2f, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            float U(float x, float y) => (x - o) * ca + (y - o) * sa;
            float V(float x, float y) => -(x - o) * sa + (y - o) * ca;
            int ns = seed + 500 + kind * 31 + frame * 7;
            float Fade(float x, float y, float r) => Mathf.Clamp01((r - new Vector2(x - o, y - o).magnitude) / 12f);
            Color crevice = C(StylePalette.Rock0) * 1.2f;
            switch (kind)
            {
                case 0:
                {
                    // A crevice: a deep dark split with jagged walls.
                    float len = 20 + rng.Range(34), wide = 1.6f + rng.Range(15) / 10f;
                    float Split(float x, float y)
                    {
                        float u = U(x, y), t = Mathf.Abs(u) / len;
                        if (t >= 1) return 9;
                        float v = V(x, y) - 9 * (Mathf.PerlinNoise(u * 0.05f + ns * 0.11f, 0.3f) - 0.5f);
                        return Mathf.Abs(v) - wide * (1 - t * t) * (0.7f + 0.6f * Mathf.PerlinNoise(u * 0.3f, ns * 0.07f));
                    }
                    c.Displace((x, y) => Split(x, y) < 0 ? -7 : -3 * Mathf.Clamp01(1 - Split(x, y) / 3));
                    c.Tint((x, y, col) => Split(x, y) < 0 ? crevice : Split(x, y) < 2 ? col * 0.8f : col);
                    break;
                }
                case 1:
                {
                    // A crag: a raised knot of ridged stone, darker in its cracks.
                    float r = 18 + rng.Range(28);
                    float W(float x, float y) => Mathf.SmoothStep(0, 1, Fade(x, y, r) * (0.8f + 0.4f * Noise.Fbm(x, y, FeatureSize, 4, 2, ns)));
                    float Ridge(float x, float y) => 1 - Mathf.Abs(Noise.Fbm(x, y, FeatureSize, 6, 4, ns + 3) * 2 - 1);
                    c.Displace((x, y) => W(x, y) * 11 * Ridge(x, y));
                    c.Tint((x, y, col) => Color.Lerp(col, Color.Lerp(crevice, col * 1.1f, Mathf.SmoothStep(0.15f, 0.75f, Ridge(x, y))), W(x, y)));
                    break;
                }
                case 2:
                {
                    // Mineral veins: a few thin lighter bands with a dark seam, wandering together.
                    float r = 26 + rng.Range(28);
                    int bands = 1 + rng.Range(4);
                    float gap = 6 + rng.Range(5);
                    float Band(float x, float y)
                    {
                        float v = V(x, y) - 8 * (Mathf.PerlinNoise(U(x, y) * 0.04f + ns * 0.17f, 0.7f) - 0.5f) + gap * (bands - 1) / 2f;
                        float best = 9;
                        for (int b = 0; b < bands; b++) best = Mathf.Min(best, Mathf.Abs(v - b * gap));
                        return best;
                    }
                    Color vein = C(StylePalette.Rock3) * 1.5f;
                    c.Tint((x, y, col) =>
                    {
                        float f = Fade(x, y, r), d = Band(x, y);
                        if (f <= 0 || d > 1.6f) return col;
                        return Color.Lerp(col, d < 0.5f ? crevice : vein, f * (d < 0.5f ? 0.8f : 0.7f));
                    });
                    c.Displace((x, y) => Fade(x, y, r) * (Band(x, y) < 1.6f ? 0.8f : 0));
                    break;
                }
                case 3:
                {
                    // An outcrop: a big blocky slab standing proud of the rock, with a smaller one beside it.
                    var stone = new Material(C(StylePalette.Rock3) * 1.25f, 0.1f, 12);
                    float br = 7 + rng.Range(12), sx = o + Mathf.Cos(a) * (br + 4), sy = o + Mathf.Sin(a) * (br + 4);
                    int sides = 5 + rng.Range(2);
                    c.Shape((x, y) => Polygon(x, y, o, o, br, sides, a) - 1.2f * Noise.Fbm(x, y, FeatureSize, 12, 2, ns), stone, 34, 3, 9);
                    c.Shape((x, y) => Polygon(x, y, sx, sy, br * 0.45f, 5, a + 1) - 0.8f * Noise.Fbm(x, y, FeatureSize, 12, 2, ns + 1), stone, 30, 2, 6);
                    c.Groove((x, y) => Polygon(x, y, o, o, br, sides, a) > -3 ? 9 : Mathf.Abs(U(x, y) * 0.3f + V(x, y)) - 0.4f, 1.2f, 0.8f);
                    break;
                }
                default:
                {
                    // A pit: a rough hollow with loose stones in it.
                    float r = 7 + rng.Range(14);
                    float D(float x, float y) => new Vector2(U(x, y) / 1.2f, V(x, y)).magnitude / r + 0.25f * (Noise.Fbm(x, y, FeatureSize, 8, 2, ns) - 0.5f);
                    c.Displace((x, y) => D(x, y) < 1 ? -6 * (1 - D(x, y) * D(x, y)) : 0);
                    c.Tint((x, y, col) => D(x, y) < 1 ? Color.Lerp(col, crevice, 0.5f * (1 - D(x, y))) : col);
                    for (int k = 0; k < 6; k++)
                    {
                        float t = rng.Range(360) * Mathf.Deg2Rad, d = r * rng.Range(80) / 100f;
                        c.Rivet(o + Mathf.Cos(t) * d, o + Mathf.Sin(t) * d, 1.2f + rng.Range(3) * 0.5f, 1.6f, 0.08f);
                    }
                    break;
                }
            }
            return c;
        }

        /// <summary>Rock with glowing faceted gem crystals set into it.</summary>
        public static LitCanvas Gem(int seed, int variant, int mask = North | East | South | West, int corners = 0) =>
            Crystals(seed, "crystals" + variant, 3, 3, 4, 4, C(StylePalette.Gem1), C(StylePalette.Gem2), 0.35f, mask, corners);

        /// <summary>
        /// A rich gem (v0.17): rock with fewer, larger crystals in a brighter, warmer colour than ordinary gems (the palette's
        /// <c>Gem3</c> halfway to <c>Warn</c>, Figma H1) and a stronger glow.
        /// </summary>
        public static LitCanvas RichGem(int seed, int variant, int mask = North | East | South | West, int corners = 0) =>
            Crystals(seed, "rich" + variant, 2, 2, 6, 5, RichGemColour, C(StylePalette.Warn), 0.55f, mask, corners);

        /// <summary>The rich gem's colour: <c>Gem3</c> halfway to <c>Warn</c> (Figma H1).</summary>
        public static Color RichGemColour => Color.Lerp(C(StylePalette.Gem3), C(StylePalette.Warn), 0.5f);

        /// <summary>
        /// Rock with <paramref name="countBase"/> plus up to <paramref name="countRange"/> − 1 faceted crystals of radius
        /// <paramref name="radiusBase"/> plus up to <paramref name="radiusRange"/> − 1, tinted between <paramref name="from"/> and
        /// <paramref name="to"/>, only on the rock's top; a narrow rock gets one crystal at its deepest point.
        /// </summary>
        private static LitCanvas Crystals(int seed, string stream, int countBase, int countRange, int radiusBase, int radiusRange,
            Color from, Color to, float glow, int mask, int corners)
        {
            LitCanvas c = Rock(seed, mask, corners);
            var rng = new ArtRandom(seed, stream);
            int count = countBase + rng.Range(countRange), placed = 0;
            void Crystal(float cx, float cy, float r, float a, float t)
            {
                Color tint = Color.Lerp(from, to, t);
                c.Shape((x, y) => Polygon(x, y, cx, cy, r, 6, a), new Material(tint, 0.9f, 80, glow: glow), 34 + r, r, r * 1.6f);
                placed++;
            }
            for (int k = 0; k < count; k++)
            {
                float cx = 14 + rng.Range(TileSize - 28), cy = 14 + rng.Range(TileSize - 28);
                float r = radiusBase + rng.Range(radiusRange), a = rng.Range(60) * Mathf.Deg2Rad, t = rng.Range(100) / 100f;
                // Only on the rock's top: the cliff and its bevel reach about 23 px in from an open side.
                if (EdgeDistance(cx, cy, mask, corners) < 23 + r) continue;
                Crystal(cx, cy, r, a, t);
            }
            if (placed == 0)
            {
                // Narrow rock (most sides open): one crystal at the deepest point, so every gem tile shows gems.
                float bx = TileSize / 2f, by = TileSize / 2f, best = -1;
                for (int y = 8; y <= TileSize - 8; y += 4)
                for (int x = 8; x <= TileSize - 8; x += 4)
                {
                    float d = EdgeDistance(x, y, mask, corners);
                    if (d > best) { best = d; bx = x; by = y; }
                }
                Crystal(bx, by, Mathf.Clamp(best - 23, 3, radiusBase + 2), rng.Range(60) * Mathf.Deg2Rad, 0.5f);
            }
            return c;
        }

        /// <summary>
        /// Distance (px) to the nearest side open to floor, with rounded outside corners; large when enclosed. An inside
        /// corner (<paramref name="corners"/>) is open floor at that corner point, where the two neighbours' cliffs meet.
        /// </summary>
        private static float EdgeDistance(float x, float y, int mask, int corners = 0)
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
            float Point(float px, float py) => Mathf.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if ((corners & NorthEast) != 0) d = Mathf.Min(d, Point(s, s));
            if ((corners & SouthEast) != 0) d = Mathf.Min(d, Point(s, 0));
            if ((corners & SouthWest) != 0) d = Mathf.Min(d, Point(0, 0));
            if ((corners & NorthWest) != 0) d = Mathf.Min(d, Point(0, s));
            return d;
        }

        private static float Mod(float a, float m) => a - m * Mathf.Floor(a / m);
    }
}
