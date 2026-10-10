using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using WAR2D.Art.Render;
using WAR2D.World;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// Renders samples of the lit look (owner, 2026-10-11: "Factorio combined with the current style",
    /// Charcoal palette) to <c>docs/art/render/</c>: the sprites close up, and an in-game scene on a real
    /// generated map at 1:1 (64 px per tile).
    /// </summary>
    public static class RenderSamplesMenu
    {
        public const string Folder = "docs/art/render";
        private const int Seed = 1, SceneW = 30, SceneH = 17;

        [MenuItem("WAR-2D/Art/Render lit samples")]
        public static void Render()
        {
            Directory.CreateDirectory(Folder);
            Color blue = (Color)(Color32)PlayerPalette.Of(0), red = (Color)(Color32)PlayerPalette.Of(1);

            // Close-ups on a floor strip.
            const int T = RenderGen.TileSize, gap = 12;
            var items = new List<(LitCanvas canvas, Color team)>();
            for (int d = 0; d < 8; d++) items.Add((RenderGen.Tank(d * Mathf.PI / 4, 0), blue));
            items.Add((RenderGen.Tank(0, 2), red));
            items.Add((RenderGen.Miner(0), blue));
            items.Add((RenderGen.Spawner(0), blue));
            items.Add((RenderGen.Base(), red));
            int w = gap, h = 3 * T + 2 * gap;
            foreach (var item in items) w += item.canvas.Width + gap;
            var strip = new Color32[w * h];
            Tile(strip, w, h, (x, y) => TileAt(TileType.Ground, 0, (x + y) & 3));
            int ox = gap;
            foreach (var (canvas, team) in items)
            {
                Over(strip, w, h, canvas.Render(team), canvas.Width, canvas.Height, ox, gap + (3 * T - canvas.Height) / 2);
                ox += canvas.Width + gap;
            }
            Save(Path.Combine(Folder, "sprites.png"), strip, w, h, 2);

            // Terrain close-ups: floor, rock masks, gem.
            var tiles = new List<Color32[]> { TileAt(TileType.Ground, 0, 0), TileAt(TileType.Ground, 0, 1) };
            foreach (int mask in new[] { 15, 14, 6, 0 }) tiles.Add(TileAt(TileType.Wall, mask, 0));
            tiles.Add(TileAt(TileType.Gem, 15, 0));
            tiles.Add(TileAt(TileType.Gem, 14, 1));
            int tw = tiles.Count * (T + gap) + gap, th = T + 2 * gap;
            var terrain = new Color32[tw * th];
            for (int i = 0; i < terrain.Length; i++) terrain[i] = new Color32(0x26, 0x30, 0x3A, 255);
            for (int k = 0; k < tiles.Count; k++) Over(terrain, tw, th, tiles[k], T, T, gap + k * (T + gap), gap);
            Save(Path.Combine(Folder, "terrain.png"), terrain, tw, th, 3);

            Save(Path.Combine(Folder, "scene.png"), Scene(blue, red, out int sw, out int sh), sw, sh, 1);
            Debug.Log($"[Art] rendered lit samples to {Folder}");
        }

        private static readonly Dictionary<(TileType, int, int), Color32[]> TileCache = new Dictionary<(TileType, int, int), Color32[]>();

        private static Color32[] TileAt(TileType kind, int mask, int variant)
        {
            if (kind == TileType.Border) kind = TileType.Wall;
            var key = (kind, kind == TileType.Ground ? 0 : mask, variant);
            if (TileCache.TryGetValue(key, out Color32[] px)) return px;
            LitCanvas c = kind == TileType.Ground ? RenderGen.Floor(Seed, variant)
                : kind == TileType.Gem ? RenderGen.Gem(Seed, variant, mask) : RenderGen.Rock(Seed, mask);
            px = c.Render(Color.white, shadow: false);
            TileCache[key] = px;
            return px;
        }

        private static Color32[] Scene(Color blue, Color red, out int w, out int h)
        {
            const int T = RenderGen.TileSize;
            TileCache.Clear();
            using MapStore map = MapStore.Generate(256, 5u, 0.15f);
            int2 site = map.HqSites[2];
            int x0 = site.x - 8, y0 = site.y - SceneH / 2;
            int W = SceneW * T, H = SceneH * T;
            w = W;
            h = H;
            var px = new Color32[W * H];
            bool Rock(int tx, int ty) => map.Grid.TileAt(new int2(tx, ty)) != TileType.Ground;
            for (int ty = 0; ty < SceneH; ty++)
            for (int tx = 0; tx < SceneW; tx++)
            {
                int mx = x0 + tx, my = y0 + ty;
                TileType kind = map.Grid.TileAt(new int2(mx, my));
                int variant = ((mx * 73856093) ^ (my * 19349663)) & 3;
                int mask = (Rock(mx, my + 1) ? RenderGen.North : 0) | (Rock(mx + 1, my) ? RenderGen.East : 0)
                         | (Rock(mx, my - 1) ? RenderGen.South : 0) | (Rock(mx - 1, my) ? RenderGen.West : 0);
                Over(px, W, H, TileAt(kind, mask, variant), T, T, tx * T, ty * T);
            }

            float2 hq = (float2)site + 0.5f - new float2(x0, y0);
            void Put(LitCanvas sprite, Color team, float2 tile)
            {
                Color32[] img = sprite.Render(team);
                Over(px, W, H, img, sprite.Width, sprite.Height, (int)(tile.x * T) - sprite.Width / 2, (int)(tile.y * T) - sprite.Height / 2);
            }
            Put(RenderGen.Base(), blue, hq);
            Put(RenderGen.Spawner(0), blue, hq + new float2(4f, -0.5f));
            int miners = 0;
            for (int ty = 1; ty < SceneH - 1 && miners < 2; ty++)
            for (int tx = 1; tx < SceneW - 1 && miners < 2; tx++)
            {
                int mx = x0 + tx, my = y0 + ty;
                if (Rock(mx, my) || map.Grid.TileAt(new int2(mx + 1, my)) != TileType.Gem) continue;
                Put(RenderGen.Miner(miners), blue, new float2(tx + 0.5f, ty + 0.5f));
                miners++;
            }
            var rng = new System.Random(4);
            var used = new HashSet<int2>();
            int blueCount = 0, redCount = 0;
            for (int i = 0; i < 3000 && (blueCount < 18 || redCount < 16); i++)
            {
                int tx = rng.Next(SceneW), ty = rng.Next(SceneH);
                if (Rock(x0 + tx, y0 + ty) || math.distance(new float2(tx, ty), hq) < 3.5f || !used.Add(new int2(tx, ty))) continue;
                if (math.distance(new float2(tx, ty), hq + new float2(4, -0.5f)) < 2f) continue;
                bool isBlue = tx < 16;
                if (isBlue ? blueCount >= 18 : redCount >= 16) continue;
                float facing = isBlue ? rng.Next(-1, 2) * Mathf.PI / 4 : Mathf.PI + rng.Next(-1, 2) * Mathf.PI / 4;
                Put(RenderGen.Tank(facing, rng.Next(6) == 0 ? 2 : rng.Next(2)), isBlue ? blue : red, new float2(tx + 0.5f, ty + 0.5f));
                if (isBlue) blueCount++; else redCount++;
            }
            return px;
        }

        private static void Tile(Color32[] dst, int w, int h, Func<int, int, Color32[]> tile)
        {
            const int T = RenderGen.TileSize;
            for (int ty = 0; ty * T < h; ty++)
            for (int tx = 0; tx * T < w; tx++)
                Over(dst, w, h, tile(tx, ty), T, T, tx * T, ty * T);
        }

        /// <summary>Alpha-blends <paramref name="src"/> over <paramref name="dst"/> at (ox, oy).</summary>
        private static void Over(Color32[] dst, int w, int h, Color32[] src, int sw, int sh, int ox, int oy)
        {
            for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int dx = ox + x, dy = oy + y;
                if ((uint)dx >= (uint)w || (uint)dy >= (uint)h) continue;
                Color32 s = src[y * sw + x];
                if (s.a == 0) continue;
                int i = dy * w + dx;
                Color32 d = dst[i];
                float a = s.a / 255f;
                dst[i] = new Color32((byte)(s.r * a + d.r * (1 - a)), (byte)(s.g * a + d.g * (1 - a)), (byte)(s.b * a + d.b * (1 - a)), 255);
            }
        }

        private static void Save(string path, Color32[] px, int w, int h, int scale)
        {
            var big = new Color32[w * scale * h * scale];
            for (int y = 0; y < h * scale; y++)
            for (int x = 0; x < w * scale; x++)
                big[y * w * scale + x] = px[y / scale * w + x / scale];
            var texture = new Texture2D(w * scale, h * scale, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(big);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
