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
        public const string LitFolder = "docs/art/render", PixelFolder = "docs/art/pixel", PixelHdFolder = "docs/art/pixel-hd";

        /// <summary>The finishes the owner compares: pixel art at 32 px per tile, pixel art at 64, and smooth (lit, anti-aliased) at 64.</summary>
        public enum Finish { Pixel32, Pixel64, Smooth64 }
        private static Finish mode;
        private const int Seed = 1, SceneW = 30, SceneH = 17;

        private static bool pixel => mode != Finish.Smooth64;
        private static int Factor => mode == Finish.Pixel32 ? 2 : 1;
        private static string Folder => mode == Finish.Pixel32 ? PixelFolder : mode == Finish.Pixel64 ? PixelHdFolder : LitFolder;
        /// <summary>Output pixels per tile.</summary>
        private static int T => RenderGen.TileSize / Factor;
        /// <summary>Close-up, terrain and scene scales for the page.</summary>
        private static int CloseScale => mode == Finish.Pixel32 ? 4 : 2;

        [MenuItem("WAR-2D/Art/Render lit samples")]
        public static void Render() => RenderAll(Finish.Smooth64);

        [MenuItem("WAR-2D/Art/Render pixel samples")]
        public static void RenderPixel() => RenderAll(Finish.Pixel32);

        [MenuItem("WAR-2D/Art/Render fine pixel samples")]
        public static void RenderFinePixel() => RenderAll(Finish.Pixel64);

        [MenuItem("WAR-2D/Art/Render all sample finishes")]
        public static void RenderEveryFinish()
        {
            foreach (Finish f in Enum.GetValues(typeof(Finish))) RenderAll(f);
        }

        /// <summary>A sprite or tile, finished for the current mode; its output size in <paramref name="w"/> × <paramref name="h"/>.</summary>
        private static Color32[] Img(LitCanvas c, Color team, bool sprite, out int w, out int h, float lift = 0f)
        {
            if (!pixel)
            {
                w = c.Width;
                h = c.Height;
                return c.Render(team, shadow: sprite, lift: lift);
            }
            w = c.Width / Factor;
            h = c.Height / Factor;
            Color32[] lit = c.Render(team, shadow: false, lift: lift);
            return PixelFinish.Finish(lit, c.Width, c.Height, Factor, PixelFinish.Palette(WAR2D.Art.Style.StylePalettes.Charcoal, team),
                sprite ? (Color32?)WAR2D.Art.Style.StylePalettes.Charcoal.Colours[WAR2D.Art.Style.StylePalette.Outline] : null);
        }

        /// <summary>Draws a sprite with its shadow: hard and offset in pixel mode (the lit mode bakes a soft one).</summary>
        private static void Sprite(Color32[] dst, int w, int h, LitCanvas c, Color team, int cx, int cy, float lift = 0f)
        {
            Color32[] img = Img(c, team, true, out int sw, out int sh, lift);
            int ox = cx - sw / 2, oy = cy - Mathf.RoundToInt(c.PivotY / Factor);
            int sd = 4 / Factor;
            if (pixel)
                for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    if (img[y * sw + x].a == 0) continue;
                    int dx = ox + x + sd, dy = oy + y - sd;
                    if ((uint)dx >= (uint)w || (uint)dy >= (uint)h) continue;
                    Color32 d = dst[dy * w + dx];
                    dst[dy * w + dx] = new Color32((byte)(d.r * 0.55f), (byte)(d.g * 0.55f), (byte)(d.b * 0.55f), 255);
                }
            Over(dst, w, h, img, sw, sh, ox, oy);
        }

        private static void RenderAll(Finish finish)
        {
            mode = finish;
            TileCache.Clear();
            Directory.CreateDirectory(Folder);
            Color blue = (Color)(Color32)PlayerPalette.Of(0), red = (Color)(Color32)PlayerPalette.Of(1);

            // Close-ups on a floor strip.
            int gap = pixel ? 6 : 12;
            var items = new List<(LitCanvas canvas, Color team, float lift)>();
            for (int d = 0; d < 8; d++) items.Add((RenderGen.Tank(d * Mathf.PI / 4, 0), blue, 0f));
            items.Add((RenderGen.Tank(0, 2), red, 0f));
            items.Add((RenderGen.Miner(0), blue, RenderGen.Lift));
            items.Add((RenderGen.Spawner(0), blue, RenderGen.Lift));
            items.Add((RenderGen.Base(), red, RenderGen.Lift));
            int w = gap, h = RenderGen.HqTiles * T + RenderGen.Headroom * 2 / Factor + 2 * gap;
            foreach (var item in items) w += item.canvas.Width * T / RenderGen.TileSize + gap;
            var strip = new Color32[w * h];
            Tile(strip, w, h, (x, y) => TileAt(TileType.Ground, 0, (x + y) & 3));
            int ox = gap;
            foreach (var (canvas, team, lift) in items)
            {
                int cw = canvas.Width * T / RenderGen.TileSize;
                Sprite(strip, w, h, canvas, team, ox + cw / 2, gap + RenderGen.HqTiles * T / 2, lift);
                ox += cw + gap;
            }
            Save(Path.Combine(Folder, "sprites.png"), strip, w, h, CloseScale);

            // Terrain close-ups: floor, rock masks, gem.
            var tiles = new List<Color32[]> { TileAt(TileType.Ground, 0, 0), TileAt(TileType.Ground, 0, 1) };
            foreach (int mask in new[] { 15, 14, 6, 0 }) tiles.Add(TileAt(TileType.Wall, mask, 0));
            tiles.Add(TileAt(TileType.Gem, 15, 0));
            tiles.Add(TileAt(TileType.Gem, 14, 1));
            int tw = tiles.Count * (T + gap) + gap, th = T + 2 * gap;
            var terrain = new Color32[tw * th];
            for (int i = 0; i < terrain.Length; i++) terrain[i] = new Color32(0x26, 0x30, 0x3A, 255);
            for (int k = 0; k < tiles.Count; k++) Over(terrain, tw, th, tiles[k], T, T, gap + k * (T + gap), gap);
            Save(Path.Combine(Folder, "terrain.png"), terrain, tw, th, CloseScale + CloseScale / 2);

            Save(Path.Combine(Folder, "scene.png"), Scene(blue, red, out int sw, out int sh), sw, sh, CloseScale / 2);
            Debug.Log($"[Art] rendered lit samples to {Folder}");
        }

        public const string ArsenalFolder = "docs/art/v0.12";

        /// <summary>
        /// The v0.12 Arsenal art for the owner's review, in the game's finish (pixel art at 64 px per tile):
        /// every new unit (and the Builder, redrawn as a bulldozer) in its 8 facings plus its action frames (with the Tank for scale), the new producers
        /// beside the Barracks and Radar, and an in-game scene with mixed armies, to <c>docs/art/v0.12/</c>.
        /// </summary>
        [MenuItem("WAR-2D/Art/Render v0.12 samples")]
        public static void RenderArsenal()
        {
            mode = Finish.Pixel64;
            TileCache.Clear();
            Directory.CreateDirectory(ArsenalFolder);
            Color blue = (Color)(Color32)PlayerPalette.Of(0), red = (Color)(Color32)PlayerPalette.Of(1);
            const int gap = 8;

            var units = new (string name, Func<float, int, LitCanvas> make)[]
            {
                ("Tank", RenderGen.Tank), ("Builder", RenderGen.Builder), ("Scout", RenderGen.Scout), ("Flamethrower", RenderGen.Flamethrower),
                ("Sniper", RenderGen.Sniper), ("SuicideBomber", RenderGen.SuicideBomber),
                ("Heavy", RenderGen.Heavy), ("Artillery", RenderGen.Artillery),
            };
            foreach (var (name, make) in units)
            {
                // One row: the 8 move facings, then both action frames facing east and north-east, in both team colours.
                var row = new List<(LitCanvas, Color)>();
                for (int d = 0; d < 8; d++) row.Add((make(d * Mathf.PI / 4, 0), blue));
                row.Add((make(0, 1), blue));
                row.Add((make(0, 2), blue));
                row.Add((make(0, 3), blue));
                row.Add((make(Mathf.PI / 4, 2), red));
                Strip(Path.Combine(ArsenalFolder, $"unit-{name}.png"), row, gap, 2);
            }

            var buildings = new List<(LitCanvas, Color)>
            {
                (RenderGen.Spawner(0), blue), (RenderGen.HeavyFactory(0), blue), (RenderGen.SiegeWorkshop(0), blue),
                (RenderGen.HeavyFactory(0), red), (RenderGen.SiegeWorkshop(1), red), (RenderGen.Radar(0), blue),
            };
            Strip(Path.Combine(ArsenalFolder, "buildings.png"), buildings, gap, 2, RenderGen.Lift);

            Save(Path.Combine(ArsenalFolder, "scene.png"), ArsenalScene(blue, red, out int sw, out int sh), sw, sh, 1);

            // Effects: each one's frames in order, on the floor, at 2×.
            static List<EffectFrame> Frames(int n, Func<int, float, EffectFrame> make)
            {
                var list = new List<EffectFrame>();
                for (int i = 0; i < n; i++) list.Add(make(i, n > 1 ? (float)i / (n - 1) : 0f));
                return list;
            }
            var effects = new (string name, List<EffectFrame> frames)[]
            {
                ("SniperTracer", Frames(1, (i, t) => EffectGen.SniperTracer())),
                ("ArtilleryShell", Frames(2, (i, t) => EffectGen.ArtilleryShell(i))),
                ("FlameStream", Frames(4, (i, t) => EffectGen.FlameStream(i))),
                ("ShellImpact", Frames(6, (i, t) => EffectGen.ShellImpact(t, 37))),
                ("BomberBlast", Frames(8, (i, t) => EffectGen.Blast(4, t, 29))),
            };
            foreach (var (name, frames) in effects)
            {
                int w = gap, h = 0;
                foreach (EffectFrame f in frames) { w += f.Width + gap; h = Math.Max(h, f.Height); }
                h += 2 * gap;
                var px = new Color32[w * h];
                Tile(px, w, h, (x, y) => TileAt(TileType.Ground, 0, (x + y) & 3));
                int ox = gap;
                foreach (EffectFrame f in frames)
                {
                    Over(px, w, h, f.Pixels, f.Width, f.Height, ox, gap + (h - 2 * gap - f.Height) / 2);
                    ox += f.Width + gap;
                }
                Save(Path.Combine(ArsenalFolder, $"fx-{name}.png"), px, w, h, 2);
            }

            // Sounds: the new sets and two old ones to compare against, as WAVs beside the page.
            string sfx = Path.Combine(ArsenalFolder, "sfx");
            Directory.CreateDirectory(sfx);
            foreach (string id in new[] { "shot_tank", "bomb_blast", "shot_scout", "shot_flamethrower", "shot_sniper", "shot_heavy", "shot_artillery", "shell_impact", "bomber_blast" })
            {
                var p = JsonUtility.FromJson<WAR2D.Audio.SfxrParams>(File.ReadAllText($"{AudioBuild.SfxSource}/{id}.json"));
                File.WriteAllBytes(Path.Combine(sfx, id + ".wav"), WAR2D.Audio.Wav.Encode(WAR2D.Audio.Sfxr.Render(p, p.seed, AudioBuild.SampleRate), AudioBuild.SampleRate));
            }
            Debug.Log($"[Art] rendered v0.12 samples to {ArsenalFolder}");
        }

        /// <summary>Sprites side by side on a floor strip, centred on one baseline, saved at <paramref name="scale"/>×.</summary>
        private static void Strip(string path, List<(LitCanvas canvas, Color team)> items, int gap, int scale, float lift = 0f)
        {
            int w = gap, h = 0;
            foreach (var (canvas, _) in items)
            {
                w += canvas.Width / Factor + gap;
                h = Math.Max(h, canvas.Height / Factor);
            }
            h += 2 * gap;
            var px = new Color32[w * h];
            Tile(px, w, h, (x, y) => TileAt(TileType.Ground, 0, (x + y) & 3));
            int ox = gap;
            foreach (var (canvas, team) in items)
            {
                int cw = canvas.Width / Factor;
                Sprite(px, w, h, canvas, team, ox + cw / 2, gap + Mathf.RoundToInt(canvas.PivotY / Factor), lift);
                ox += cw + gap;
            }
            Save(path, px, w, h, scale);
        }

        /// <summary>The v0.7 scene's map, with the HQ, the three producers and two mixed armies facing each other.</summary>
        private static Color32[] ArsenalScene(Color blue, Color red, out int w, out int h)
        {
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
            void Put(LitCanvas sprite, Color team, float2 tile, float lift = 0f) => Sprite(px, W, H, sprite, team, (int)(tile.x * T), (int)(tile.y * T), lift);
            Put(RenderGen.Spawner(0), blue, hq + new float2(5f, -2.5f), RenderGen.Lift);
            Put(RenderGen.SiegeWorkshop(0), blue, hq + new float2(5f, 2.5f), RenderGen.Lift);
            Put(RenderGen.HeavyFactory(0), blue, hq + new float2(-0.5f, -5.5f), RenderGen.Lift);
            Put(RenderGen.Base(), blue, hq, RenderGen.Lift);

            // Weighted like the spec's perf mix; large units take a 2×2 spot.
            var mix = new (Func<float, int, LitCanvas> make, int weight, bool large)[]
            {
                (RenderGen.Tank, 8, false), (RenderGen.Builder, 2, false), (RenderGen.Scout, 3, false), (RenderGen.Flamethrower, 2, false), (RenderGen.Sniper, 2, false),
                (RenderGen.SuicideBomber, 2, false), (RenderGen.Heavy, 2, true), (RenderGen.Artillery, 2, true),
            };
            int total = 0;
            foreach (var m in mix) total += m.weight;
            var rng = new System.Random(4);
            var used = new HashSet<int2>();
            int blueCount = 0, redCount = 0;
            bool Free(int tx, int ty, int size)
            {
                for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                    if (tx + dx >= SceneW || ty + dy >= SceneH || Rock(x0 + tx + dx, y0 + ty + dy) || used.Contains(new int2(tx + dx, ty + dy))) return false;
                return true;
            }
            for (int i = 0; i < 4000 && (blueCount < 18 || redCount < 16); i++)
            {
                int tx = rng.Next(SceneW), ty = rng.Next(SceneH);
                if (math.distance(new float2(tx, ty), hq) < 4.5f) continue;
                if (math.distance(new float2(tx, ty), hq + new float2(5, -2.5f)) < 2.5f || math.distance(new float2(tx, ty), hq + new float2(5, 2.5f)) < 2.5f) continue;
                if (math.distance(new float2(tx, ty), hq + new float2(-0.5f, -5.5f)) < 3f) continue;
                bool isBlue = tx < 16;
                if (isBlue ? blueCount >= 18 : redCount >= 16) continue;
                int pick = rng.Next(total), k = 0;
                while (pick >= mix[k].weight) pick -= mix[k++].weight;
                int size = mix[k].large ? 2 : 1;
                if (!Free(tx, ty, size)) continue;
                for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                    used.Add(new int2(tx + dx, ty + dy));
                float facing = isBlue ? rng.Next(-1, 2) * Mathf.PI / 4 : Mathf.PI + rng.Next(-1, 2) * Mathf.PI / 4;
                Put(mix[k].make(facing, rng.Next(6) == 0 ? 2 : rng.Next(2)), isBlue ? blue : red, new float2(tx + size / 2f, ty + size / 2f));
                if (isBlue) blueCount++; else redCount++;
            }
            return px;
        }

        private static readonly Dictionary<(TileType, int, int), Color32[]> TileCache = new Dictionary<(TileType, int, int), Color32[]>();

        private static Color32[] TileAt(TileType kind, int mask, int variant)
        {
            if (kind == TileType.Border) kind = TileType.Wall;
            var key = (kind, kind == TileType.Ground ? 0 : mask, variant);
            if (TileCache.TryGetValue(key, out Color32[] px)) return px;
            LitCanvas c = kind == TileType.Ground ? RenderGen.Floor(Seed, variant)
                : kind == TileType.Gem ? RenderGen.Gem(Seed, variant, mask) : RenderGen.Rock(Seed, mask);
            px = Img(c, Color.white, false, out _, out _);
            TileCache[key] = px;
            return px;
        }

        private static Color32[] Scene(Color blue, Color red, out int w, out int h)
        {
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
            void Put(LitCanvas sprite, Color team, float2 tile, float lift = 0f) => Sprite(px, W, H, sprite, team, (int)(tile.x * T), (int)(tile.y * T), lift);
            Put(RenderGen.Spawner(0), blue, hq + new float2(5f, -2.5f), RenderGen.Lift);
            Put(RenderGen.Base(), blue, hq, RenderGen.Lift);
            int miners = 0;
            for (int ty = 1; ty < SceneH - 1 && miners < 2; ty++)
            for (int tx = 1; tx < SceneW - 1 && miners < 2; tx++)
            {
                int mx = x0 + tx, my = y0 + ty;
                if (Rock(mx, my) || map.Grid.TileAt(new int2(mx + 1, my)) != TileType.Gem) continue;
                Put(RenderGen.Miner(miners), blue, new float2(tx + 0.5f, ty + 0.5f), RenderGen.Lift);
                miners++;
            }
            var rng = new System.Random(4);
            var used = new HashSet<int2>();
            int blueCount = 0, redCount = 0;
            for (int i = 0; i < 3000 && (blueCount < 18 || redCount < 16); i++)
            {
                int tx = rng.Next(SceneW), ty = rng.Next(SceneH);
                if (Rock(x0 + tx, y0 + ty) || math.distance(new float2(tx, ty), hq) < 4.5f || !used.Add(new int2(tx, ty))) continue;
                if (math.distance(new float2(tx, ty), hq + new float2(5, -2.5f)) < 2.5f) continue;
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
