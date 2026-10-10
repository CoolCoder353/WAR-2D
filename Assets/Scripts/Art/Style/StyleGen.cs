using System;
using WAR2D.Art.Generators;
using static WAR2D.Art.Style.StylePalette;

namespace WAR2D.Art.Style
{
    /// <summary>
    /// Sprites in the current placeholder art's style (owner request, 2026-10-11) at 32 px per tile: dark
    /// rounded machines with panel lines, an outline, a bright light strip and team-coloured parts; cracked
    /// stone floor; rock shaded darker toward open floor; gem crystals. Drawn with <see cref="StylePalette"/>
    /// role indices, so any palette restyles them. Deterministic per seed.
    /// </summary>
    public static class StyleGen
    {
        /// <summary>Pixels per tile in this style.</summary>
        public const int TileSize = 32;

        public const int North = 1, East = 2, South = 4, West = 8;

        private static PixelCanvas Canvas(int tilesW, int tilesH) =>
            new PixelCanvas(tilesW * TileSize, tilesH * TileSize, StylePalettes.Charcoal.Colours);

        // ---------- Units ----------

        /// <summary>The Tank facing <paramref name="angle"/> (radians, 0 = east, counter-clockwise). Frames: 0–1 move (treads), 2–3 fire.</summary>
        public static PixelCanvas Tank(double angle, int frame)
        {
            PixelCanvas c = Canvas(1, 1);
            int tread = frame == 1 ? 2 : 0;
            double recoil = frame == 2 ? 2 : 0;
            Rotated(c, angle, (lx, ly) => TankAt(lx, ly, tread, recoil));
            c.Outline(Outline);
            if (frame >= 2) Flash(c, angle, frame == 2 ? 15.5 : 16.2, frame == 2);
            return c;
        }

        private static byte TankAt(double lx, double ly, int tread, double recoil)
        {
            double ay = Math.Abs(ly);
            // Barrel.
            if (lx > 3 - recoil && lx < 14.5 - recoil && ay < 1.6) return lx > 12.5 - recoil ? Body2 : ay < 0.6 ? Body3 : Body2;
            // Turret: rounded block in team colour, lit on its left (+y) side.
            if (RoundRect(lx + 1.5, ly, 6.5, 5.5, 2.5))
            {
                if (RoundRect(lx + 1.5, ly, 5.0, 4.0, 1.5)) return ly > 2.0 ? Team3 : ly < -2.0 ? Team1 : Team2;
                return Team0;
            }
            // Deck panel with a lit rim, inside the hull.
            if (RoundRect(lx, ly, 10.5, 7.5, 2.5))
            {
                if (!RoundRect(lx, ly, 9.5, 6.5, 2.0)) return Body3;
                if (lx < -8.0 && ay < 4.5) return Lamp; // rear light strip
                return Body2;
            }
            // Hull.
            if (RoundRect(lx, ly, 12.5, 9.0, 3.0)) return Body1;
            // Treads with notches that move with the frame.
            if (Math.Abs(lx) < 13.5 && ay >= 9.0 && ay < 12.5)
                return ((int)Math.Floor(lx + 20 + tread) & 3) == 0 ? Outline : ay > 11.5 ? Body0 : Body1;
            return Clear;
        }

        // ---------- Buildings ----------

        /// <summary>The Miner (1×1), facing east; frames turn its four wheels' hubs.</summary>
        public static PixelCanvas Miner(int frame)
        {
            PixelCanvas c = Canvas(1, 1);
            // Diagonal arms to the four wheels.
            for (int k = -1; k <= 1; k++)
            {
                c.Line(9 + k, 9, 22 + k, 22, Body1);
                c.Line(9 + k, 22, 22 + k, 9, Body1);
            }
            foreach (var (wx, wy) in new[] { (6.5f, 6.5f), (25.5f, 6.5f), (6.5f, 25.5f), (25.5f, 25.5f) })
            {
                c.Disc(wx, wy, 5.6f, Body1);
                c.Disc(wx, wy, 4.2f, Body2);
                c.Disc(wx, wy, 1.8f, Body0);
                double a = frame * Math.PI / 2;
                c.Set((int)Math.Floor(wx + Math.Cos(a) * 3.2), (int)Math.Floor(wy + Math.Sin(a) * 3.2), Body3);
            }
            // Body block with a lit rim, a light strip toward the drill side and a team stripe behind it.
            Fill(c, (x, y) => RoundRect(x - 15.5, y - 15.5, 8.5, 8.5, 2.0), Body3);
            Fill(c, (x, y) => RoundRect(x - 15.5, y - 15.5, 7.5, 7.5, 1.5), Body1);
            c.Rect(20, 11, 2, 10, Lamp);
            c.Rect(10, 11, 2, 10, Team2);
            c.Rect(10, 19, 2, 2, Team3);
            c.Outline(Outline);
            return c;
        }

