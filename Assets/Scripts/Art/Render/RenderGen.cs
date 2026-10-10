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
        /// The Miner (1×1): a drilling rig. A truss derrick stands over the drill at the gem face (east); a
        /// conveyor carries ore chunks back to a hopper; an engine with an exhaust drives it. Frames turn the
        /// drill and advance the belt.
        /// </summary>
        public static LitCanvas Miner(int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            // Skid frame the rig sits on.
            c.Shape((x, y) => Box(x, y, o, o, 29, 22, 3), Concrete, 4, 1.5f, 1.5f);
            // Engine block (west) with a cooling grille and an exhaust.
            c.Shape((x, y) => Box(x, y, o - 19, o - 8, 9, 9, 2), Hull, 14, 2, 2);
            c.Groove((x, y) => Box(x, y, o - 19, o - 8, 7, 7) > 0 ? 9 : Mathf.Abs(Mod(y, 4) - 2) - 0.8f, 1.2f, 0.9f);
            c.Shape((x, y) => Circle(x, y, o - 24, o + 4, 3), Steel, 18, 1.2f, 1.5f);
            c.Shape((x, y) => Circle(x, y, o - 24, o + 4, 1.6f), Dark, 16, 0.5f, 0.5f);
            // Hopper (north-west) holding mined gems.
            c.Shape((x, y) => Box(x, y, o - 17, o + 13, 10, 8, 2), Plate, 12, 2, 2);
            c.Shape((x, y) => Box(x, y, o - 17, o + 13, 7, 5, 1), Dark, 7, 1, 1);
            foreach (var (gx, gy) in new[] { (-20f, 12f), (-15f, 14f), (-17f, 10f), (-13f, 11f) })
                c.Shape((x, y) => Polygon(x, y, o + gx, o + gy, 2.2f, 6, 0.3f), Ore, 9, 1.5f, 1.5f);
            // Conveyor from the drill to the hopper; belt cleats and ore chunks move with the frame.
            c.Shape((x, y) => Box(x, y, o + 2, o + 13, 15, 3.5f, 1), Rubber, 9, 1, 1);
            c.Groove((x, y) => Box(x, y, o + 2, o + 13, 15, 3) > 0 ? 9 : Mathf.Abs(Mod(x + frame * 2, 4) - 2) - 0.4f, 0.7f, 0.4f);
            for (int k = 0; k < 3; k++)
            {
                float gx = o + 12 - Mod(k * 9 + frame * 2, 27);
                c.Shape((x, y) => Polygon(x, y, gx, o + 13, 1.8f, 5, k), Ore, 11, 1, 1);
            }
            // Derrick over the drill at the gem face: a truss square with cross braces, the drill inside.
            float dx = o + 17, dy = o - 4;
            c.Shape((x, y) => Mathf.Max(Box(x, y, dx, dy, 12, 12, 1), -Box(x, y, dx, dy, 9f, 9f)), Steel, 22, 1, 2);
            foreach (float s in new[] { -1f, 1f })
                c.Shape((x, y) => Capsule(x, y, dx - 10, dy - 10 * s, dx + 10, dy + 10 * s, 1.6f), Plate, 21, 1f, 1.2f);
            c.Shape((x, y) => Circle(x, y, dx, dy, 6), Steel, 16, 4, 5);
            float phase = frame * Mathf.PI / 2;
            c.Groove((x, y) =>
            {
                float px = x - dx, py = y - dy, r = Mathf.Sqrt(px * px + py * py);
                return r > 5.5f ? 9 : Mathf.Abs(Mathf.Sin(Mathf.Atan2(py, px) * 3 + r * 0.9f + phase)) - 0.35f;
            }, 1.2f, 0.4f);
            // Team panel on the engine, and a work lamp.
            c.Shape((x, y) => Box(x, y, o - 19, o - 20, 9, 2.5f, 1), Team, 10, 1, 1);
            c.Shape((x, y) => Circle(x, y, dx + 10, dy + 10, 1.6f), Lamp, 23, 0.6f, 0.4f);
            c.Grime(11, 0.25f, 0.12f, 6);
            return c;
        }

        private static Material Ore => new Material(C(StylePalette.Gem1), 0.8f, 70, glow: 0.3f);
        private static Material Hazard => new Material(C(StylePalette.Warn) * 0.95f, 0.15f, 16);

        /// <summary>Hazard stripes (warning yellow and dark) inside <paramref name="sdf"/>.</summary>
        private static void Stripes(LitCanvas c, Func<float, float, float> sdf, float top)
        {
            c.Shape(sdf, Hazard, top, 0.8f, 0.8f);
            c.Shape((x, y) => Mathf.Max(sdf(x, y), Mathf.Abs(Mod(x + y, 12) - 6) - 3), Dark, top, 0.4f, 0.3f);
        }

        /// <summary>A half-built tank (no turret) for the factory bay.</summary>
        private static void Chassis(LitCanvas c, float cx, float cy, float top)
        {
            foreach (float s in new[] { -1f, 1f })
            {
                c.Shape((x, y) => Box(x, y, cx + s * 9, cy, 3.5f, 13, 1.5f), Rubber, top + 3, 1, 1.5f);
                c.Groove((x, y) => Box(x, y, cx + s * 9, cy, 3, 12) > 0 ? 9 : Mathf.Abs(Mod(y, 3) - 1.5f) - 0.6f, 0.8f, 0.5f);
            }
            c.Shape((x, y) => Box(x, y, cx, cy, 6.5f, 11, 2), Hull, top + 5, 1.5f, 2);
            c.Shape((x, y) => Circle(x, y, cx, cy + 1, 3.5f), Dark, top + 4, 1, 1); // the empty turret ring
        }

        /// <summary>
        /// The Small Unit Spawner (2×2): a vehicle factory. Its roof is open over the assembly bay, where a
        /// tank is being built on a turntable under a gantry crane carrying its turret; vehicles leave through
        /// the striped roller door on the south side. Fuel tanks and a pipe feed it; a stack vents it. Frame 1
        /// moves the crane and dims the door lamp.
        /// </summary>
        public static LitCanvas Spawner(int frame)
        {
            int s = 2 * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f;
            // Concrete apron, tyre tracks out of the door, hazard stripes along the exit.
            c.Shape((x, y) => Box(x, y, o, o, 62, 62, 4), Concrete, 3, 1.5f, 1.5f);
            foreach (float tx in new[] { o - 14f, o - 2f }) c.Groove((x, y) => y > 22 ? 9 : Mathf.Abs(x - tx) - 1.5f, 0.6f, 1.2f);
            Stripes(c, (x, y) => Box(x, y, o - 8, 7, 32, 5), 3.5f);
            // Main hall (west and middle): corrugated roof around an open bay.
            Func<float, float, float> hall = (x, y) => Box(x, y, o - 12, o + 12, 46, 42, 3);
            Func<float, float, float> bay = (x, y) => Box(x, y, o - 12, o + 18, 26, 26, 2);
            c.Shape((x, y) => Mathf.Max(hall(x, y), -bay(x, y)), Plate, 26, 2, 3);
            c.Groove((x, y) => hall(x, y) > -3 || bay(x, y) < 3 ? 9 : Mathf.Abs(Mod(x, 10) - 5) - 0.9f, 1.4f, 1.2f);
            // The bay floor, the turntable and the tank being built.
            c.Shape(bay, Concrete, 6, 1, 1);
            c.Shape((x, y) => Circle(x, y, o - 12, o + 16, 19), Steel, 8, 1, 1);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o - 12, o + 16, 16)) - 0.1f, 0.6f, 0.5f);
            Chassis(c, o - 12, o + 16, 8);
            // Gantry crane: two rails across the bay, a trolley carrying the turret.
            foreach (float ry in new[] { o + 38f, o - 2f })
                c.Shape((x, y) => Box(x, y, o - 12, ry, 28, 3f, 1f), Steel, 30, 1.2f, 1.5f);
            float trolley = o - 22 + frame * 8;
            c.Shape((x, y) => Box(x, y, trolley, o + 18, 5, 23, 1.5f), Plate, 32, 1.5f, 2f);
            c.Shape((x, y) => Box(x, y, trolley, o + 24, 8, 7, 2.5f), Team, 34, 2f, 2.5f);
            c.Shape((x, y) => Box(x, y, trolley + 10, o + 24, 6, 2f, 1f), Steel, 34, 1f, 1f);
            // Roller door (south) with its lamp, in the hall's south wall.
            c.Shape((x, y) => Box(x, y, o - 8, o - 33, 16, 2.5f, 0.5f), Dark, 24, 0.6f, 0.6f);
            c.Shape((x, y) => Box(x, y, o - 8, 18, 14, 5, 1), Plate, 8, 1, 1);
            c.Groove((x, y) => Box(x, y, o - 8, 18, 13, 4) > 0 ? 9 : Mathf.Abs(Mod(y, 4) - 2) - 0.7f, 1f, 0.9f);
            c.Shape((x, y) => Circle(x, y, o + 10, o - 33, 1.8f), frame == 0 ? Lamp : Plate, 27, 0.6f, 0.4f);
            // Team stripe along the hall's roof edge.
            c.Shape((x, y) => Box(x, y, o - 12, o + 50, 44, 4, 1f), Team, 27, 1f, 1f);
            // Fuel tanks (east), a pipe into the hall, and the stack.
            foreach (float ty in new[] { o + 30f, o + 6f })
            {
                c.Shape((x, y) => Circle(x, y, o + 48, ty, 10), Plate, 20, 7, 9);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 48, ty, 6)) - 0.1f, 0.6f, 0.5f);
                c.Rivet(o + 48, ty, 1.5f, 1.2f);
            }
            c.Shape((x, y) => Mathf.Min(Capsule(x, y, o + 48, o + 18, o + 36, o + 18, 2), Capsule(x, y, o + 48, o + 30, o + 48, o + 6, 1.6f)), Steel, 14, 1.2f, 1.5f);
            c.Shape((x, y) => Circle(x, y, o + 46, o - 20, 7), Steel, 36, 2, 3);
            c.Shape((x, y) => Circle(x, y, o + 46, o - 20, 4), Dark, 30, 1, 1);
            c.Grime(23, 0.3f, 0.13f, 8);
            return c;
        }

        /// <summary>
        /// The HQ (3×3): a command post. A bunker with roof AC units and the team's marking, a radar dish and
        /// an antenna mast, a vehicle garage to the south, a landing pad to the west, fuel drums and supply
        /// crates, all inside a sandbag wall with gaps for the roads.
        /// </summary>
        public static LitCanvas Base()
        {
            int s = 3 * TileSize;
            var c = new LitCanvas(s, s);
            float o = s / 2f;
            c.Shape((x, y) => Box(x, y, o, o, 94, 94, 6), Concrete, 3, 1.5f, 1.5f);
            // Sandbag wall: a chain of rounded bags around the edge, with gaps north, south, east and west.
            for (int i = 0; i < 28; i++)
            {
                float t = i / 28f * 4, side = Mathf.Floor(t), u = (t - side) * 172 - 86;
                if (Mathf.Abs(u) < 16) continue;
                float bx = side == 0 ? o + u : side == 1 ? o + 86 : side == 2 ? o - u : o - 86;
                float by = side == 0 ? o - 86 : side == 1 ? o + u : side == 2 ? o + 86 : o - u;
                bool horizontal = side == 0 || side == 2;
                c.Shape((x, y) => Box(x, y, bx, by, horizontal ? 5.5f : 4.5f, horizontal ? 4.5f : 5.5f, 2.5f), new Material(new Color(0.42f, 0.39f, 0.32f), 0.05f, 6), 7, 1.5f, 3);
            }
            // Landing pad (west): a ring and an H.
            c.Shape((x, y) => Circle(x, y, o - 52, o + 30, 22), Plate, 5, 1, 1);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o - 52, o + 30, 18)) - 0.2f, 0.7f, 0.8f);
            c.Shape((x, y) => Mathf.Min(Mathf.Min(Box(x, y, o - 58, o + 30, 1.8f, 9), Box(x, y, o - 46, o + 30, 1.8f, 9)), Box(x, y, o - 52, o + 30, 6, 1.6f)), Hazard, 5.6f, 0.4f, 0.4f);
            // Bunker: chamfered block with a team roof marking, AC units and a hatch.
            Func<float, float, float> bunker = (x, y) => Mathf.Max(Box(x, y, o + 6, o + 4, 40, 34, 4), Polygon(x, y, o + 6, o + 4, 48, 8, Mathf.PI / 8));
            c.Shape(bunker, Hull, 24, 4, 5);
            c.Groove((x, y) => Box(x, y, o + 6, o + 4, 34, 28, 3), 1.4f, 1.1f);
            c.Shape((x, y) => Box(x, y, o - 4, o + 4, 20, 20, 3), Team, 26f, 2f, 2f);
            c.Groove((x, y) => Box(x, y, o - 4, o + 4, 16, 16) > 0 ? 9 : Mathf.Abs(Mathf.Abs(x - (o - 4)) * 0.8f + (y - o - 4) * 0.9f - 2) - 1.4f, 1.5f, 1.4f); // chevron
            foreach (float ax in new[] { o + 22f, o + 34f })
            {
                c.Shape((x, y) => Box(x, y, ax, o + 22, 6, 6, 1.5f), Plate, 28, 1.5f, 2f);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, ax, o + 22, 3)) - 0.1f, 0.8f, 0.5f);
                c.Groove((x, y) => Circle(x, y, ax, o + 22, 3) > 0 ? 9 : Mathf.Abs(Mathf.Sin(Mathf.Atan2(y - o - 22, x - ax) * 3)) - 0.4f, 0.6f, 0.3f);
            }
            c.Shape((x, y) => Circle(x, y, o + 28, o - 12, 5), Plate, 26, 1.5f, 1.5f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 28, o - 12, 3.2f)) - 0.1f, 0.6f, 0.4f);
            // Radar dish on a pylon (north-east) and an antenna mast with a beacon.
            c.Shape((x, y) => Box(x, y, o + 56, o + 56, 5, 5, 1), Plate, 18, 1, 1);
            c.Shape((x, y) => Circle(x, y, o + 56, o + 56, 18), Steel, 32, 16, 11);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 56, o + 56, 9)) - 0.1f, 0.6f, 0.5f);
            c.Shape((x, y) => Capsule(x, y, o + 56, o + 56, o + 66, o + 64, 1), Steel, 34, 0.5f, 0.5f);
            c.Shape((x, y) => Circle(x, y, o - 26, o + 52, 3), Steel, 30, 1, 1);
            c.Shape((x, y) => Capsule(x, y, o - 26, o + 52, o - 26, o + 72, 0.9f), Steel, 31, 0.5f, 0.5f);
            c.Shape((x, y) => Circle(x, y, o - 26, o + 73, 2), Lamp, 32, 0.6f, 0.4f);
            // Garage (south) with a striped apron.
            c.Shape((x, y) => Box(x, y, o + 6, o - 42, 22, 10, 2), Hull, 18, 2, 2);
            c.Shape((x, y) => Box(x, y, o + 6, o - 52, 18, 2, 0.5f), Plate, 8, 0.6f, 0.6f);
            c.Groove((x, y) => Box(x, y, o + 6, o - 52, 17, 1.5f) > 0 ? 9 : Mathf.Abs(Mod(x, 6) - 3) - 0.8f, 1f, 0.8f);
            Stripes(c, (x, y) => Box(x, y, o + 6, o - 62, 20, 3), 3.5f);
            // Fuel drums and supply crates (south-east).
            foreach (var (fx, fy) in new[] { (48f, -46f), (58f, -46f), (53f, -55f), (63f, -55f) })
            {
                c.Shape((x, y) => Circle(x, y, o + fx, o + fy, 4.5f), new Material(new Color(0.55f, 0.18f, 0.14f), 0.4f, 30), 9, 2, 2.5f);
                c.Rivet(o + fx, o + fy, 0.9f, 0.6f);
            }
            foreach (var (bx, by) in new[] { (-50f, -52f), (-38f, -52f), (-44f, -40f) })
            {
                c.Shape((x, y) => Box(x, y, o + bx, o + by, 5.5f, 5.5f, 1f), new Material(new Color(0.42f, 0.33f, 0.22f), 0.08f, 8), 10, 1, 1.5f);
                c.Groove((x, y) => Box(x, y, o + bx, o + by, 3.5f, 3.5f) > 0 ? 9 : Mathf.Min(Mathf.Abs(x - o - bx - (y - o - by)), Mathf.Abs(x - o - bx + (y - o - by))) - 0.4f, 0.6f, 0.4f);
            }
            c.Grime(31, 0.3f, 0.13f, 10);
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
