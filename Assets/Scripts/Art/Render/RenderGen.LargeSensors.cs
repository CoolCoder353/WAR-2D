using System;
using UnityEngine;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// The Large Radar and Large Jammer (3×3): their own designs at the 3×3 canvas in the look of
    /// <see cref="Radar"/> and <see cref="Jammer"/>, drawn for the 3/4 view (<see cref="Lift"/>).
    /// </summary>
    public static partial class RenderGen
    {
        /// <summary>
        /// The Large Radar (3×3): a long-range station. A wide parabolic dish with ribs and a team ring on a braced mast,
        /// a control block with a team band and windows, a generator bank and a radome. Frames turn the dish.
        /// </summary>
        public static LitCanvas LargeRadar(int frame)
        {
            int s = 3 * TileSize;
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
            float o = s / 2f;
            c.Shape((x, y) => Box(x, y, o, o - 6, 88, 80, 3), Concrete, 3, 1, 1.5f);
            Stripes(c, (x, y) => Box(x, y, o, o - 83, 62, 2.5f), 3.5f);
            // The control block (south-west): a team band over a row of windows.
            c.Shape((x, y) => Box(x, y, o - 48, o - 46, 34, 24, 3), Hull, 18, 2, 3);
            c.Shape((x, y) => Box(x, y, o - 48, o - 30, 26, 5, 1), Team, 1, 1, 1.2f, stack: true);
            foreach (float wx in new[] { o - 64f, o - 48f, o - 32f })
                c.Shape((x, y) => Box(x, y, wx, o - 56, 5, 3, 1), Glass, 0.6f, 0.5f, 0.5f, stack: true);
            // The generator bank (south-east), its grille and a conduit to the mast.
            c.Shape((x, y) => Mathf.Min(Capsule(x, y, o - 14, o - 36, o + 18, o - 36, 1.8f), Capsule(x, y, o - 8, o - 36, o - 8, o - 14, 1.8f)), Dark, 6, 0.8f, 1);
            c.Shape((x, y) => Box(x, y, o + 50, o - 50, 30, 18, 2), Plate, 14, 1.5f, 2);
            c.Groove((x, y) => Box(x, y, o + 50, o - 50, 26, 14) > 0 ? 9 : Mathf.Abs(Mod(x, 5) - 2.5f) - 0.6f, 1f, 0.7f);
            // The radome (east): a dome on a low collar, ringed in team colour.
            c.Shape((x, y) => Box(x, y, o + 64, o + 16, 21, 21, 3), Plate, 6, 1, 1.5f);
            c.Shape((x, y) => Circle(x, y, o + 64, o + 16, 18), Steel, 30, 15, 18);
            c.Shape((x, y) => Mathf.Abs(Circle(x, y, o + 64, o + 16, 15.5f)) - 1.4f, Team, 0.8f, 0.6f, 0.6f, stack: true);
            // The mast: braces from footings on the pad to a tall column.
            float mx = o - 8;
            foreach (float side in new[] { -1f, 1f })
            {
                c.Shape((x, y) => Box(x, y, mx + side * 18, o - 30, 4, 4, 1), Plate, 5, 1, 1.5f);
                c.Shape((x, y) => Capsule(x, y, mx + side * 18, o - 30, mx + side * 4, o - 6, 2), Steel, 16, 1, 1.2f);
            }
            c.Shape((x, y) => Box(x, y, mx, o - 2, 6, 34, 2), Steel, 40, 1.5f, 2);
            // The dish.
            float shift = frame == 0 ? -7f : 7f;
            float dx = mx + shift, dy = o + 36, r = 38;
            Func<float, float, float> dish = (x, y) => Circle(x, y, dx, dy, r);
            c.Shape(dish, Steel, 58, 4, 6);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, dx, dy, 27)) - 0.35f, 1f, 0.7f);
            c.Groove((x, y) => Mathf.Abs(Circle(x, y, dx, dy, 17)) - 0.35f, 1f, 0.7f);
            c.Groove((x, y) =>
            {
                if (dish(x, y) > -5 || Circle(x, y, dx, dy, 11) < 0) return 9;
                float a = Mathf.Atan2(y - dy, x - dx) / (Mathf.PI / 4);
                return Mathf.Abs(a - Mathf.Round(a)) * Mathf.PI / 4 * new Vector2(x - dx, y - dy).magnitude - 0.4f;
            }, 0.9f, 0.6f);
            c.Shape((x, y) => Mathf.Abs(dish(x, y) + 3.5f) - 1.6f, Team, 0.8f, 0.6f, 0.6f, stack: true);
            c.Shape((x, y) => Circle(x, y, dx, dy, 9), Team, 61, 1.5f, 2.5f);
            c.Shape((x, y) => Circle(x, y, dx, dy, 3.2f), Lamp, 63, 0.8f, 0.5f);
            c.Grime(103, 0.2f, 0.08f, 10);
            return c;
        }

        /// <summary>
        /// The Large Jammer (3×3): a wide upturned dish with a feed horn on four struts, five whip antennas and a panel
        /// array, two equipment huts and a generator. Frames alternate the lamps. Its dish is the <see cref="Jammer"/>'s at
        /// 1.5 times (centre, radius and rim height), so <c>JamPulse</c> drawn 1.5 times bigger lands on it.
        /// </summary>
        public static LitCanvas LargeJammer(int frame)
        {
            int s = 3 * TileSize;
            var c = new LitCanvas(s, s + Headroom / 2, s / 2f);
            float o = s / 2f;
            c.Shape((x, y) => Box(x, y, o, o - 6, 88, 80, 3), Concrete, 3, 1, 1.5f);
            Stripes(c, (x, y) => Box(x, y, o + 30, o - 83, 54, 2.5f), 3.5f);
            // Two huts (south-west) with team bands and vents, a generator (south-east) and conduits to the dish.
            foreach (float hx in new[] { o - 62f, o - 26f })
            {
                c.Shape((x, y) => Box(x, y, hx, o - 50, 16, 20, 2.5f), Hull, 16, 2, 3);
                c.Shape((x, y) => Box(x, y, hx, o - 35, 12, 3.5f, 1), Team, 1, 1, 1.2f, stack: true);
                c.Shape((x, y) => Box(x, y, hx, o - 58, 6, 5, 1), Plate, 18, 1, 1.5f);
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, hx, o - 58, 3)) - 0.2f, 0.8f, 0.6f);
            }
            c.Shape((x, y) => Mathf.Min(Capsule(x, y, o - 8, o - 44, o + 26, o - 44, 1.6f), Capsule(x, y, o + 9, o - 44, o + 9, o - 12, 1.6f)), Dark, 6, 0.8f, 1);
            c.Shape((x, y) => Box(x, y, o + 50, o - 46, 22, 16, 2), Plate, 12, 1.5f, 2);
            c.Groove((x, y) => Box(x, y, o + 50, o - 46, 19, 13) > 0 ? 9 : Mathf.Abs(Mod(x, 4) - 2) - 0.6f, 0.8f, 0.6f);
            // The dish: a pedestal, then a bowl whose rim stands higher than its centre, ringed in team colour.
            float dx = o + 9, dy = o + 21, r = 34.5f;
            c.Shape((x, y) => Box(x, y, dx, dy - 6, 15, 12, 2), Hull, 30, 1.5f, 2);
            Func<float, float, float> dish = (x, y) => Circle(x, y, dx, dy, r);
            c.Shape(dish, Steel, 39, 6, 10);
            c.Displace((x, y) =>
            {
                float d = new Vector2(x - dx, y - dy).magnitude / (r - 3);
                return d >= 1 ? 0 : -14 * (1 - d * d);
            });
            foreach (float ring in new[] { 24f, 16f, 8f })
                c.Groove((x, y) => Mathf.Abs(Circle(x, y, dx, dy, ring)) - 0.3f, 0.8f, 0.6f);
            c.Shape((x, y) => Mathf.Abs(dish(x, y) + 4.5f) - 1.6f, Team, 0.8f, 0.6f, 0.6f, stack: true);
            // Four struts from the rim to the feed horn over the dish's centre.
            for (int k = 0; k < 4; k++)
            {
                float a = Mathf.PI / 4 + k * Mathf.PI / 2;
                float ex = dx + Mathf.Cos(a) * (r - 5), ey = dy + Mathf.Sin(a) * (r - 5);
                c.Shape((x, y) => Capsule(x, y, ex, ey, dx, dy, 1.1f), Steel, 44, 0.5f, 0.6f);
            }
            c.Shape((x, y) => Circle(x, y, dx, dy, 5.5f), Plate, 50, 1, 1.5f);
            c.Shape((x, y) => Circle(x, y, dx, dy, 2.4f), new Material(C(WAR2D.Art.Style.StylePalette.Lamp) * 0.9f, 0.1f, 20, glow: 0.7f), 51, 0.5f, 0.3f);
            // An array of tall panel antennas (north-west): a row of thin plates on one plinth, each with a team cap.
            float ax = o - 56, ay = o + 40;
            c.Shape((x, y) => Box(x, y, ax, ay, 25, 6, 1), Plate, 5, 1, 1.5f);
            for (int k = -1; k <= 1; k++)
            {
                float px = ax + k * 15, top = 66 - 10 * Mathf.Abs(k);
                c.Shape((x, y) => Box(x, y, px, ay, 6, 2, 0.5f), Steel, top, 0.5f, 0.5f);
                c.Shape((x, y) => Box(x, y, px, ay, 6, 2, 0.5f), Team, 1f, 0.3f, 0.3f, stack: true);
            }
            // Whip antennas: thin poles (the lift draws them as tall masts) with a lamp each; the lamps alternate.
            var whips = new[] { (o - 80f, o + 8f, 60f), (o - 10f, o + 66f, 76f), (o + 58f, o + 62f, 88f), (o + 78f, o + 22f, 66f), (o + 78f, o - 18f, 52f) };
            for (int k = 0; k < whips.Length; k++)
            {
                var (wx, wy, h) = whips[k];
                c.Shape((x, y) => Box(x, y, wx, wy, 3.5f, 3.5f, 1), Plate, 5, 1, 1.5f);
                c.Shape((x, y) => Circle(x, y, wx, wy, 1.4f), Steel, h, 0.5f, 0.5f);
                bool lit = (k + frame) % 2 == 0;
                c.Shape((x, y) => Circle(x, y, wx, wy, 2.2f), lit ? Lamp : Plate, h + 2, 0.6f, 0.4f);
            }
            c.Grime(107, 0.2f, 0.08f, 10);
            return c;
        }
    }
}
