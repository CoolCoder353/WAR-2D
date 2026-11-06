using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using WAR2D.Art.Render;
using WAR2D.World;

namespace WAR2D.EditorTools
{
    /// <summary>
    /// Batches 2 to 4 of the v0.20 asset review: each new effect as a frame strip, the effects played together on the floor as
    /// short clips (sprite sheets the review page animates at 10 frames a second), and the sounds as WAVs. The clips are staged
    /// here from the generators; once the rules are built, clips from a real match replace them (spec §8.4).
    /// </summary>
    public static partial class RenderSamplesMenu
    {
        /// <summary>Frames a second the review page plays the clips at.</summary>
        private const int ClipFps = 10, ClipColumns = 8;

        private static readonly StringBuilder ClipIndex = new StringBuilder();

        /// <summary>One clip: a floor of <c>Tw × Th</c> tiles and its frames, each drawn by the caller.</summary>
        private sealed class Clip
        {
            public readonly int W, H;
            public readonly List<Color32[]> Frames = new List<Color32[]>();
            private readonly Color32[] floor;

            public Clip(int tilesW, int tilesH, int seed)
            {
                W = tilesW * T;
                H = tilesH * T;
                floor = new Color32[W * H];
                Tile(floor, W, H, (x, y) => TileAt(TileType.Ground, 0, ((x + seed) * 73856093 ^ y * 19349663) & 3));
            }

            /// <summary>A new frame on the bare floor.</summary>
            public Color32[] Next()
            {
                // The lit canvases allocate a lot per sprite; collect each frame so a long clip can't grow the editor's heap
                // (the first run of the artillery clip got the editor killed by the OOM killer).
                GC.Collect();
                var px = (Color32[])floor.Clone();
                Frames.Add(px);
                return px;
            }
        }

        /// <summary>Saves a clip as a grid sprite sheet, <see cref="ClipColumns"/> frames a row, and notes it in <c>clips.json</c>.</summary>
        private static void SaveClip(string name, Clip clip)
        {
            int n = clip.Frames.Count, cols = Math.Min(ClipColumns, n), rows = (n + cols - 1) / cols;
            int w = cols * clip.W, h = rows * clip.H;
            var sheet = new Color32[w * h];
            for (int i = 0; i < n; i++)
            {
                // Rows run top to bottom on the page; the image is stored bottom-up.
                int ox = i % cols * clip.W, oy = (rows - 1 - i / cols) * clip.H;
                Color32[] f = clip.Frames[i];
                for (int y = 0; y < clip.H; y++) Array.Copy(f, y * clip.W, sheet, (oy + y) * w + ox, clip.W);
            }
            clip.Frames.Clear();
            Save(Path.Combine(OnslaughtFolder, $"clip-{name}.png"), sheet, w, h, 1);
            GC.Collect();
            if (ClipIndex.Length > 0) ClipIndex.Append(",\n");
            ClipIndex.Append($"  \"{name}\": {{ \"w\": {clip.W}, \"h\": {clip.H}, \"frames\": {n}, \"cols\": {cols}, \"fps\": {ClipFps} }}");
        }

        /// <summary>An effect frame centred at (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
        private static void Fx(Color32[] px, int w, int h, EffectFrame f, float cx, float cy) =>
            Over(px, w, h, f.Pixels, f.Width, f.Height, Mathf.RoundToInt(cx - f.Width / 2f), Mathf.RoundToInt(cy - f.Height / 2f));

        /// <summary>An effect drawn along +x from (<paramref name="x0"/>, <paramref name="cy"/>), stretched to <paramref name="length"/> px.</summary>
        private static void FxAlong(Color32[] px, int w, int h, EffectFrame f, float x0, float cy, float length, float thickness = 1f)
        {
            int nw = Mathf.Max(1, Mathf.RoundToInt(length)), nh = Mathf.Max(1, Mathf.RoundToInt(f.Height * thickness));
            var s = new Color32[nw * nh];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                s[y * nw + x] = f.Pixels[Mathf.Min(f.Height - 1, y * f.Height / nh) * f.Width + Mathf.Min(f.Width - 1, x * f.Width / nw)];
            Over(px, w, h, s, nw, nh, Mathf.RoundToInt(x0), Mathf.RoundToInt(cy - nh / 2f));
        }

