using System;
using UnityEngine;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// The v0.13 Bastion sprites (spec <c>2026-10-16-v0.13-bastion-design.md</c> §2 and §3): the Gun turret as a
    /// base (1×1) with a head drawn over it in 8 facings, and the Jammer (2×2). Same look and light as the rest
    /// of <see cref="RenderGen"/>, drawn for the 3/4 view (<see cref="Lift"/>). The base and the head share one
    /// canvas size and pivot, so the head is drawn over the base at the same anchor.
    /// </summary>
    public static partial class RenderGen
    {
        /// <summary>The Gun turret's canvas: 1×1 with headroom for the head's barrel pointing north.</summary>
        private static LitCanvas TurretCanvas() => new LitCanvas(TileSize, TileSize + Headroom / 2, TileSize / 2f);

        /// <summary>
        /// Output pixels from the turret's centre to its muzzle, along the head's facing (before the 3/4 lift),
        /// and the muzzle's height (it rises <c>height × Lift</c> px up the screen): where the muzzle flash goes.
        /// </summary>
        public const float TurretMuzzleReach = 23.5f, TurretMuzzleHeight = 22f;

        /// <summary>
        /// The Gun turret's base (1×1): an octagonal armoured plinth on a concrete pad, a team ring, rivets at its
        /// corners, a status lamp and the dark race ring the head turns on. Frame 0 is whole; frame 1 is damaged
        /// (cracked and scorched plates, the lamp out).
        /// </summary>
        public static LitCanvas GunTurretBase(int frame)
        {
            LitCanvas c = TurretCanvas();
            float o = TileSize / 2f;
            bool damaged = frame == 1;
            c.Shape((x, y) => Box(x, y, o, o, 29, 29, 3), Concrete, 3, 1, 1.5f);
            Stripes(c, (x, y) => Box(x, y, o, o - 26.5f, 20, 2), 3.5f);
            Func<float, float, float> plinth = (x, y) => Polygon(x, y, o, o, 25, 8, Mathf.PI / 8);
            c.Shape(plinth, Hull, 12, 3, 4);
            c.Groove((x, y) => Polygon(x, y, o, o, 21, 8, Mathf.PI / 8), 1f, 0.7f);
            // Seams from each corner of the inner plate toward the ring.
            c.Groove((x, y) =>
            {
                float d = Polygon(x, y, o, o, 21, 8, Mathf.PI / 8);
                if (d > 0 || Circle(x, y, o, o, 15) < 0) return 9;
                float a = Mathf.Atan2(y - o, x - o) / (Mathf.PI / 4);
                return (Mathf.Abs(a - Mathf.Round(a)) * Mathf.PI / 4) * new Vector2(x - o, y - o).magnitude - 0.4f;
            }, 0.8f, 0.6f);
            c.Shape((x, y) => Mathf.Abs(Circle(x, y, o, o, 19.5f)) - 1.4f, Team, 1.2f, 0.6f, 0.8f, stack: true);
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4;
                c.Rivet(o + Mathf.Cos(a) * 22.5f, o + Mathf.Sin(a) * 22.5f, 1.1f, 0.8f);
            }
            c.Shape((x, y) => Circle(x, y, o, o, 13.5f), Dark, 13, 1, 1.5f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o, o, 11.5f)) - 0.3f, 0.7f, 0.5f);
            c.Shape((x, y) => Circle(x, y, o + 15, o - 15, 1.6f), damaged ? Dark : Lamp, 13, 0.5f, 0.4f);
            c.Grime(83, damaged ? 0.45f : 0.25f, damaged ? 0.2f : 0.1f, 6);
            if (damaged)
            {
                // Cracks across the plinth and soot from hits.
                c.Groove((x, y) => plinth(x, y) > -1 ? 9 : (Mathf.Abs(Noise.Fbm(x, y, TileSize, 3, 3, 89) - 0.5f) - 0.018f) * 60, 1.6f, 0.9f);
                c.Tint((x, y, col) => col * Mathf.Lerp(1f, 0.45f, Mathf.Clamp01((Noise.Fbm(x, y, TileSize, 3, 3, 97) - 0.45f) * 4)));
            }
            return c;
        }

        /// <summary>
        /// The Gun turret's head, drawn over <see cref="GunTurretBase"/>: a steel mount on the race ring, a
        /// team-coloured gun shield with a sight hatch, a rear counterweight and a single barrel with a muzzle
        /// brake. <paramref name="angle"/> is the facing (radians, 0 = east, anticlockwise; facing <c>d</c> of 8
        /// is <c>d × π/4</c>, as for units). Frame 0 rests; frame 1 recoils (the shot).
        /// </summary>
        public static LitCanvas GunTurretHead(float angle, int frame = 0)
        {
            LitCanvas c = TurretCanvas();
            float o = TileSize / 2f;
            var L = Local(angle, o);
            float recoil = frame == 1 ? 3f : 0f;
            // Every part floats (thickness): it stands on the base's race ring (height 13), not on the ground, so
            // the 3/4 view draws no wall from it down to the pad.
            c.Shape((x, y) => Circle(x, y, o, o, 11.5f), Steel, 17.5f, 1.5f, 2.5f, thickness: 4.5f);
            c.Shape(L((x, y) => Box(x, y, -10, 0, 4, 6.5f, 1.5f)), Plate, 21, 1.2f, 1.5f, thickness: 4f);
            c.Shape(L((x, y) => Mathf.Max(Box(x, y, -1, 0, 9, 8.5f, 3.5f), Polygon(x, y, 0, 0, 13.5f, 6, 0))), Team, 24, 2, 3, thickness: 7f);
            c.Groove(L((x, y) => Mathf.Abs(x - 3.5f) - 0.4f > 0 || Mathf.Abs(y) > 7 ? 9 : 0), 0.8f, 0.5f);
            c.Shape(L((x, y) => Circle(x, y, -4, 4, 2.4f)), Plate, 25, 0.8f, 0.8f, thickness: 1.5f);
            c.Groove(L((x, y) => Circle(x, y, -4, 4, 1.4f)), 0.5f, 0.4f);
            c.Shape(L((x, y) => Box(x, y, 8.5f, -3.5f, 1.5f, 1.2f, 0.5f)), Glass, 25, 0.5f, 0.5f, thickness: 1.5f);
            // The barrel leaves the shield at its own height and stays over the plinth in every facing.
            c.Shape(L((x, y) => Box(x, y, 13 - recoil, 0, 7.5f, 2.2f, 1.5f)), Steel, TurretMuzzleHeight, 1.2f, 1.5f, thickness: 3.5f);
            c.Shape(L((x, y) => Box(x, y, TurretMuzzleReach - 1.8f - recoil, 0, 1.8f, 2.9f, 0.8f)), Steel, TurretMuzzleHeight + 0.5f, 0.8f, 1.2f, thickness: 4f);
            c.Grime(85, 0.25f, 0.1f, 6);
            return c;
        }

        /// <summary>
        /// The Jammer (2×2): an upturned dish (concave, lit inside) on a pedestal with a feed horn held over it on
        /// three struts, three tall whip antennas with lamps at their tips, an equipment hut with a team band and
        /// a generator. A relative of the Radar, but its dish faces the sky and it bristles with aerials.
        /// Frames alternate the antenna lamps.
        /// </summary>
        public static LitCanvas Jammer(int frame)
        {
            int s = 2 * TileSize;
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
            float o = s / 2f;
            c.Shape((x, y) => Box(x, y, o, o - 6, 54, 46, 3), Concrete, 3, 1, 1.5f);
            Stripes(c, (x, y) => Box(x, y, o + 22, o - 49, 24, 2.5f), 3.5f);
            // The equipment hut (south-west) with a team band and a vent, and a generator (south-east).
            c.Shape((x, y) => Box(x, y, o - 28, o - 26, 20, 15, 2.5f), Hull, 16, 2, 3);
            c.Shape((x, y) => Box(x, y, o - 28, o - 15, 16, 3.5f, 1), Team, 1, 1, 1.2f, stack: true);
            c.Shape((x, y) => Box(x, y, o - 20, o - 31, 6, 5, 1), Plate, 18, 1, 1.5f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, o - 20, o - 31, 3)) - 0.2f, 0.8f, 0.6f);
            c.Groove((x, y) => Box(x, y, o + 22, o - 30, 12, 9) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.6f, 0.8f, 0.6f);
            c.Shape((x, y) => Box(x, y, o + 22, o - 30, 13, 10, 2), Plate, 12, 1.5f, 2);
            c.Shape((x, y) => Mathf.Min(Capsule(x, y, o - 8, o - 26, o + 9, o - 26, 1.6f), Capsule(x, y, o + 4, o - 26, o + 4, o - 4, 1.6f)), Dark, 6, 0.8f, 1);
            // The dish: a pedestal, then a bowl whose rim stands higher than its centre, ringed in team colour.
            float dx = o + 6, dy = o + 14, r = 23;
            c.Shape((x, y) => Box(x, y, dx, dy - 4, 10, 8, 2), Hull, 20, 1.5f, 2);
            Func<float, float, float> dish = (x, y) => Circle(x, y, dx, dy, r);
            c.Shape(dish, Steel, 26, 5, 8);
            c.Displace((x, y) =>
            {
                float d = new Vector2(x - dx, y - dy).magnitude / (r - 2);
                return d >= 1 ? 0 : -9 * (1 - d * d);
            });
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, dx, dy, 15)) - 0.3f, 0.8f, 0.6f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, dx, dy, 8)) - 0.3f, 0.6f, 0.5f);
            c.Shape((x, y) => Mathf.Abs(dish(x, y) + 3.5f) - 1.3f, Team, 0.8f, 0.6f, 0.6f, stack: true);
            // Three struts from the rim to the feed horn over the dish's centre.
            for (int k = 0; k < 3; k++)
            {
                float a = Mathf.PI / 2 + k * 2 * Mathf.PI / 3;
                float ex = dx + Mathf.Cos(a) * (r - 4), ey = dy + Mathf.Sin(a) * (r - 4);
                c.Shape((x, y) => Capsule(x, y, ex, ey, dx, dy, 0.9f), Steel, 30, 0.5f, 0.6f);
            }
            c.Shape((x, y) => Circle(x, y, dx, dy, 3.6f), Plate, 34, 1, 1.5f);
            c.Shape((x, y) => Circle(x, y, dx, dy, 1.6f), new Material(C(WAR2D.Art.Style.StylePalette.Lamp) * 0.9f, 0.1f, 20, glow: 0.7f), 35, 0.5f, 0.3f);
            // Whip antennas: thin poles (the lift draws them as tall masts) with a lamp each; the lamps alternate.
            var whips = new[] { (o - 42f, o + 32f, 56f), (o + 46f, o + 30f, 62f), (o + 46f, o - 8f, 48f) };
            for (int k = 0; k < whips.Length; k++)
            {
                var (wx, wy, h) = whips[k];
                c.Shape((x, y) => Box(x, y, wx, wy, 3.5f, 3.5f, 1), Plate, 5, 1, 1.5f);
                c.Shape((x, y) => Circle(x, y, wx, wy, 1.4f), Steel, h, 0.5f, 0.5f);
                bool lit = (k + frame) % 2 == 0;
                c.Shape((x, y) => Circle(x, y, wx, wy, 2.2f), lit ? Lamp : Plate, h + 2, 0.6f, 0.4f);
            }
            c.Grime(91, 0.2f, 0.08f, 8);
            return c;
        }
    }
}
