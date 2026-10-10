using System;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>
    /// A palette-indexed pixel canvas for code-drawn art. Index 0 is transparent. Rows run bottom-up, as in
    /// Unity textures, so <see cref="ToPixels"/> feeds <c>Texture2D.SetPixels32</c> directly. Writes outside
    /// the canvas are ignored and reads outside it are transparent, so drawing code needn't clip.
    /// </summary>
    public sealed class PixelCanvas
    {
        private readonly byte[] pixels;

        public int Width { get; }
        public int Height { get; }
        public Color32[] Palette { get; }

        public PixelCanvas(int width, int height, Color32[] palette)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (palette == null || palette.Length == 0 || palette.Length > 256) throw new ArgumentException("1–256 palette colours", nameof(palette));
            Width = width;
            Height = height;
            Palette = palette;
            pixels = new byte[width * height];
        }

        public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

        public void Set(int x, int y, byte index)
        {
            if (Contains(x, y) && index < Palette.Length) pixels[y * Width + x] = index;
        }

        public byte Get(int x, int y) => Contains(x, y) ? pixels[y * Width + x] : (byte)0;

        /// <summary>A filled rectangle from (x, y), <paramref name="w"/> by <paramref name="h"/>.</summary>
        public void Rect(int x, int y, int w, int h, byte index)
        {
            for (int j = y; j < y + h; j++)
            for (int i = x; i < x + w; i++)
                Set(i, j, index);
        }

        /// <summary>A one-pixel line (Bresenham), both ends included.</summary>
        public void Line(int x0, int y0, int x1, int y1, byte index)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Set(x0, y0, index);
                if (x0 == x1 && y0 == y1) return;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>A filled disc of pixels whose centres lie within <paramref name="r"/> of (cx, cy).</summary>
        public void Disc(float cx, float cy, float r, byte index)
        {
            for (int y = (int)Math.Floor(cy - r); y <= (int)Math.Ceiling(cy + r); y++)
            for (int x = (int)Math.Floor(cx - r); x <= (int)Math.Ceiling(cx + r); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                if (dx * dx + dy * dy <= r * r) Set(x, y, index);
            }
        }

        /// <summary>Writes <paramref name="index"/> on every transparent pixel 4-adjacent to an opaque one (as the canvas was before the call).</summary>
        public void Outline(byte index)
        {
            var before = (byte[])pixels.Clone();
            byte At(int x, int y) => Contains(x, y) ? before[y * Width + x] : (byte)0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (before[y * Width + x] != 0) continue;
                if (At(x - 1, y) != 0 || At(x + 1, y) != 0 || At(x, y - 1) != 0 || At(x, y + 1) != 0) pixels[y * Width + x] = index;
            }
        }

        /// <summary>A checkerboard of <paramref name="a"/> and <paramref name="b"/> over the rectangle.</summary>
        public void Dither(RectInt r, byte a, byte b)
        {
            for (int y = r.yMin; y < r.yMax; y++)
            for (int x = r.xMin; x < r.xMax; x++)
                Set(x, y, ((x + y) & 1) == 0 ? a : b);
        }

        /// <summary>Copies another canvas onto this one at (x, y); its transparent pixels are skipped.</summary>
        public void Blit(PixelCanvas source, int x, int y)
        {
            for (int j = 0; j < source.Height; j++)
            for (int i = 0; i < source.Width; i++)
            {
                byte p = source.Get(i, j);
                if (p != 0) Set(x + i, y + j, p);
            }
        }

        /// <summary>The palette indices, bottom-up rows (a copy).</summary>
        public byte[] Indices() => (byte[])pixels.Clone();

        /// <summary>The colours, bottom-up rows; index 0 becomes the palette's first (transparent) colour.</summary>
        public Color32[] ToPixels()
        {
            var colours = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) colours[i] = Palette[pixels[i]];
            return colours;
        }

        /// <summary>
        /// Lays equal-size frames out left to right, <paramref name="columns"/> per row, the first row at the
        /// top of the image (bottom-up pixel rows, as <see cref="ToPixels"/>).
        /// </summary>
        public static Color32[] Sheet(PixelCanvas[] frames, int columns, out int width, out int height)
        {
            if (frames == null || frames.Length == 0) throw new ArgumentException("no frames", nameof(frames));
            int fw = frames[0].Width, fh = frames[0].Height;
            int rows = (frames.Length + columns - 1) / columns;
            width = fw * Math.Min(columns, frames.Length);
            height = fh * rows;
            var sheet = new Color32[width * height];
            for (int f = 0; f < frames.Length; f++)
            {
                if (frames[f].Width != fw || frames[f].Height != fh) throw new ArgumentException("frames differ in size", nameof(frames));
                int ox = f % columns * fw, oy = height - (f / columns + 1) * fh;
                Color32[] px = frames[f].ToPixels();
                for (int y = 0; y < fh; y++) Array.Copy(px, y * fw, sheet, (oy + y) * width + ox, fw);
            }
            return sheet;
        }
    }
}
