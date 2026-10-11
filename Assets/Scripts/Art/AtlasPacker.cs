using System;
using System.Collections.Generic;
using UnityEngine;

namespace WAR2D.Art
{
    /// <summary>Where <see cref="AtlasPacker.Pack"/> put each rect, and the atlas size (both powers of two).</summary>
    public readonly struct PackResult
    {
        public readonly int Width, Height;
        /// <summary>Bottom-left corner of each input rect, in input order.</summary>
        public readonly Vector2Int[] Positions;

        public PackResult(int width, int height, Vector2Int[] positions)
        {
            Width = width;
            Height = height;
            Positions = positions;
        }
    }

    /// <summary>
    /// Packs sprite frames into a power-of-two atlas with shelves (tallest first). Deterministic: the same
    /// rects in the same order always pack the same way.
    /// </summary>
    public static class AtlasPacker
    {
        /// <summary>The largest atlas edge it will make.</summary>
        public const int MaxSize = 8192;

        /// <summary>Packs <paramref name="rects"/> with <paramref name="padding"/> transparent pixels between frames and around the edge.</summary>
        public static PackResult Pack(IReadOnlyList<(string id, int frame, int w, int h)> rects, int padding = 1)
        {
            long area = 0;
            int widest = 0;
            foreach (var r in rects)
            {
                area += (long)(r.w + padding) * (r.h + padding);
                widest = Math.Max(widest, r.w + 2 * padding);
            }
            // Try sizes in order of area (square first, then wide), smallest that fits.
            var sizes = new List<(int w, int h)>();
            for (int w = 16; w <= MaxSize; w *= 2)
            for (int h = w / 2; h <= w; h *= 2)
                if (w >= widest && (long)w * h >= area) sizes.Add((w, h));
            sizes.Sort((a, b) => ((long)a.w * a.h).CompareTo((long)b.w * b.h) != 0 ? ((long)a.w * a.h).CompareTo((long)b.w * b.h) : a.w.CompareTo(b.w));
            foreach (var (w, h) in sizes)
                if (TryShelves(rects, padding, w, h, out Vector2Int[] positions)) return new PackResult(w, h, positions);
            throw new InvalidOperationException($"{rects.Count} frames don't fit in {MaxSize}×{MaxSize}");
        }

        private static bool TryShelves(IReadOnlyList<(string id, int frame, int w, int h)> rects, int padding, int width, int height, out Vector2Int[] positions)
        {
            var order = new int[rects.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // Tallest first; ties keep input order so the result is stable.
            Array.Sort(order, (a, b) => rects[a].h != rects[b].h ? rects[b].h.CompareTo(rects[a].h) : a.CompareTo(b));
            positions = new Vector2Int[rects.Count];
            int x = padding, y = padding, shelf = 0;
            foreach (int i in order)
            {
                var r = rects[i];
                if (x + r.w + padding > width)
                {
                    x = padding;
                    y += shelf + padding;
                    shelf = 0;
                }
                if (y + r.h + padding > height) return false;
                positions[i] = new Vector2Int(x, y);
                x += r.w + padding;
                shelf = Math.Max(shelf, r.h);
            }
            return true;
        }
    }
}