        /// <summary>A sprite darkened by <paramref name="k"/> (1 = as is, 0 = black) and sunk <paramref name="sink"/> px into the ground (cut at <paramref name="ground"/>).</summary>
        private static void Darkened(Color32[] dst, int w, int h, LitCanvas c, Color team, int cx, int cy, float k, int sink = 0, int ground = int.MinValue, float lift = 0f)
        {
            Color32[] img = Img(c, team, true, out int sw, out int sh, lift);
            int ox = cx - sw / 2, oy = cy - Mathf.RoundToInt(c.PivotY) - sink;
            for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                Color32 p = img[y * sw + x];
                if (p.a == 0 || oy + y < ground) img[y * sw + x] = default;
                else img[y * sw + x] = new Color32((byte)(p.r * k), (byte)(p.g * k), (byte)(p.b * k), p.a);
            }
            Over(dst, w, h, img, sw, sh, ox, oy);
        }

        /// <summary>Frames <c>make(i, t)</c> for i in 0..n-1 with t running 0..1.</summary>
        private static List<EffectFrame> Seq(int n, Func<int, float, EffectFrame> make)
        {
            var list = new List<EffectFrame>();
            for (int i = 0; i < n; i++) list.Add(make(i, n > 1 ? (float)i / (n - 1) : 0f));
            return list;
        }

        private static void RenderOnslaughtEffects()
        {
            ClipIndex.Clear();
            Color blue = Player(0), red = Player(1);
            const int gap = 8;
            string F(string name) => Path.Combine(OnslaughtFolder, name);
            void StripFx(string name, List<EffectFrame> frames)
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
                Save(F($"fx-{name}.png"), px, w, h, 1);
                CheckEffectPalette(name, frames);
            }