        /// <summary>A production building (Small Unit Spawner, 2×2): a panelled block with a lit slot; frame 1 dims the slot.</summary>
        public static PixelCanvas Spawner(int frame) => Building(2, frame, 6, false);

        /// <summary>The HQ (3×3): a larger panelled block with a team-coloured core and corner bolts.</summary>
        public static PixelCanvas Base() => Building(3, 0, 10, true);

        private static PixelCanvas Building(int tiles, int frame, int slotHalfHeight, bool hq)
        {
            PixelCanvas c = Canvas(tiles, tiles);
            int s = c.Width;
            double h = s / 2.0;
            Fill(c, (x, y) => RoundRect(x - h + 0.5, y - h + 0.5, h - 1, h - 1, 5), Body2);
            Fill(c, (x, y) => RoundRect(x - h + 0.5, y - h + 0.5, h - 3, h - 3, 4), Body1);
            c.Rect(5, s - 4, s - 10, 1, Body3); // lit top edge
            // Panel grooves: L shapes near each corner, and a frame around the slot.
            int g = tiles == 2 ? 9 : 13;
            Groove(c, g, g, 1, 1); Groove(c, s - 1 - g, g, -1, 1); Groove(c, g, s - 1 - g, 1, -1); Groove(c, s - 1 - g, s - 1 - g, -1, -1);
            int cx = s / 2, cy = s / 2;
            int slotW = hq ? 8 : 4, slotH = slotHalfHeight * (tiles == 2 ? 2 : 2);
            c.Rect(cx - slotW - 2, cy - slotH - 2, 2 * slotW + 4, 2 * slotH + 4, Body0);
            c.Rect(cx - slotW - 1, cy - slotH - 1, 2 * slotW + 2, 2 * slotH + 2, Body3);
            byte core = hq ? Team2 : frame == 0 ? Lamp : Body3;
            c.Rect(cx - slotW, cy - slotH, 2 * slotW, 2 * slotH, core);
            if (hq)
            {
                c.Rect(cx - slotW, cy + slotH - 3, 2 * slotW, 3, Team3);
                c.Rect(cx - slotW, cy - slotH, 2 * slotW, 3, Team1);
                foreach (var (bx, by) in new[] { (7, 7), (s - 8, 7), (7, s - 8), (s - 8, s - 8) })
                {
                    c.Disc(bx + 0.5f, by + 0.5f, 2.2f, Body3);
                    c.Set(bx, by, Body0);
                }
            }
            // A team bracket beside the slot.
            int bx0 = cx + slotW + 4;
            c.Rect(bx0, cy - slotH, 2, 2 * slotH, Team2);
            c.Rect(bx0 - 2, cy - slotH, 2, 2, Team2);
            c.Rect(bx0 - 2, cy + slotH - 2, 2, 2, Team3);
            c.Outline(Outline);
            return c;
        }

        private static void Groove(PixelCanvas c, int x, int y, int sx, int sy)
        {
            for (int i = 0; i < 8; i++) c.Set(x + sx * i, y, Outline);
            for (int i = 0; i < 8; i++) c.Set(x, y + sy * i, Outline);
        }

        // ---------- Terrain ----------

        /// <summary>Stone floor: a flat tone with short crack lines, light and dark.</summary>
        public static PixelCanvas Floor(int seed, int variant)
        {
            var rng = new ArtRandom(seed, "floor" + variant);
            PixelCanvas c = Canvas(1, 1);
            c.Rect(0, 0, TileSize, TileSize, Floor1);
            int cracks = 7 + rng.Range(4);
            for (int k = 0; k < cracks; k++)
            {
                int x = rng.Range(TileSize), y = rng.Range(TileSize), len = 3 + rng.Range(5);
                byte tone = rng.Chance(0.55f) ? Floor0 : Floor2;
                int dx = rng.Range(3) - 1, dy = rng.Range(3) - 1;
                if (dx == 0 && dy == 0) dx = 1;
                for (int i = 0; i < len; i++)
                {
                    c.Set((x + TileSize) % TileSize, (y + TileSize) % TileSize, tone); // wraps, so tiles join
                    x += dx;
                    y += dy;
                    if (rng.Chance(0.3f)) { dx = rng.Range(3) - 1; if (dx == 0 && dy == 0) dy = 1; }
                }
            }
            return c;
        }

