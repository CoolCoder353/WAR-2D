using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Style;
using WAR2D.World;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// Renders the style samples for the owner (plan Task 4–5, 2026-10-11): for every candidate palette, the
    /// sprites close up and an in-game scene, on one labelled page (<c>docs/art/style/index.html</c>).
    /// </summary>
    public static class StyleSamplesMenu
    {
        public const string Folder = "docs/art/style";
        private const int Seed = 1, CloseScale = 3, SceneScale = 2, SceneW = 30, SceneH = 17;

        [MenuItem("WAR-2D/Art/Render style samples")]
        public static void Render()
        {
            Directory.CreateDirectory(Folder);
            var html = new StringBuilder();
            html.Append(PageHead());
            foreach (StylePalette palette in StylePalettes.All)
            {
                string slug = palette.Name.ToLowerInvariant().Replace(' ', '-');
                WritePng(Path.Combine(Folder, slug + "-sprites.png"), Sprites(palette, out int sw, out int sh), sw, sh, CloseScale);
                WritePng(Path.Combine(Folder, slug + "-scene.png"), Scene(palette, out int cw, out int ch), cw, ch, SceneScale);
                html.Append(Section(palette, slug));
            }
            html.Append("</main></body></html>\n");
            File.WriteAllText(Path.Combine(Folder, "index.html"), html.ToString());
            Debug.Log($"[Art] rendered {StylePalettes.All.Length} style samples to {Folder}/index.html");
        }

        private static Color32 Player(int colourIndex) => PlayerPalette.Of(colourIndex);

        /// <summary>One row: the Tank in 8 facings and firing, the Miner, the Spawner, the HQ, floor, rock edges and gems.</summary>
        private static Color32[] Sprites(StylePalette palette, out int w, out int h)
        {
            const int T = StyleGen.TileSize, gap = 8;
            Color32[] blue = palette.WithTeam(Player(0)), red = palette.WithTeam(Player(1));
            var items = new List<(PixelCanvas canvas, Color32[] colours)>();
            for (int d = 0; d < 8; d++) items.Add((StyleGen.Tank(d * Math.PI / 4, 0), blue));
            items.Add((StyleGen.Tank(0, 2), red));
            items.Add((StyleGen.Miner(0), blue));
            items.Add((StyleGen.Spawner(0), blue));
            items.Add((StyleGen.Base(), red));
            for (int v = 0; v < 2; v++) items.Add((StyleGen.Floor(Seed, v), palette.Colours));
            foreach (int mask in new[] { 15, 14, 6, 0 }) items.Add((StyleGen.Rock(Seed, mask), palette.Colours));
            items.Add((StyleGen.Gem(Seed, 0), palette.Colours));

            w = gap;
            h = 3 * T + 2 * gap;
            foreach (var item in items) w += item.canvas.Width + gap;
            var px = Background(w, h);
            int x = gap;
            foreach (var (canvas, colours) in items)
            {
                Paint(px, w, h, canvas, colours, x, gap + (3 * T - canvas.Height) / 2);
                x += canvas.Width + gap;
            }
            return px;
        }

        /// <summary>A 30×17-tile region of a real generated map with an HQ, a spawner, miners and two armies.</summary>
        private static Color32[] Scene(StylePalette palette, out int w, out int h)
        {
            const int T = StyleGen.TileSize;
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
                int mask = (Rock(mx, my + 1) ? StyleGen.North : 0) | (Rock(mx + 1, my) ? StyleGen.East : 0)
                         | (Rock(mx, my - 1) ? StyleGen.South : 0) | (Rock(mx - 1, my) ? StyleGen.West : 0);
                PixelCanvas tile = kind == TileType.Ground ? StyleGen.Floor(Seed, variant)
                    : kind == TileType.Gem ? StyleGen.Gem(Seed, variant, mask) : StyleGen.Rock(Seed, mask);
                if (kind != TileType.Ground) Paint(px, W, H, StyleGen.Floor(Seed, variant), palette.Colours, tx * T, ty * T);
                Paint(px, W, H, tile, palette.Colours, tx * T, ty * T);
            }

            Color32[] blue = palette.WithTeam(Player(0)), red = palette.WithTeam(Player(1));
            float2 Local(float2 world) => world - new float2(x0, y0);
            void Put(PixelCanvas sprite, Color32[] colours, float2 centreTile) =>
                Paint(px, W, H, sprite, colours, (int)(centreTile.x * T) - sprite.Width / 2, (int)(centreTile.y * T) - sprite.Height / 2);

            float2 hq = Local((float2)site + 0.5f);
            Put(StyleGen.Base(), blue, hq);
            Put(StyleGen.Spawner(0), blue, hq + new float2(4f, -0.5f));
            int miners = 0;
            for (int ty = 1; ty < SceneH - 1 && miners < 2; ty++)
            for (int tx = 1; tx < SceneW - 1 && miners < 2; tx++)
            {
                int mx = x0 + tx, my = y0 + ty;
                if (Rock(mx, my) || map.Grid.TileAt(new int2(mx + 1, my)) != TileType.Gem) continue;
                Put(StyleGen.Miner(miners), blue, new float2(tx + 0.5f, ty + 0.5f));
                miners++;
            }
            var rng = new System.Random(4);
            int blueCount = 0, redCount = 0;
            for (int i = 0; i < 2000 && (blueCount < 18 || redCount < 16); i++)
            {
                int tx = rng.Next(SceneW), ty = rng.Next(SceneH);
                if (Rock(x0 + tx, y0 + ty) || math.distance(new float2(tx, ty), hq) < 3.5f) continue;
                bool isBlue = tx < 16;
                if (isBlue ? blueCount >= 18 : redCount >= 16) continue;
                double facing = isBlue ? rng.Next(-1, 2) * Math.PI / 4 : Math.PI + rng.Next(-1, 2) * Math.PI / 4;
                Put(StyleGen.Tank(facing, rng.Next(6) == 0 ? 2 : rng.Next(2)), isBlue ? blue : red, new float2(tx + 0.5f, ty + 0.5f));
                if (isBlue) blueCount++; else redCount++;
            }
            return px;
        }

        private static Color32[] Background(int w, int h)
        {
            var px = new Color32[w * h];
            var bg = new Color32(0x26, 0x30, 0x3A, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            return px;
        }

        private static void Paint(Color32[] dst, int w, int h, PixelCanvas src, Color32[] colours, int ox, int oy)
        {
            for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                byte i = src.Get(x, y);
                int dx = ox + x, dy = oy + y;
                if (i != 0 && (uint)dx < (uint)w && (uint)dy < (uint)h) dst[dy * w + dx] = colours[i];
            }
        }

        private static void WritePng(string path, Color32[] px, int w, int h, int scale)
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

        private static string PageHead() => @"<!doctype html><html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Style Samples</title><style>