            // ---------- Frame strips ----------
            StripFx("LeapSparks", Seq(5, (i, t) => EffectGen.LeapSparks(t, 7)));
            StripFx("LeapShadow", Seq(4, (i, t) => EffectGen.LeapShadow(t)));
            StripFx("BomberLanding", Seq(8, (i, t) => EffectGen.ShockBlast(5, 2f, t, 29)));
            StripFx("BurnFlames1", Seq(4, (i, t) => EffectGen.BurnFlames(1, i)));
            StripFx("BurnFlames2", Seq(4, (i, t) => EffectGen.BurnFlames(2, i)));
            StripFx("BurnFlames3", Seq(4, (i, t) => EffectGen.BurnFlames(3, i)));
            StripFx("SootSmoke", Seq(5, (i, t) => EffectGen.SootSmoke(t, 3)));
            StripFx("FlameScorch", Seq(4, (i, t) => EffectGen.FlameScorch(i, 9)));
            StripFx("SniperTrail", Seq(4, (i, t) => EffectGen.SniperTrail(t)));
            StripFx("SniperHit", Seq(4, (i, t) => EffectGen.SniperHit(i)));
            StripFx("MuzzleGlint", Seq(3, (i, t) => EffectGen.MuzzleGlint(i)));
            StripFx("ArmourSparks", Seq(4, (i, t) => EffectGen.ArmourSparks(i)));
            StripFx("HeavyShell", Seq(8, (i, t) => EffectGen.HeavyShell(i * Mathf.PI / 4, i % 2)));
            StripFx("IncomingShell", Seq(2, (i, t) => EffectGen.IncomingShell(i)));
            StripFx("LaunchPuff", Seq(6, (i, t) => EffectGen.LaunchPuff(t, 5)));
            StripFx("ArtilleryBlast", Seq(8, (i, t) => EffectGen.ShockBlast(8, 3.5f, t, 37, dust: true)));
            StripFx("CraterLarge", Seq(3, (i, t) => EffectGen.CraterLarge(i, 0)));
            StripFx("TwinMuzzle", Seq(2, (i, t) => EffectGen.TwinMuzzle(i)));
            StripFx("TracerStream", Seq(3, (i, t) => EffectGen.TracerStream(i)));
            StripFx("DeathSmall", Seq(7, (i, t) => EffectGen.DeathSmall(t, 41)));
            StripFx("DeathLarge", Seq(8, (i, t) => EffectGen.DeathLarge(t, 43)));
            StripFx("Debris", Seq(6, (i, t) => EffectGen.Debris(t, 47)));
            StripFx("DustCloud", Seq(6, (i, t) => EffectGen.DustCloud(2, t, 53)));
            StripFx("HqFinale", Seq(8, (i, t) => EffectGen.ShockBlast(8, 3f, t, 59, dust: true)));
            // The laser is soft (partial alpha, like the flame), so it skips the palette check.
            var laser = Seq(5, (i, t) => EffectGen.AimLaser(t));
            {
                int w = 4 * T + 2 * gap, h = laser.Count * 16 + gap;
                var px = new Color32[w * h];
                Tile(px, w, h, (x, y) => TileAt(TileType.Ground, 0, (x + y) & 3));
                for (int i = 0; i < laser.Count; i++) Over(px, w, h, laser[i].Pixels, laser[i].Width, laser[i].Height, gap, h - gap - (i + 1) * 16 + 4);
                Save(F("fx-AimLaser.png"), px, w, h, 1);
            }
            var splash = Seq(2, (i, t) => EffectGen.FlameSplashSized(i, 2.5f));
            {
                int w = 2 * splash[0].Width + 3 * gap, h = splash[0].Height + 2 * gap;
                var px = new Color32[w * h];
                Tile(px, w, h, (x, y) => TileAt(TileType.Ground, 0, (x + y) & 3));
                for (int i = 0; i < 2; i++) Over(px, w, h, splash[i].Pixels, splash[i].Width, splash[i].Height, gap + i * (splash[0].Width + gap), gap);
                Save(F("fx-FlameSplashWide.png"), px, w, h, 1);
            }

