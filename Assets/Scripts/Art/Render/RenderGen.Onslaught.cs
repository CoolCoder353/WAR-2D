using System;
using UnityEngine;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// The v0.20 Onslaught sprites (spec <c>2026-11-06-v0.20-onslaught-design.md</c> §5.1, §6.3 and §8.3), drawn for the owner's
    /// asset review before they replace the 1×1 Sniper and Gun turret: the Sniper as a 2×2 unit (large size class) with a turning
    /// head, and the Gun turret as a 2×2 building with a twin-barrelled turning head. Same look and light as the rest of
    /// <see cref="RenderGen"/>. Nothing here is in the atlases until the owner approves it (<c>docs/art/v0.20-asset-review.md</c>).
    /// </summary>
    public static partial class RenderGen
    {
        /// <summary>Art pixels along the 2×2 Sniper's centre line from its centre to the point its head turns about.</summary>
        public const float Sniper2x2Pivot = -10f;

        /// <summary>Art pixels from the 2×2 Sniper's head pivot to its muzzle: where the aim laser and the shot leave.</summary>
        public const float Sniper2x2MuzzleReach = 66f, Sniper2x2MuzzleHeight = 24f;

        /// <summary>
        /// The 2×2 Sniper (large size class, 2×2 canvas; its head alone needs 3 tiles for the barrel): a long, low eight-wheeled
        /// tank destroyer with folded stabiliser legs, a rear engine deck, and a low wedge-shaped team head carrying a big scope
        /// and a very long barrel with a muzzle brake. Frames 0–1 move, 2–3 fire (a sharp, long flash and a hard recoil).
        /// </summary>
        public static LitCanvas Sniper2x2(float angle, int frame) => Sniper2x2(angle, frame, angle);

        /// <summary>The 2×2 Sniper with its head turned to <paramref name="turret"/>.</summary>
        public static LitCanvas Sniper2x2(float angle, int frame, float turret, UnitLayer layer = UnitLayer.Whole)
        {
            int size = layer == UnitLayer.Turret ? 3 * TileSize : LargeUnitSize;
            var c = new LitCanvas(size, size);
            float o = size / 2f;
            var L = Local(angle, o);
            var T = TurretLocal(angle, turret, o, Sniper2x2Pivot, layer);
            float spin = frame == 1 ? 1.5f : 0f, recoil = frame == 2 ? 5f : frame == 3 ? 2.5f : 0f;
            if (layer == UnitLayer.Turret) c.Floor(14);
            else
            {
                Wheels(c, L, new[] { -33f, -12f, 9f, 30f }, 25, 7.5f, 4.5f, spin);
                // Folded stabiliser legs along each side, with their pads at the back.
                foreach (float side in new[] { -1f, 1f })
                {
                    c.Shape(L((x, y) => Capsule(x, y, -38, side * 31, 14, side * 31, 2.2f)), Dark, 8, 0.8f, 1);
                    c.Shape(L((x, y) => Box(x, y, -40, side * 31, 3.5f, 4.5f, 1)), Plate, 9, 1, 1.2f);
                }
                // A long, narrow hull with a sharp wedge nose.
                c.Shape(L((x, y) => Mathf.Max(Box(x, y, -2, 0, 44, 19, 5), Polygon(x, y, -6, 0, 52, 6, 0))), Hull, 14, 3.5f, 4);
                c.Shape(L((x, y) => Mathf.Max(Box(x, y, 30, 0, 12, 15, 3), Polygon(x, y, 22, 0, 22, 6, 0))), Plate, 1.5f, 1.5f, 1.5f, stack: true);
                // The engine deck: louvres and an exhaust each side.
                c.Groove(L((x, y) => Box(x, y, -34, 0, 7, 12) > 0 ? 9 : Mathf.Abs(Mod(x, 3) - 1.5f) - 0.55f), 1.1f, 0.5f);
                foreach (float side in new[] { -1f, 1f })
                    c.Shape(L((x, y) => Circle(x, y, -42, side * 13, 2.4f)), Dark, 16, 0.6f, 0.8f);
                foreach (float s in new[] { -1f, 1f })
                    for (int k = -1; k <= 2; k++)
                    {
                        var (rx, ry) = LocalPoint(angle, o, k * 13 - 4, s * 16);
                        c.Rivet(rx, ry, 1f, 0.8f);
                    }
            }
            if (layer != UnitLayer.Hull)
            {
                // A low wedge head (the pivot is its centre), the scope on its left, an aerial and a hatch.
                c.Shape(T((x, y) => Mathf.Max(Box(x, y, -12, 0, 16, 13, 4), Polygon(x, y, -14, 0, 21, 5, 0))), Team, 22, 3, 3.5f);
                c.Shape(T((x, y) => Circle(x, y, -20, -5, 4)), Plate, 23, 1, 1.2f);
                c.Groove(T((x, y) => Circle(x, y, -20, -5, 2.6f)), 0.6f, 0.4f);
                c.Shape(T((x, y) => Box(x, y, -6, 8.5f, 9, 2.4f, 1.2f)), Dark, 26, 0.8f, 1);
                c.Shape(T((x, y) => Circle(x, y, 3.5f, 8.5f, 2f)), Glass, 26.5f, 0.6f, 0.6f);
                c.Shape(T((x, y) => Circle(x, y, -26, 6, 1.2f)), Steel, 34, 0.4f, 0.4f);
                // The barrel: a thick mantlet, a long thin tube with a mid-length band, and a slotted muzzle brake.
                c.Shape(T((x, y) => Box(x, y, 6, 0, 5, 6, 2)), Steel, 24, 1.5f, 1.5f);
                c.Shape(T((x, y) => Box(x, y, 36 - recoil, 0, 28, 1.7f, 1.2f)), Steel, Sniper2x2MuzzleHeight, 1.2f, 1.5f);
                c.Shape(T((x, y) => Box(x, y, 34 - recoil, 0, 2, 2.6f, 0.8f)), Steel, Sniper2x2MuzzleHeight + 0.5f, 0.8f, 1);
                c.Shape(T((x, y) => Box(x, y, Sniper2x2MuzzleReach - 3 - recoil, 0, 3, 2.8f, 0.8f)), Steel, Sniper2x2MuzzleHeight + 0.5f, 0.8f, 1.2f);
                c.Groove(T((x, y) => Mathf.Abs(x - (Sniper2x2MuzzleReach - 3 - recoil)) - 0.5f > 0 || Mathf.Abs(y) > 2.8f ? 9 : 0), 0.8f, 0.5f);
            }
            c.Grime(67, 0.3f, 0.12f, 8);
            if (frame >= 2 && layer == UnitLayer.Whole) MuzzleFlash(c, T, Sniper2x2MuzzleReach + 1 - recoil, 0, frame == 2 ? 5f : 3f, 26);
            return c;
        }

        /// <summary>The 2×2 Gun turret's canvas: 2×2 with headroom for the head's barrels pointing north.</summary>
        private static LitCanvas Turret2x2Canvas() => new LitCanvas(2 * TileSize, 2 * TileSize + Headroom / 2, TileSize);

        /// <summary>
        /// Output pixels from the 2×2 turret's centre to its muzzles, along the head's facing (before the 3/4 lift), the muzzles'
        /// height, and how far each barrel sits to the side of the centre line: where the twin flashes go.
        /// </summary>
        public const float Turret2x2MuzzleReach = 46f, Turret2x2MuzzleHeight = 34f, Turret2x2BarrelSide = 7f;

        /// <summary>
        /// The 2×2 Gun turret's base, standing straight on the ground (no pad): an octagonal armoured plinth with a team ring,
        /// rivets, a status lamp and the dark race ring the head turns on. Frame 0 is whole; frame 1 is damaged (cracked and scorched plates, the lamp out).
        /// </summary>
        public static LitCanvas GunTurret2x2Base(int frame)
        {
            LitCanvas c = Turret2x2Canvas();
            float o = TileSize;
            bool damaged = frame == 1;
            Func<float, float, float> plinth = (x, y) => Polygon(x, y, o, o, 48, 8, Mathf.PI / 8);
            c.Shape(plinth, Hull, 16, 3.5f, 5);
            c.Groove((x, y) => Polygon(x, y, o, o, 42, 8, Mathf.PI / 8), 1.1f, 0.8f);
            c.Groove((x, y) =>
            {
                float d = Polygon(x, y, o, o, 42, 8, Mathf.PI / 8);
                if (d > 0 || Circle(x, y, o, o, 29) < 0) return 9;
                float a = Mathf.Atan2(y - o, x - o) / (Mathf.PI / 4);
                return (Mathf.Abs(a - Mathf.Round(a)) * Mathf.PI / 4) * new Vector2(x - o, y - o).magnitude - 0.45f;
            }, 0.8f, 0.6f);
            c.Shape((x, y) => Mathf.Abs(Circle(x, y, o, o, 36f)) - 2f, Team, 1.4f, 0.7f, 0.9f, stack: true);
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4;
                c.Rivet(o + Mathf.Cos(a) * 44f, o + Mathf.Sin(a) * 44f, 1.4f, 1f);
            }
            c.Shape((x, y) => Circle(x, y, o, o, 27), Dark, 17, 1.2f, 1.8f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o, o, 24)) - 0.35f, 0.8f, 0.5f);
            c.Shape((x, y) => Circle(x, y, o + 30, o - 30, 2.2f), damaged ? Dark : Lamp, 17, 0.6f, 0.5f);
            c.Grime(87, damaged ? 0.45f : 0.25f, damaged ? 0.2f : 0.1f, 8);
            if (damaged)
            {
                c.Groove((x, y) => plinth(x, y) > -1 ? 9 : (Mathf.Abs(Noise.Fbm(x, y, 2 * TileSize, 3, 3, 89) - 0.5f) - 0.016f) * 60, 1.6f, 0.9f);
                c.Tint((x, y, col) => col * Mathf.Lerp(1f, 0.45f, Mathf.Clamp01((Noise.Fbm(x, y, 2 * TileSize, 3, 3, 97) - 0.45f) * 4)));
            }
            return c;
        }

        /// <summary>
        /// The 2×2 Gun turret's head, drawn over <see cref="GunTurret2x2Base"/>: a wide steel mount on the race ring, a team gun
        /// shield with two sight hatches, ammunition drums each side, a rear counterweight, and twin barrels with perforated
        /// cooling jackets and flash hiders. <paramref name="angle"/> is the facing (radians, 0 = east, anticlockwise). Frame 0
        /// rests; frame 1 fires the left barrel (it recoils), frame 2 the right: the game alternates them.
        /// </summary>
        public static LitCanvas GunTurret2x2Head(float angle, int frame = 0)
        {
            LitCanvas c = Turret2x2Canvas();
            float o = TileSize;
            var L = Local(angle, o);
            c.Shape((x, y) => Circle(x, y, o, o, 24f), Steel, 24, 1.8f, 3, thickness: 6f);
            c.Shape(L((x, y) => Box(x, y, -19, 0, 6, 12, 2)), Plate, 29, 1.4f, 1.8f, thickness: 5f);
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape(L((x, y) => Circle(x, y, -4, side * 19, 6f)), Plate, 30, 1.2f, 1.5f, thickness: 6f);
                c.Groove(L((x, y) => Mathf.Abs(Circle(x, y, -4, side * 19, 3.8f)) - 0.3f), 0.6f, 0.4f);
            }
            c.Shape(L((x, y) => Mathf.Max(Box(x, y, -1, 0, 15, 15, 5), Polygon(x, y, 1, 0, 22, 6, 0))), Team, 34, 2.5f, 3.5f, thickness: 10f);
            c.Groove(L((x, y) => Mathf.Abs(x - 6f) - 0.45f > 0 || Mathf.Abs(y) > 13 ? 9 : 0), 0.9f, 0.5f);
            foreach (float side in new[] { -1f, 1f })
                c.Shape(L((x, y) => Box(x, y, 12, side * 11, 2, 1.6f, 0.6f)), Glass, 35, 0.6f, 0.6f, thickness: 2f);
            c.Shape(L((x, y) => Circle(x, y, -7, 0, 3.4f)), Plate, 35, 1f, 1f, thickness: 2f);
            c.Groove(L((x, y) => Circle(x, y, -7, 0, 2f)), 0.5f, 0.4f);
            for (int b = 0; b < 2; b++)
            {
                float side = b == 0 ? Turret2x2BarrelSide : -Turret2x2BarrelSide;
                float recoil = frame == b + 1 ? 4f : 0f;
                // The jacket, its cooling holes, the bare barrel beyond it and the flash hider.
                c.Shape(L((x, y) => Box(x, y, 22 - recoil, side, 9, 3.2f, 1.5f)), Steel, Turret2x2MuzzleHeight, 1.2f, 1.5f, thickness: 4f);
                c.Groove(L((x, y) => Box(x, y, 22 - recoil, side, 7.5f, 2) > 0 ? 9 : Circle(Mod(x, 3.2f), y, 1.6f, side, 0.8f)), 0.7f, 0.4f);
                c.Shape(L((x, y) => Box(x, y, 37 - recoil, side, 7, 1.6f, 1)), Steel, Turret2x2MuzzleHeight, 0.8f, 1.2f, thickness: 3f);
                c.Shape(L((x, y) => Box(x, y, Turret2x2MuzzleReach - 2 - recoil, side, 2.2f, 2.4f, 0.8f)), Steel, Turret2x2MuzzleHeight + 0.5f, 0.8f, 1.2f, thickness: 3.5f);
            }
            c.Grime(89, 0.25f, 0.1f, 8);
            return c;
        }
    

        /// <summary>
        /// The v0.20 Artillery shell in flight, larger than v0.19's for the 160-damage, 3.5-tile blast: a long steel body with a
        /// fuze, two brass driving bands and four tail fins. Drawn in a tile, turned to <paramref name="angle"/>.
        /// </summary>
        public static LitCanvas HeavyShellBody(float angle)
        {
            var c = new LitCanvas(TileSize, TileSize);
            var L = Local(angle, TileSize / 2f);
            foreach (float side in new[] { -1f, 1f }) c.Shape(L((x, y) => Box(x, y, -14, side * 5f, 3.4f, 2.6f, 0.8f)), Dark, 6, 0.8f, 0.8f);
            c.Shape(L((x, y) => Box(x, y, -14, 0, 3.4f, 1.2f, 0.5f)), Dark, 7, 0.6f, 0.6f);
            c.Shape(L((x, y) => Box(x, y, -3, 0, 12, 6.2f, 2.5f)), Steel, 10, 2.2f, 3);
            c.Shape(L((x, y) => Circle(x, y, 8, 0, 6.2f)), Steel, 10, 2.2f, 3);
            c.Shape(L((x, y) => Circle(x, y, 12.5f, 0, 2.8f)), Plate, 11, 1, 1.2f);
            foreach (float bx in new[] { -6f, -1.5f }) c.Shape(L((x, y) => Box(x, y, bx, 0, 1.2f, 6.4f)), Hazard, 11, 0.6f, 0.6f);
            return c;
        }
    }
}
