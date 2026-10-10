using System;
using static WAR2D.Art.Generators.PrototypeGenerators;

namespace WAR2D.Art.Generators
{
    /// <summary>
    /// The Tank, top-down, in 8 facings (45° apart, 0 = east, counter-clockwise). Frames per facing: move 0,
    /// move 1 (treads shifted a pixel), fire 0 (barrel recoiled, muzzle flash), fire 1 (smaller flash).
    /// Each facing is drawn from the same shapes rotated in continuous space and sampled once per pixel, so
    /// every facing stays on the pixel grid. The hull and turret use the team key ramp.
    /// </summary>
    public sealed class TankGen : ISpriteGenerator
    {
        public string Id => nameof(UnitType.Tank);

        public PixelCanvas[] Generate(int seed)
        {
            ArtEntry entry = Entry(Id);
            var frames = new PixelCanvas[entry.Frames * entry.Directions];
            for (int d = 0; d < entry.Directions; d++)
            for (int f = 0; f < entry.Frames; f++)
                frames[d * entry.Frames + f] = Draw(entry, d * 2 * Math.PI / entry.Directions, f);
            return frames;
        }

        private static PixelCanvas Draw(ArtEntry entry, double angle, int frame)
        {
            PixelCanvas c = Canvas(entry);
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double centre = c.Width / 2.0;
            int tread = frame == 1 ? 1 : 0;
            bool firing = frame >= 2;
            double recoil = frame == 2 ? 1.0 : 0.0;

            for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
            {
                // Pixel centre in tank space (x forward, y left), one unit = one pixel.
                double wx = x + 0.5 - centre, wy = y + 0.5 - centre;
                double lx = wx * cos + wy * sin, ly = -wx * sin + wy * cos;
                byte p = Sample(lx, ly, tread, recoil);
                if (p != Clear) c.Set(x, y, p);
            }
            c.Outline(Outline);

            if (firing)
            {
                // Muzzle flash just past the barrel tip, in world space.
                double tip = frame == 2 ? 7.2 : 7.6;
                int fx = (int)Math.Floor(centre + cos * tip), fy = (int)Math.Floor(centre + sin * tip);
                c.Set(fx, fy, frame == 2 ? Yellow : Orange);
                if (frame == 2)
                {
                    c.Set(fx + (int)Math.Round(cos), fy + (int)Math.Round(sin), Orange);
                    c.Set(fx + (int)Math.Round(-sin), fy + (int)Math.Round(cos), Orange);
                    c.Set(fx - (int)Math.Round(-sin), fy - (int)Math.Round(cos), Orange);
                }
            }
            return c;
        }

        /// <summary>The colour at a point in tank space, or <see cref="PrototypeGenerators.Clear"/>.</summary>
        private static byte Sample(double lx, double ly, int tread, double recoil)
        {
            // Barrel (on top of everything).
            if (lx >= 1.0 - recoil && lx < 6.8 - recoil && Math.Abs(ly) < 0.75) return lx > 5.5 - recoil ? Steel1 : Steel2;
            // Turret.
            double tx = lx + 0.5, r2 = tx * tx + ly * ly;
            if (r2 < 2.9 * 2.9) return r2 < 1.2 * 1.2 ? Team3 : (ly > 0.8 ? Team3 : Team2);
            // Hull.
            if (Math.Abs(lx) < 4.8 && Math.Abs(ly) < 3.1) return Math.Abs(ly) > 2.2 || Math.Abs(lx) > 4.0 ? Team1 : Team2;
            // Treads, striped along their length; the stripe shifts a pixel per move frame.
            if (Math.Abs(lx) < 5.8 && Math.Abs(ly) >= 3.1 && Math.Abs(ly) < 5.6)
                return (((int)Math.Floor(lx + 6 + tread)) & 1) == 0 ? Steel0 : Steel1;
            return Clear;
        }
    }
}