            // ---------- Clips ----------
            ClipLeap(blue, red);
            ClipFlame(blue, red);
            ClipBurningBuilding(red);
            ClipSniper(blue, red);
            ClipArmour(blue, red);
            ClipArtillery(blue, red);
            ClipIncoming(red);
            ClipTurret(blue, red);
            ClipDeaths(blue);
            ClipCollapse(blue);
            File.WriteAllText(F("clips.json"), "{\n" + ClipIndex + "\n}\n");
        }

        /// <summary>A Bomber runs at a Tank block, crouches, leaps over the front Tank (shadow and sparks) and lands in the middle.</summary>
        private static void ClipLeap(Color blue, Color red)
        {
            var clip = new Clip(10, 4, 1);
            int W = clip.W, H = clip.H, cy = 2 * T;
            var tanks = new[] { (6.3f, 1.4f), (6.6f, 2.6f), (7.6f, 2.0f), (8.4f, 1.2f), (8.6f, 2.8f), (7.5f, 3.3f) };
            float land = 7.6f;
            for (int f = 0; f < 22; f++)
            {
                Color32[] px = clip.Next();
                bool landed = f >= 12;
                if (landed) Fx(px, W, H, EffectGen.Scorch(2, 0, 3), land * T, 2.0f * T);
                foreach (var (tx, ty) in tanks)
                {
                    float d = Mathf.Abs(tx - land) + Mathf.Abs(ty - 2.0f);
                    bool dead = landed && d < 1.6f && f >= 13;
                    if (dead) Charred(px, W, H, RenderGen.Tank(Mathf.PI, 0), red, (int)(tx * T), (int)(ty * T));
                    else Sprite(px, W, H, RenderGen.Tank(Mathf.PI, f % 2), red, (int)(tx * T), (int)(ty * T));
                }
                if (f < 5)
                    Sprite(px, W, H, RenderGen.SuicideBomber(0, f % 2), blue, (int)((1f + f * 0.35f) * T), cy);
                else if (f == 5)
                    Darkened(px, W, H, RenderGen.SuicideBomber(0, 2), blue, (int)(2.75f * T), cy - 3, 0.9f);
                else if (f < 12)
                {
                    float u = (f - 5) / 6f, x = Mathf.Lerp(2.75f, land, u) * T, height = Mathf.Sin(Mathf.PI * u);
                    Fx(px, W, H, EffectGen.LeapShadow(height), x, cy);
                    for (int k = 1; k <= 2; k++)
                    {
                        float pu = Mathf.Max(0, u - k * 0.12f), sx = Mathf.Lerp(2.75f, land, pu) * T, sy = cy + Mathf.Sin(Mathf.PI * pu) * 1.1f * T;
                        Fx(px, W, H, EffectGen.LeapSparks(k * 0.3f, 7 + f), sx, sy);
                    }
                    Sprite(px, W, H, RenderGen.SuicideBomber(0, 2 + f % 2), blue, (int)x, (int)(cy + height * 1.1f * T));
                }
                if (landed && f < 21) Fx(px, W, H, EffectGen.ShockBlast(5, 2f, (f - 12) / 8f, 29), land * T, cy);
            }
            SaveClip("leap", clip);
        }

        /// <summary>A Flamethrower sweeps a Tank group at 5.5 tiles: the stream, the 2.5-tile splash, the scorch, and the targets burning on after it stops.</summary>
        private static void ClipFlame(Color blue, Color red)
        {
            var clip = new Clip(12, 6, 2);
            int W = clip.W, H = clip.H;
            float fx = 1.5f * T, cy = 3 * T, target = fx + 5.5f * T;
            var tanks = new[] { (7.0f, 3.0f), (7.6f, 4.2f), (8.2f, 2.2f), (6.4f, 1.9f), (8.8f, 3.6f), (10.6f, 4.8f) };
            for (int f = 0; f < 24; f++)
            {
                Color32[] px = clip.Next();
                bool firing = f < 12;
                if (f >= 2) FxAlong(px, W, H, EffectGen.FlameScorch(0, 9), fx + 20, cy, 5.5f * T - 20, 1f);
                Sprite(px, W, H, RenderGen.Flamethrower(0, firing ? 2 + f % 2 : 0), blue, (int)fx, (int)cy);
                foreach (var (tx, ty) in tanks)
                {
                    bool inSplash = Mathf.Sqrt((tx * T - target) * (tx * T - target) + (ty * T - cy) * (ty * T - cy)) < 2.5f * T;
                    Sprite(px, W, H, RenderGen.Tank(Mathf.PI, 0), red, (int)(tx * T), (int)(ty * T));
                    if (inSplash && f >= 3)
                    {
                        Fx(px, W, H, EffectGen.BurnFlames(1, f % 4), tx * T, ty * T + 14);
                        float puffT = (f % 6) / 6f;
                        Fx(px, W, H, EffectGen.SootSmoke(puffT, (int)(tx * 10)), tx * T + 4, ty * T + 30 + puffT * 40);
                    }
                }
                if (firing)
                {
                    FxAlong(px, W, H, EffectGen.FlameStream(f % 4), fx + 22, cy + 2, 5.5f * T - 22, 1.3f);
                    Fx(px, W, H, EffectGen.FlameSplashSized(f % 2, 2.5f), target, cy);
                }
            }
            SaveClip("flame", clip);
        }

        /// <summary>A burning Barracks: flames across the footprint, embers and a smoke column.</summary>
        private static void ClipBurningBuilding(Color red)
        {
            var clip = new Clip(4, 5, 3);
            int W = clip.W, H = clip.H;
            float cx = 2 * T, cy = 1.6f * T;
            for (int f = 0; f < 12; f++)
            {
                Color32[] px = clip.Next();
                Sprite(px, W, H, RenderGen.Spawner(0), red, (int)cx, (int)cy, RenderGen.Lift);
                Fx(px, W, H, EffectGen.BurnFlames(2, f % 4), cx, cy + 26);
                for (int k = 0; k < 3; k++)
                {
                    float t = ((f + k * 4) % 12) / 12f;
                    Fx(px, W, H, EffectGen.SootSmoke(t, k + 1), cx + 22 + t * 10, cy + 60 + t * 120);
                }
            }
            SaveClip("burn-building", clip);
        }

        /// <summary>A 2×2 Sniper turns, aims for 2 s (the laser brightens, the glint), fires and nearly kills a Heavy with one shot.</summary>
        private static void ClipSniper(Color blue, Color red)
        {
            var clip = new Clip(11, 3, 4);
            int W = clip.W, H = clip.H;
            float sx = 1.5f * T, cy = 1.5f * T, hx = 9.3f * T;
            float muzzle = sx + RenderGen.Sniper2x2Pivot + RenderGen.Sniper2x2MuzzleReach;
            for (int f = 0; f < 30; f++)
            {
                Color32[] px = clip.Next();
                Sprite(px, W, H, RenderGen.Heavy(Mathf.PI, f < 24 ? f % 2 : 0), red, (int)hx, (int)cy);
                int frame = f == 22 ? 2 : f == 23 ? 3 : 0;
                Sprite(px, W, H, RenderGen.Sniper2x2(0, frame), blue, (int)sx, (int)cy);
                if (f < 21) FxAlong(px, W, H, EffectGen.AimLaser(f / 20f), muzzle, cy, hx - 14 - muzzle);
                if (f >= 18 && f < 21) Fx(px, W, H, EffectGen.MuzzleGlint(f - 18), muzzle, cy);
                if (f >= 22 && f < 26) FxAlong(px, W, H, EffectGen.SniperTrail((f - 22) / 3f), muzzle, cy, hx - muzzle);
                if (f >= 22 && f < 26) Fx(px, W, H, EffectGen.SniperHit(f - 22), hx - 10, cy);
            }
            SaveClip("sniper", clip);
        }

        /// <summary>A Scout's shots bounce off a Heavy's armour: the spark ring on each hit.</summary>
        private static void ClipArmour(Color blue, Color red)
        {
            var clip = new Clip(7, 2, 5);
            int W = clip.W, H = clip.H;
            float sx = 1.0f * T, cy = T, hx = 5.5f * T;
            for (int f = 0; f < 12; f++)
            {
                Color32[] px = clip.Next();
                Sprite(px, W, H, RenderGen.Heavy(Mathf.PI, 0), red, (int)hx, (int)cy);
                int phase = f % 6;
                Sprite(px, W, H, RenderGen.Scout(0, phase == 0 ? 2 : phase == 1 ? 3 : 0), blue, (int)sx, (int)cy);
                if (phase == 0) FxAlong(px, W, H, EffectGen.ScoutTracer(), sx + 30, cy, hx - 40 - sx - 30);
                if (phase >= 1 && phase < 5) Fx(px, W, H, EffectGen.ArmourSparks(phase - 1), hx - 36, cy);
            }
            SaveClip("armour", clip);
        }

        /// <summary>An Artillery fires (launch smoke), the bigger shell flies its arc with its shadow and lands: the 3.5-tile blast, dust, the larger crater.</summary>
        private static void ClipArtillery(Color blue, Color red)
        {
            var clip = new Clip(14, 6, 6);
            int W = clip.W, H = clip.H;
            float ax = 1.5f * T, cy = 2.6f * T, lx = 10.5f * T;
            float muzzle = ax + 46;
            var targets = new[] { (10.0f, 2.1f), (11.2f, 3.2f), (9.4f, 3.7f), (11.6f, 1.6f), (12.9f, 2.7f) };
            const int fly = 22, land = 4 + fly;
            for (int f = 0; f < land + 14; f++)
            {
                Color32[] px = clip.Next();
                if (f >= land) Fx(px, W, H, EffectGen.CraterLarge(1, 0), lx, cy);
                foreach (var (tx, ty) in targets)
                {
                    bool dead = f >= land + 1 && Mathf.Abs(tx * T - lx) + Mathf.Abs(ty * T - cy) < 2.2f * T;
                    if (dead) Charred(px, W, H, RenderGen.Tank(Mathf.PI, 0), red, (int)(tx * T), (int)(ty * T));
                    else Sprite(px, W, H, RenderGen.Tank(Mathf.PI, 0), red, (int)(tx * T), (int)(ty * T));
                }
                Sprite(px, W, H, RenderGen.Artillery(0, f == 0 ? 2 : f == 1 ? 3 : 0, true), blue, (int)ax, (int)cy);
                if (f < 8) Fx(px, W, H, EffectGen.LaunchPuff(f / 7f, 5), muzzle + 6, cy + 20 + f * 2);
                if (f >= 2 && f < land)
                {
                    float u = (f - 2f) / (land - 2f), x = Mathf.Lerp(muzzle, lx, u), height = Mathf.Sin(Mathf.PI * u) * 2.6f * T + (1 - u) * 18;
                    float dy = Mathf.Cos(Mathf.PI * u) * 2.6f * T * Mathf.PI / (lx - muzzle);
                    Fx(px, W, H, EffectGen.ShellShadow(), x, cy);
                    for (int k = 1; k <= 3; k++)
                    {
                        float pu = Mathf.Max(0, u - k * 0.03f);
                        Fx(px, W, H, EffectGen.ShellPuff(k / 4f), Mathf.Lerp(muzzle, lx, pu), cy + Mathf.Sin(Mathf.PI * pu) * 2.6f * T);
                    }
                    Fx(px, W, H, EffectGen.HeavyShell(Mathf.Atan(dy), f % 2), x, cy + height);
                }
                if (f >= land) Fx(px, W, H, EffectGen.ShockBlast(8, 3.5f, (f - land) / 13f, 37, dust: true), lx, cy);
            }
            SaveClip("artillery", clip);
        }

        /// <summary>What a player who sees only the landing point is shown: the last 0.6 s of the shell, dropping straight down, then the blast.</summary>
        private static void ClipIncoming(Color red)
        {
            var clip = new Clip(8, 7, 7);
            int W = clip.W, H = clip.H;
            float lx = 4f * T, cy = 3f * T;
            for (int f = 0; f < 20; f++)
            {
                Color32[] px = clip.Next();
                if (f >= 6) Fx(px, W, H, EffectGen.CraterLarge(2, 0), lx, cy);
                Sprite(px, W, H, RenderGen.Heavy(Mathf.PI * 0.75f, 0), red, (int)(lx + 1.2f * T), (int)(cy + 0.4f * T));
                if (f < 6)
                {
                    float drop = (1f - f / 6f) * 4f * T;
                    Fx(px, W, H, EffectGen.LeapShadow(1f - f / 6f), lx, cy);
                    Fx(px, W, H, EffectGen.IncomingShell(f % 2), lx, cy + drop + T);
                }
                else Fx(px, W, H, EffectGen.ShockBlast(8, 3.5f, (f - 6) / 13f, 37, dust: true), lx, cy);
            }
            SaveClip("incoming", clip);
        }

        /// <summary>The 2×2 Gun turret firing ten shots a second at an incoming Bomber: alternating barrels, the merged tracer stream, hits.</summary>
        private static void ClipTurret(Color blue, Color red)
        {
            var clip = new Clip(9, 3, 8);
            int W = clip.W, H = clip.H;
            float tx = 1.5f * T, cy = 1.5f * T;
            float mx = tx + RenderGen.Turret2x2MuzzleReach, my = cy + RenderGen.Turret2x2MuzzleHeight * RenderGen.Lift;
            for (int f = 0; f < 12; f++)
            {
                Color32[] px = clip.Next();
                float bx = (7.8f - f * 0.25f) * T;
                if (f < 10) Sprite(px, W, H, RenderGen.SuicideBomber(Mathf.PI, f % 2), red, (int)bx, (int)cy);
                else Fx(px, W, H, EffectGen.ShockBlast(5, 2f, (f - 10) / 7f, 29), bx, cy);
                Sprite(px, W, H, RenderGen.GunTurret2x2Base(0), blue, (int)tx, (int)cy, RenderGen.Lift);
                Sprite(px, W, H, RenderGen.GunTurret2x2Head(0, 1 + f % 2), blue, (int)tx, (int)cy, RenderGen.Lift);
                if (f < 10)
                {
                    Fx(px, W, H, EffectGen.TwinMuzzle(f % 2), mx + 14, my);
                    int len = Mathf.RoundToInt(bx - mx - 30);
                    FxAlong(px, W, H, EffectGen.TracerStream(f % 3, len), mx + 18, my, len);
                    Fx(px, W, H, EffectGen.HitSpark(f % 3), bx - 12, cy + 4);
                }
            }
            SaveClip("turret", clip);
        }

        /// <summary>
        /// Deaths by cause, side by side: a Tank shot (pop, turret thrown), a Heavy (two blasts, larger scorch), a Tank caught in a
        /// blast (wreck flung and turned, debris), and a Tank burned (flames, burning down, a black smouldering wreck).
        /// </summary>
        private static void ClipDeaths(Color blue)
        {
            var clip = new Clip(16, 4, 9);
            int W = clip.W, H = clip.H;
            float cy = 2 * T;
            float shot = 2f * T, large = 6f * T, blast = 10f * T, fire = 14f * T;
            for (int f = 0; f < 24; f++)
            {
                Color32[] px = clip.Next();
                float tShot = f / 10f;
                // Shot: hull to wreck, the turret thrown a tile up-right on an arc and spun.
                if (f >= 1) Fx(px, W, H, EffectGen.Scorch(1, 0, 5), shot, cy);
                if (f == 0) Sprite(px, W, H, RenderGen.Tank(0.4f, 0), blue, (int)shot, (int)cy);
                else
                {
                    Charred(px, W, H, RenderGen.Tank(0.4f, 0, 0.4f, UnitLayer.Hull), blue, (int)shot, (int)cy);
                    float u = Mathf.Clamp01((f - 1) / 7f);
                    Charred(px, W, H, RenderGen.Tank(0.4f + u * 4f, 0, 0.4f + u * 4f, UnitLayer.Turret), blue,
                        (int)(shot + u * 0.9f * T), (int)(cy + u * 0.4f * T + Mathf.Sin(Mathf.PI * u) * 0.7f * T));
                }
                if (f < 7) Fx(px, W, H, EffectGen.DeathSmall(tShot * 1.4f, 41), shot, cy);

                // Large: the Heavy, a second blast a beat later, a large scorch.
                if (f >= 1) Fx(px, W, H, EffectGen.Scorch(2, 0, 11), large, cy);
                if (f == 0) Sprite(px, W, H, RenderGen.Heavy(-0.3f, 0), blue, (int)large, (int)cy);
                else Charred(px, W, H, RenderGen.Heavy(-0.3f, 0), blue, (int)large, (int)cy);
                if (f < 13) Fx(px, W, H, EffectGen.DeathLarge(f / 12f, 43), large, cy);

                // Blast: flung up to a tile away from the blast (to the left) and turned, debris.
                float v = Mathf.Clamp01(f / 6f);
                if (f >= 6) Fx(px, W, H, EffectGen.Scorch(1, 0, 13), blast + 0.9f * T, cy - 0.2f * T);
                if (f == 0) Sprite(px, W, H, RenderGen.Tank(Mathf.PI, 0), blue, (int)blast, (int)cy);
                else Charred(px, W, H, RenderGen.Tank(Mathf.PI + v * 1.9f, 0), blue, (int)(blast + v * 0.9f * T), (int)(cy - v * 0.2f * T + Mathf.Sin(Mathf.PI * v) * 0.5f * T));
                if (f < 9) Fx(px, W, H, EffectGen.Debris(f / 8f, 47), blast + 0.4f * T, cy);
                if (f < 5) Fx(px, W, H, EffectGen.Blast(2, f / 4f * 0.6f + 0.2f, 61), blast - 0.9f * T, cy + 0.2f * T);

                // Fire: burns on the sprite, darkening, then a black wreck smouldering.
                if (f < 10)
                {
                    Darkened(px, W, H, RenderGen.Tank(Mathf.PI / 2, 0), blue, (int)fire, (int)cy, 1f - f * 0.07f);
                    Fx(px, W, H, EffectGen.BurnFlames(1, f % 4), fire, cy + 14);
                }
                else
                {
                    Fx(px, W, H, EffectGen.Scorch(1, 0, 17), fire, cy);
                    Darkened(px, W, H, RenderGen.Tank(Mathf.PI / 2, 0), blue, (int)fire, (int)cy, 0.16f);
                    if (f < 13) Fx(px, W, H, EffectGen.BurnFlames(1, 3), fire, cy + 8);
                }
                for (int k = 0; k < 3; k++)
                {
                    float t = ((f + k * 3) % 9) / 9f;
                    Fx(px, W, H, EffectGen.SootSmoke(t, 20 + k), fire + 6 + t * 8, cy + 26 + t * 70);
                }
            }
            SaveClip("deaths", clip);
        }

        /// <summary>A Barracks collapsing over 1.5 s: small blasts across the footprint, the sprite darkening and sinking, dust, then rubble.</summary>
        private static void ClipCollapse(Color blue)
        {
            var clip = new Clip(5, 5, 10);
            int W = clip.W, H = clip.H;
            float cx = 2.5f * T, cy = 2.2f * T;
            int ground = (int)(cy - T);
            var spots = new[] { (-0.5f, -0.4f), (0.6f, 0.3f), (-0.2f, 0.6f), (0.4f, -0.6f), (0f, 0f) };
            for (int f = 0; f < 22; f++)
            {
                Color32[] px = clip.Next();
                float u = Mathf.Clamp01(f / 15f);
                if (f >= 10) Fx(px, W, H, EffectGen.Rubble(2, 0, 23), cx, cy);
                if (f < 16) Darkened(px, W, H, RenderGen.Spawner(0), blue, (int)cx, (int)cy, 1f - 0.65f * u, (int)(u * 40), ground, RenderGen.Lift);
                for (int k = 0; k < spots.Length; k++)
                {
                    int start = k * 3;
                    if (f >= start && f < start + 5)
                        Fx(px, W, H, EffectGen.Blast(1, (f - start) / 4f, 70 + k), cx + spots[k].Item1 * T, cy + spots[k].Item2 * T + 20);
                }
                if (f >= 6) Fx(px, W, H, EffectGen.DustCloud(2, Mathf.Clamp01((f - 6) / 15f), 53), cx, cy);
            }
            SaveClip("collapse", clip);
        }

        /// <summary>Batch 4: renders the proposed sound sets in <c>docs/art/v0.20/sfx-src/</c> (and today's sounds they replace or rename) to WAVs beside the page.</summary>
        private static void RenderOnslaughtSounds()
        {
            string src = Path.Combine(OnslaughtFolder, "sfx-src"), outDir = Path.Combine(OnslaughtFolder, "sfx");
            Directory.CreateDirectory(outDir);
            if (!Directory.Exists(src)) return;
            void Render(string path, string name)
            {
                var p = JsonUtility.FromJson<WAR2D.Audio.SfxrParams>(File.ReadAllText(path));
                File.WriteAllBytes(Path.Combine(outDir, name + ".wav"), WAR2D.Audio.Wav.Encode(WAR2D.Audio.Sfxr.Render(p, p.seed, AudioBuild.SampleRate), AudioBuild.SampleRate));
            }
            foreach (string path in Directory.GetFiles(src, "*.json")) Render(path, Path.GetFileNameWithoutExtension(path));
            foreach (string old in new[] { "bomber_blast", "shot_sniper", "shot_artillery", "shell_impact", "shot_turret", "shot_flamethrower", "explosion_small", "explosion_large", "building_destroyed" })
                Render($"{AudioBuild.SfxSource}/{old}.json", "old_" + old);
        }
    }
}
