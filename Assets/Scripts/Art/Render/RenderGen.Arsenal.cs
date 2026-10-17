using System;
using UnityEngine;
using WAR2D.Art.Style;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// The v0.12 Arsenal sprites (spec <c>2026-10-16-v0.12-arsenal-design.md</c> §5): the Scout, Flamethrower,
    /// Sniper and Suicide bomber (1×1 like the Tank), the Heavy and Artillery (2×2 canvases for the large size
    /// class), the Heavy Factory (3×3) and the Siege Workshop (2×2). Same look and light as the rest of
    /// <see cref="RenderGen"/>. Unit frames are move (0–1) then the unit's action (2–3).
    /// </summary>
    public static partial class RenderGen
    {
        /// <summary>Canvas size of a large-class unit (Heavy, Artillery): 2 tiles, so a 0.7-tile radius fits.</summary>
        public const int LargeUnitSize = 2 * TileSize;

        private static Material Flame => new Material(C(StylePalette.Warn), 0, 1, glow: 2.2f);
        private static Material WhiteHot => new Material(Color.white, 0, 1, glow: 2.5f);
        /// <summary>A flame jet: orange, glowing less than a muzzle flash so it keeps its colour.</summary>
        private static Material Fire => new Material(new Color(1f, 0.45f, 0.12f), 0, 1, glow: 0.9f);
        private static Material FireCore => new Material(C(StylePalette.Warn), 0, 1, glow: 1.3f);

        /// <summary>Maps a shape in unit-local coordinates (x forward, y left) onto a canvas centred on <paramref name="o"/>.</summary>
        private static Func<Func<float, float, float>, Func<float, float, float>> Local(float angle, float o)
        {
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            return local => (x, y) => local((x - o) * cos + (y - o) * sin, -(x - o) * sin + (y - o) * cos);
        }

        /// <summary>The canvas point of unit-local point (<paramref name="lx"/>, <paramref name="ly"/>).</summary>
        private static (float x, float y) LocalPoint(float angle, float o, float lx, float ly)
        {
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            return (o + lx * cos - ly * sin, o + lx * sin + ly * cos);
        }

        /// <summary>A pair of tracks, with link grooves (period <paramref name="period"/>) that advance with <paramref name="tread"/>.</summary>
        private static void Tracks(LitCanvas c, Func<Func<float, float, float>, Func<float, float, float>> L,
            float cx, float halfLength, float offset, float halfWidth, float tread, float period = 4)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape(L((x, y) => Box(x, y, cx, side * offset, halfLength, halfWidth, 3)), Rubber, 6, 2, 3);
                c.Groove(L((x, y) => Box(x, y, cx, side * offset, halfLength - 1, halfWidth - 1) > 0 ? 9
                    : Mathf.Abs(Mod(x + tread, period) - period / 2) - period * 0.3f), 1.2f, 0.7f);
            }
        }

        /// <summary>Tyres at each local x in <paramref name="xs"/>, both sides, with tread lines that turn with <paramref name="spin"/>.</summary>
        private static void Wheels(LitCanvas c, Func<Func<float, float, float>, Func<float, float, float>> L,
            float[] xs, float offset, float halfLength, float halfWidth, float spin)
        {
            foreach (float wx in xs)
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape(L((x, y) => Box(x, y, wx, side * offset, halfLength, halfWidth, 2)), Rubber, 6, 1.5f, 2.5f);
                c.Groove(L((x, y) => Box(x, y, wx, side * offset, halfLength - 0.8f, halfWidth - 0.6f) > 0 ? 9
                    : Mathf.Abs(Mod(x + spin, 3) - 1.5f) - 0.5f), 0.8f, 0.5f);
            }
        }

        /// <summary>A muzzle flash centred ahead of local (<paramref name="x"/>, <paramref name="y"/>).</summary>
        private static void MuzzleFlash(LitCanvas c, Func<Func<float, float, float>, Func<float, float, float>> L, float x, float y, float size, float top)
        {
            c.Shape(L((px, py) => Circle(px, py, x + size * 0.5f, y, size)), Flame, top, 2, 0);
            c.Shape(L((px, py) => Circle(px, py, x + size * 0.5f, y, size * 0.45f)), WhiteHot, top + 0.5f, 1, 0);
        }

        /// <summary>
        /// The Scout: a light four-wheeled buggy with a team-coloured cab, a roll bar, a sensor mast with a lamp
        /// (it has the widest sight) and a light gun. Frames 0–1 move, 2–3 fire.
        /// </summary>
        public static LitCanvas Scout(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            var L = Local(angle, o);
            float spin = frame == 1 ? 1.5f : 0f, recoil = frame == 2 ? 1.5f : 0f;
            Wheels(c, L, new[] { -10f, 10f }, 10.5f, 5, 3, spin);
            c.Shape(L((x, y) => Box(x, y, 0, 0, 15, 8, 4)), Hull, 9, 3, 3);
            c.Shape(L((x, y) => Box(x, y, 10, 0, 5, 7, 2)), Plate, 1, 1, 1, stack: true);
            c.Shape(L((x, y) => Box(x, y, -2, 0, 6, 6.5f, 2.5f)), Team, 13, 2, 2.5f);
            c.Shape(L((x, y) => Box(x, y, 3.5f, 0, 1.2f, 5, 0.6f)), Glass, 0.6f, 0.5f, 0.5f, stack: true);
            c.Shape(L((x, y) => Capsule(x, y, -7, -6, -7, 6, 1.2f)), Steel, 15, 1, 1.2f);
            // The gun on the cab, then the sensor mast at the back.
            c.Shape(L((x, y) => Box(x, y, 8 - recoil, -3, 6, 1.1f, 1)), Steel, 14.5f, 1, 1.2f);
            c.Shape(L((x, y) => Circle(x, y, -11, 4, 2.6f)), Steel, 17, 1, 1.5f);
            c.Shape(L((x, y) => Circle(x, y, -11, 4, 1.3f)), Lamp, 18, 0.5f, 0.3f);
            c.Grime(41, 0.3f, 0.12f, 6);
            if (frame >= 2) MuzzleFlash(c, L, 14 - recoil, -3, frame == 2 ? 3.5f : 2.2f, 17);
            return c;
        }

        /// <summary>
        /// The Flamethrower: a tracked carrier with two striped fuel tanks on its back, a team cab and a nozzle
        /// with a pilot light. Frames 0–1 move, 2–3 spray flame (frame 2 the longer jet).
        /// </summary>
        public static LitCanvas Flamethrower(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            var L = Local(angle, o);
            float tread = frame == 1 ? 2f : 0f;
            Tracks(c, L, -1, 21, 15.5f, 6, tread);
            c.Shape(L((x, y) => Box(x, y, -1, 0, 19, 11, 4)), Hull, 10, 3, 3.5f);
            foreach (float side in new[] { -1f, 1f })
            {
                Func<float, float, float> tank = L((x, y) => Capsule(x, y, -16, side * 5, -4, side * 5, 4.4f));
                Stripes(c, tank, 15);
                c.Groove(L((x, y) => Mathf.Abs(x + 10) - 0.4f > 0 || Mathf.Abs(y - side * 5) > 4 ? 9 : 0), 0.8f, 0.5f);
            }
            c.Shape(L((x, y) => Box(x, y, 8, 0, 6, 7, 2.5f)), Team, 15, 2, 3);
            c.Shape(L((x, y) => Box(x, y, 12, 0, 1.4f, 5, 0.6f)), Glass, 0.6f, 0.5f, 0.5f, stack: true);
            c.Shape(L((x, y) => Capsule(x, y, -4, 3, 6, 1, 0.9f)), Dark, 15.5f, 0.6f, 0.6f);
            c.Shape(L((x, y) => Box(x, y, 17, 0, 6, 1.8f, 1.5f)), Steel, 14, 1.2f, 1.5f);
            c.Shape(L((x, y) => Box(x, y, 23, 0, 1.5f, 2.6f, 1)), Dark, 14, 1, 1);
            c.Shape(L((x, y) => Circle(x, y, 24.5f, 1.8f, 0.9f)), Lamp, 15, 0.4f, 0.3f);
            c.Grime(43, 0.35f, 0.14f, 6);
            if (frame >= 2)
            {
                float length = frame == 2 ? 7f : 5f, wobble = frame == 2 ? 0.8f : -0.8f;
                c.Shape(L((x, y) => Capsule(x, y, 25, 0, 25 + length, wobble, 2.6f + (x - 25) * 0.25f)), Fire, 18, 2, 0);
                c.Shape(L((x, y) => Capsule(x, y, 25, 0, 25 + length * 0.6f, wobble * 0.5f, 1.3f)), FireCore, 18.5f, 1, 0);
            }
            return c;
        }

        /// <summary>
        /// The Sniper: a slim six-wheeled armoured car with a small team turret, a scope and a very long, thin
        /// barrel. Frames 0–1 move, 2–3 fire (a sharp, small flash).
        /// </summary>
        public static LitCanvas Sniper(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            var L = Local(angle, o);
            float spin = frame == 1 ? 1.5f : 0f, recoil = frame == 2 ? 2f : 0f;
            Wheels(c, L, new[] { -12f, 0f, 12f }, 11, 4.2f, 3, spin);
            c.Shape(L((x, y) => Mathf.Max(Box(x, y, 0, 0, 18, 8.5f, 4), Polygon(x, y, -2, 0, 21, 6, 0))), Hull, 10, 3, 3);
            c.Groove(L((x, y) => Box(x, y, -12, 0, 4, 6) > 0 ? 9 : Mathf.Abs(Mod(x, 2) - 1) - 0.45f), 1, 0.45f);
            c.Shape(L((x, y) => Box(x, y, -4, 0, 7, 6, 3)), Team, 15, 2, 2.5f);
            c.Shape(L((x, y) => Box(x, y, 12 - recoil, 0, 17, 1.2f, 1)), Steel, 14, 1, 1.2f);
            c.Shape(L((x, y) => Box(x, y, 29 - recoil, 0, 1.6f, 2, 0.8f)), Steel, 14, 0.8f, 1);
            c.Shape(L((x, y) => Box(x, y, -3, 4.8f, 5, 1.5f, 1)), Dark, 17, 0.8f, 1);
            c.Shape(L((x, y) => Circle(x, y, 2, 4.8f, 1.3f)), Glass, 17.5f, 0.5f, 0.5f);
            var (rx, ry) = LocalPoint(angle, o, -7, -3);
            c.Rivet(rx, ry, 1.2f, 0.9f);
            c.Grime(47, 0.3f, 0.12f, 6);
            if (frame >= 2) MuzzleFlash(c, L, 30.5f - recoil, 0, frame == 2 ? 2.4f : 1.4f, 16);
            return c;
        }

        /// <summary>
        /// The Suicide bomber: a fast little four-wheeled cart carrying a big striped explosive drum with a
        /// detonator lamp. Frames 0–1 move (the lamp blinks slowly); 2–3 are armed (a fast, hot blink).
        /// </summary>
        public static LitCanvas SuicideBomber(float angle, int frame)
        {
            var c = new LitCanvas(TileSize, TileSize);
            float o = TileSize / 2f;
            var L = Local(angle, o);
            float spin = frame == 1 ? 1.5f : 0f;
            Wheels(c, L, new[] { -10f, 10f }, 10f, 4.5f, 2.8f, spin);
            c.Shape(L((x, y) => Box(x, y, 0, 0, 15, 8, 3)), Hull, 8, 2.5f, 3);
            c.Shape(L((x, y) => Box(x, y, 10, 0, 4.5f, 6, 2)), Team, 12, 1.5f, 2);
            c.Shape(L((x, y) => Box(x, y, 13.5f, 0, 0.9f, 4.5f, 0.5f)), Glass, 0.5f, 0.4f, 0.4f, stack: true);
            Func<float, float, float> drum = L((x, y) => Circle(x, y, -4, 0, 9.5f));
            Stripes(c, drum, 15);
            c.Groove(L((x, y) => Mathf.Abs(Circle(x, y, -4, 0, 7f)) - 0.3f), 0.8f, 0.6f);
            c.Shape(L((x, y) => Circle(x, y, -4, 0, 2.8f)), Dark, 16, 0.8f, 1);
            c.Shape(L((x, y) => Capsule(x, y, -1, 0, 7, 1.5f, 0.6f)), Dark, 13, 0.4f, 0.4f);
            bool lit = frame == 0 || frame == 2;
            Material lamp = frame >= 2 ? new Material(C(StylePalette.Warn), 0.1f, 20, glow: 1.4f) : Lamp;
            c.Shape(L((x, y) => Circle(x, y, -4, 0, 1.6f)), lit ? lamp : Plate, 17, 0.5f, 0.3f);
            c.Grime(53, 0.3f, 0.12f, 6);
            return c;
        }

        /// <summary>
        /// The Heavy (large size class, 2×2 canvas): a wide assault tank with side skirts, a sloped front glacis,
        /// a big team turret and twin barrels with muzzle brakes. Frames 0–1 move, 2–3 fire both barrels.
        /// </summary>
        public static LitCanvas Heavy(float angle, int frame)
        {
            var c = new LitCanvas(LargeUnitSize, LargeUnitSize);
            float o = LargeUnitSize / 2f;
            var L = Local(angle, o);
            float tread = frame == 1 ? 2.5f : 0f, recoil = frame == 2 ? 4f : 0f;
            Tracks(c, L, 0, 46, 33, 11, tread, 5);
            foreach (float side in new[] { -1f, 1f })
                c.Shape(L((x, y) => Box(x, y, -2, side * 38, 40, 4, 1.5f)), Plate, 10, 1.2f, 1.5f);
            c.Shape(L((x, y) => Box(x, y, 0, 0, 44, 27, 7)), Hull, 16, 4, 4);
            c.Shape(L((x, y) => Box(x, y, 30, 0, 12, 23, 4)), Plate, 1.5f, 1.5f, 1.5f, stack: true);
            c.Groove(L((x, y) => Box(x, y, -35, 0, 6, 16) > 0 ? 9 : Mathf.Abs(Mod(x, 2.5f) - 1.25f) - 0.5f), 1, 0.5f);
            foreach (float s in new[] { -1f, 1f })
                for (int k = -2; k <= 2; k++)
                {
                    var (rx, ry) = LocalPoint(angle, o, k * 12, s * 23);
                    c.Rivet(rx, ry, 1f, 0.8f);
                }
            c.Shape(L((x, y) => Box(x, y, -6, 0, 24, 20, 8)), Team, 28, 4, 5);
            c.Shape(L((x, y) => Circle(x, y, -15, 9, 5.5f)), Plate, 29, 1.2f, 1.2f);
            c.Groove(L((x, y) => Circle(x, y, -15, 9, 3.6f)), 0.7f, 0.5f);
            c.Shape(L((x, y) => Box(x, y, 10, 0, 6, 15, 3)), Steel, 29, 2, 2);
            foreach (float side in new[] { -6f, 6f })
            {
                c.Shape(L((x, y) => Box(x, y, 32 - recoil, side, 20, 2.6f, 2)), Steel, 26, 1.8f, 2.2f);
                c.Shape(L((x, y) => Box(x, y, 52 - recoil, side, 3, 3.8f, 1)), Steel, 26, 1, 1.5f);
            }
            c.Grime(59, 0.3f, 0.12f, 8);
            if (frame >= 2)
                foreach (float side in new[] { -6f, 6f })
                    MuzzleFlash(c, L, 55 - recoil, side, frame == 2 ? 6f : 3.5f, 32);
            return c;
        }

        /// <summary>
        /// The Artillery (large size class, 2×2 canvas): a tracked self-propelled gun with a team casemate at the
        /// back, a very long barrel with a muzzle brake, ammunition racks and a recoil spade. Direct fire.
        /// Frames 0–1 move, 2–3 fire (a big flash and a long recoil).
        /// </summary>
        public static LitCanvas Artillery(float angle, int frame)
        {
            var c = new LitCanvas(LargeUnitSize, LargeUnitSize);
            float o = LargeUnitSize / 2f;
            var L = Local(angle, o);
            float tread = frame == 1 ? 2.5f : 0f, recoil = frame == 2 ? 6f : frame == 3 ? 3f : 0f;
            c.Shape(L((x, y) => Box(x, y, -45, 0, 3, 15, 1)), Dark, 6, 1, 1.5f);
            Tracks(c, L, -4, 40, 26, 9, tread, 5);
            c.Shape(L((x, y) => Box(x, y, -4, 0, 38, 21, 6)), Hull, 14, 3.5f, 4);
            c.Groove(L((x, y) => Box(x, y, 26, 0, 6, 14) > 0 ? 9 : Mathf.Abs(Mod(y, 3) - 1.5f) - 0.5f), 0.8f, 0.5f);
            foreach (float side in new[] { -1f, 1f })
                for (int k = 0; k < 4; k++)
                {
                    float sx = 6 + k * 5, sy = side * 15;
                    c.Shape(L((x, y) => Circle(x, y, sx, sy, 2)), Hazard, 16, 0.8f, 0.8f);
                }
            c.Shape(L((x, y) => Box(x, y, -16, 0, 20, 18, 6)), Team, 24, 3, 4);
            c.Shape(L((x, y) => Box(x, y, -20, 0, 10, 12, 3)), Dark, 19, 1.5f, 1.5f);
            c.Shape(L((x, y) => Circle(x, y, -22, 5, 3)), Plate, 21, 1, 1);
            c.Shape(L((x, y) => Box(x, y, 6, 0, 6, 9, 3)), Steel, 25, 2, 2);
            c.Shape(L((x, y) => Box(x, y, 32 - recoil, 0, 26, 3, 2.5f)), Steel, 24, 2, 2.5f);
            c.Shape(L((x, y) => Box(x, y, 59 - recoil, 0, 2.5f, 5, 1)), Steel, 24, 1, 1.5f);
            c.Groove(L((x, y) => Mathf.Abs(x - (20 - recoil)) - 0.4f > 0 || Mathf.Abs(y) > 3 ? 9 : 0), 0.8f, 0.5f);
            c.Grime(61, 0.3f, 0.12f, 8);
            if (frame >= 2) MuzzleFlash(c, L, 61.5f - recoil, 0, frame == 2 ? 8f : 4.5f, 30);
            return c;
        }

        /// <summary>
        /// The Heavy Factory (3×3): a heavy-vehicle assembly hall under a ribbed barrel-vault roof with a skylight
        /// along its ridge, a wide roller door to a striped apron where large units leave, a team band and
        /// roof plate, an office annex and two tall chimneys. Frame 1 dims the door lamp.
        /// </summary>
        public static LitCanvas HeavyFactory(int frame)
        {
            int s = 3 * TileSize;
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
            float o = s / 2f;
            float hx = o - 8, hy = o + 12, halfX = 72, halfY = 58;
            // The apron in front of the door, striped along its edge.
            c.Shape((x, y) => Box(x, y, hx, o - 70, 58, 20, 2), Concrete, 3, 1, 1.5f);
            Stripes(c, (x, y) => Box(x, y, hx, o - 86, 54, 3), 3.5f);
            // The hall and its vaulted roof: ribs every 16 px, a skylight strip on the ridge.
            Func<float, float, float> hall = (x, y) => Box(x, y, hx, hy, halfX, halfY, 3);
            c.Shape(hall, Plate, 22, 1.5f, 3);
            c.Displace((x, y) => hall(x, y) > -2 ? 0 : 12 * (1 - Mathf.Pow((y - hy) / halfY, 2)));
            c.Tint((x, y, col) => hall(x, y) > -2 ? col : Mathf.Abs(y - hy) < 4 && Mod(x - hx, 16) > 4 ? Glass.Albedo : col);
            c.Groove((x, y) => hall(x, y) > -2 ? 9 : Mathf.Abs(Mod(x - hx, 16) - 8) - 0.5f, 1.5f, 1f);
            // The wide roller door in the south wall, its lamp, and the team band and roof plate.
            c.Shape((x, y) => Box(x, y, hx, hy - halfY - 2, 30, 3.5f, 1), Dark, 12, 1, 1);
            c.Groove((x, y) => Box(x, y, hx, hy - halfY - 2, 29, 2.5f) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.7f, 0.8f, 0.8f);
            c.Shape((x, y) => Circle(x, y, hx + 36, hy - halfY - 1, 2), frame == 0 ? Lamp : Plate, 14, 0.6f, 0.4f);
            c.Shape((x, y) => Box(x, y, hx - halfX + 1, hy, 2.5f, halfY - 6, 1), Team, 24, 1, 1);
            c.Shape((x, y) => Box(x, y, hx + 40, hy + 34, 12, 8, 2), Team, 2, 1, 1.2f, stack: true);
            // The office annex on the east side, with an AC unit.
            c.Shape((x, y) => Box(x, y, o + 80, o - 30, 11, 26, 2.5f), Hull, 16, 2, 2.5f);
            c.Shape((x, y) => Box(x, y, o + 80, o - 20, 6, 5, 1), Plate, 19, 1, 1.5f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o + 80, o - 20, 3)) - 0.2f, 0.8f, 0.6f);
            // Two chimneys at the back of the hall.
            foreach (float cx in new[] { hx - 38f, hx - 14f })
            {
                c.Shape((x, y) => Circle(x, y, cx, hy + 44, 8), Steel, 62, 2.5f, 4);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, cx, hy + 44, 6.5f)) - 0.3f, 1, 0.8f);
                c.Shape((x, y) => Circle(x, y, cx, hy + 44, 4.5f), Dark, 56, 1, 1);
            }
            c.Grime(67, 0.2f, 0.08f, 10);
            return c;
        }

        /// <summary>
        /// The Siege Workshop (2×2): a gabled shed with an open bay, a team band on its gable end, a drill bit on a
        /// rack and a stack of striped explosive drums in the yard (it makes Diggers and Suicide bombers).
        /// Frame 1 dims the bay lamp.
        /// </summary>
        public static LitCanvas SiegeWorkshop(int frame)
        {
            int s = 2 * TileSize;
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
            float o = s / 2f;
            float hx = o - 12, hy = o + 14, halfX = 38, halfY = 32;
            c.Shape((x, y) => Box(x, y, o, o - 6, 60, 54, 3), Concrete, 3, 1, 1.5f);
            // The shed: a gable roof whose ridge runs east–west, with seams and a ridge line.
            Func<float, float, float> shed = (x, y) => Box(x, y, hx, hy, halfX, halfY, 2);
            c.Shape(shed, Plate, 18, 1.5f, 3);
            c.Displace((x, y) => shed(x, y) > -2 ? 0 : 18 * (1 - Mathf.Abs(y - hy) / halfY));
            c.Groove((x, y) => shed(x, y) > -2 ? 9 : Mathf.Abs(y - hy) - 0.5f, 1.2f, 0.8f);
            c.Groove((x, y) => shed(x, y) > -2 ? 9 : Mathf.Abs(Mod(x - hx, 10) - 5) - 0.4f, 0.8f, 0.6f);
            c.Shape((x, y) => Box(x, y, hx - halfX + 1, hy, 2.5f, halfY - 4, 1), Team, 20, 1, 1);
            c.Shape((x, y) => Box(x, y, hx + 16, hy + 18, 8, 5, 1.2f), Team, 2, 1, 1.2f, stack: true);
            // The open bay in the south wall and its lamp.
            c.Shape((x, y) => Box(x, y, hx, hy - halfY - 2, 18, 3, 1), Dark, 10, 1, 1);
            c.Shape((x, y) => Circle(x, y, hx + 23, hy - halfY - 1, 1.8f), frame == 0 ? Lamp : Plate, 12, 0.6f, 0.4f);
            // Explosive drums in the yard (east).
            foreach (var (dx, dy) in new[] { (o + 44f, o + 36f), (o + 46f, o + 16f), (o + 32f, o + 26f) })
            {
                Stripes(c, (x, y) => Circle(x, y, dx, dy, 8.5f), 14);
                c.Shape((x, y) => Circle(x, y, dx, dy, 2.5f), Dark, 15, 0.6f, 0.6f);
            }
            // A drill bit on its rack (south-east), flutes along it.
            float x0 = o + 14, x1 = o + 52, by = o - 36;
            c.Shape((x, y) => Box(x, y, x0 - 2, by, 4, 9, 1), Dark, 6, 1, 1);
            Func<float, float, float> bit = (x, y) =>
            {
                float t = Mathf.Clamp01((x - x0) / (x1 - x0));
                return Mathf.Max(Mathf.Abs(y - by) - Mathf.Lerp(7f, 0.8f, t), Mathf.Max(x0 - x, x - x1));
            };
            c.Shape(bit, Steel, 10, 3, 5);
            c.Groove((x, y) => bit(x, y) > 0 ? 9 : Mathf.Abs(Mod(x - x0 + (y - by) * 0.9f, 5) - 2.5f) - 0.9f, 1.4f, 0.9f);
            c.Grime(71, 0.2f, 0.08f, 8);
            return c;
        }
    }
}