        /// <summary>
        /// Rock for one 4-neighbour mask (bit set = that neighbour is rock too): flat in the middle and
        /// shaded darker in bands toward each side open to the floor, with rounded outside corners.
        /// </summary>
        public static PixelCanvas Rock(int seed, int mask)
        {
            PixelCanvas c = Canvas(1, 1);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                double d = EdgeDistance(x + 0.5, y + 0.5, mask);
                if (d < 0) { c.Set(x, y, Floor1); continue; } // outside a rounded corner
                byte tone = d < 2.5 ? Rock0 : d < 5.5 ? Rock1 : d < 9.5 ? Rock2 : Rock3;
                c.Set(x, y, tone);
            }
            return c;
        }

        /// <summary>Rock (shaped by the same 4-neighbour mask as <see cref="Rock"/>) with gem crystals set into it.</summary>
        public static PixelCanvas Gem(int seed, int variant, int mask = North | East | South | West)
        {
            PixelCanvas c = Rock(seed, mask);
            var rng = new ArtRandom(seed, "gem" + variant);
            int crystals = 3 + rng.Range(3);
            for (int k = 0; k < crystals; k++)
            {
                float cx = 8 + rng.Range(TileSize - 16), cy = 8 + rng.Range(TileSize - 16);
                float rx = 3f + rng.Range(3), ry = 2f + rng.Range(2);
                double tilt = rng.Range(180) * Math.PI / 180;
                Fill(c, (x, y) =>
                {
                    double px = x + 0.5 - cx, py = y + 0.5 - cy;
                    double u = px * Math.Cos(tilt) + py * Math.Sin(tilt), v = -px * Math.Sin(tilt) + py * Math.Cos(tilt);
                    return u * u / (rx * rx) + v * v / (ry * ry) <= 1;
                }, Gem1, (x, y) => x + 0.5 - cx < -0.3 && y + 0.5 - cy > 0.3 ? Gem2 : x + 0.5 - cx > 0.8 || y + 0.5 - cy < -0.8 ? Gem0 : Gem1);
                c.Set((int)Math.Floor(cx - 1), (int)Math.Floor(cy + 1), Gem3);
            }
            return c;
        }

        /// <summary>Pixels from (x, y) to the nearest side open to floor (or to an open outside corner's arc); large when enclosed; negative outside a rounded corner.</summary>
        private static double EdgeDistance(double x, double y, int mask)
        {
            double s = TileSize, d = 99;
            bool n = (mask & North) == 0, e = (mask & East) == 0, so = (mask & South) == 0, w = (mask & West) == 0;
            if (n) d = Math.Min(d, s - y);
            if (so) d = Math.Min(d, y);
            if (w) d = Math.Min(d, x);
            if (e) d = Math.Min(d, s - x);
            const double r = 8;
            // Outside corners where both sides are open: distance to a rounded corner of radius r.
            double Corner(double cx, double cy, bool inX, bool inY)
            {
                double ox = inX ? cx - x : x - cx, oy = inY ? cy - y : y - cy; // offsets past the arc's centre, toward the corner
                if (ox <= 0 || oy <= 0) return 99;
                return r - Math.Sqrt(ox * ox + oy * oy);
            }
            if (n && w) d = Math.Min(d, Corner(r, s - r, true, false));
            if (n && e) d = Math.Min(d, Corner(s - r, s - r, false, false));
            if (so && w) d = Math.Min(d, Corner(r, r, true, true));
            if (so && e) d = Math.Min(d, Corner(s - r, r, false, true));
            return d;
        }

        // ---------- Helpers ----------

        private static bool RoundRect(double x, double y, double hx, double hy, double r)
        {
            double qx = Math.Abs(x) - (hx - r), qy = Math.Abs(y) - (hy - r);
            if (qx <= 0 || qy <= 0) return Math.Abs(x) <= hx && Math.Abs(y) <= hy;
            return qx * qx + qy * qy <= r * r;
        }

        private static void Rotated(PixelCanvas c, double angle, Func<double, double, byte> sample)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle), centre = c.Width / 2.0;
            for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
            {
                double wx = x + 0.5 - centre, wy = y + 0.5 - centre;
                byte p = sample(wx * cos + wy * sin, -wx * sin + wy * cos);
                if (p != Clear) c.Set(x, y, p);
            }
        }

        private static void Fill(PixelCanvas c, Func<int, int, bool> inside, byte index, Func<int, int, byte> shade = null)
        {
            for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
                if (inside(x, y)) c.Set(x, y, shade != null ? shade(x, y) : index);
        }

        private static void Flash(PixelCanvas c, double angle, double tip, bool big)
        {
            double centre = c.Width / 2.0;
            float fx = (float)(centre + Math.Cos(angle) * tip), fy = (float)(centre + Math.Sin(angle) * tip);
            c.Disc(fx, fy, big ? 2.6f : 1.6f, Warn);
            if (big) c.Disc(fx, fy, 1.2f, Lamp);
        }
    }
}