:root{--bg:#1b2026;--panel:#26303a;--text:#e8edf2;--muted:#9aa7b4;--accent:#3fd2e6}
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 system-ui,sans-serif}
main{max-width:1240px;margin:0 auto;padding:24px 16px 64px}
h1{font-size:26px;margin:0 0 4px} h2{font-size:21px;margin:0}
p{color:var(--muted);margin:4px 0 12px}
section{background:var(--panel);border-radius:8px;padding:16px;margin:24px 0}
.tag{display:inline-block;background:var(--accent);color:#06222a;font-weight:700;border-radius:4px;padding:1px 8px;margin-right:8px}
.swatches{display:flex;flex-wrap:wrap;gap:6px;margin:8px 0 14px}
.sw{width:74px;font-size:11px;color:var(--muted)} .sw b{display:block;height:28px;border-radius:4px;border:1px solid #0006}
img{image-rendering:pixelated;max-width:100%;height:auto;display:block;border-radius:4px}
.label{font-size:13px;color:var(--muted);margin:12px 0 6px}
</style></head><body><main>
<h1>WAR-2D style samples</h1>
<p>Everything below is drawn by code in the style of the current placeholder art, at 32 px per tile (the current art's size; the earlier prototypes were 16 px). Each section is one palette: same shapes, different colours. Units and buildings show player colours 1 (blue) and 2 (red); their team-coloured parts follow whatever colour the player picks.</p>
<p>Pick a palette, or mix and match (for example, Gunmetal machines on Charcoal ground), or name any colour to change: each swatch below is one colour role.</p>
";

        private static string Section(StylePalette palette, string slug)
        {
            var s = new StringBuilder();
            int number = Array.IndexOf(StylePalettes.All, palette) + 1;
            s.Append($"<section><h2><span class=\"tag\">{number}</span>{palette.Name}</h2><p>{palette.Description}</p><div class=\"swatches\">");
            for (int i = StylePalette.Outline; i < StylePalette.Team0; i++)
            {
                string hex = StylePalette.ToHex(palette.Colours[i]);
                s.Append($"<div class=\"sw\"><b style=\"background:{hex}\"></b>{StylePalette.RoleNames[i]}<br>{hex}</div>");
            }
            s.Append("</div>");
            s.Append($"<div class=\"label\">Close up (3×): the Tank in its 8 facings, the Tank firing, Miner, Small Unit Spawner, HQ, two floor tiles, rock (enclosed, open to the north, open on two sides, isolated), gem rock</div>");
            s.Append($"<img src=\"{slug}-sprites.png\" alt=\"{palette.Name} sprites\">");
            s.Append($"<div class=\"label\">In game (2×, near the default zoom): a real generated map with an HQ, a spawner, miners and two armies</div>");
            s.Append($"<img src=\"{slug}-scene.png\" alt=\"{palette.Name} in-game scene\"></section>\n");
            return s.ToString();
        }
    }
}
