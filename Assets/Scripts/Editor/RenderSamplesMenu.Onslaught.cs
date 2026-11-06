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
    /// The v0.20 Onslaught asset review (spec <c>2026-11-06-v0.20-onslaught-design.md</c> §8.4, checklist
    /// <c>docs/art/v0.20-asset-review.md</c>): every new sprite, effect and sound rendered to <c>docs/art/v0.20/</c> for the owner's
    /// review page, in the game's finish (pixel art at 64 px per tile). Images are saved at 1× and the page zooms them.
    /// </summary>
    public static partial class RenderSamplesMenu
    {
        public const string OnslaughtFolder = "docs/art/v0.20";

        private static Color Player(int i) => (Color)(Color32)Palettes.Player(Palettes.Standard, i);

        /// <summary><c>WAR-2D/Art/Render v0.20 samples</c>: every batch of the v0.20 asset review.</summary>
        [MenuItem("WAR-2D/Art/Render v0.20 samples")]
        public static void RenderOnslaught()
        {
            mode = Finish.Pixel64;
            TileCache.Clear();
            Directory.CreateDirectory(OnslaughtFolder);
            RenderOnslaughtUnits();
            RenderOnslaughtEffects();
            RenderOnslaughtSounds();
            Debug.Log($"[Art] rendered v0.20 samples to {OnslaughtFolder}");
        }

        /// <summary>Batch 1: the 2×2 Sniper and Gun turret with their heads, the renamed Bomber and Mine, the new wrecks, a scene.</summary>
        private static void RenderOnslaughtUnits()
        {
            Color blue = Player(0), red = Player(1);
            const int gap = 8;
            string F(string name) => Path.Combine(OnslaughtFolder, name);

            // The Sniper: 8 facings, both shot frames, the head turned off the hull, red, then the old 1×1 Sniper, a Heavy and a Tank for scale.
            var sniper = new List<(LitCanvas[], Color)>();
            for (int d = 0; d < 8; d++) sniper.Add((new[] { RenderGen.Sniper2x2(d * Mathf.PI / 4, 0) }, blue));
            sniper.Add((new[] { RenderGen.Sniper2x2(0, 2) }, blue));
            sniper.Add((new[] { RenderGen.Sniper2x2(0, 3) }, blue));
            sniper.Add((new[] { RenderGen.Sniper2x2(0, 0, Mathf.PI / 4) }, blue));
            sniper.Add((new[] { RenderGen.Sniper2x2(Mathf.PI / 2, 1, Mathf.PI) }, blue));
            sniper.Add((new[] { RenderGen.Sniper2x2(5 * Mathf.PI / 4, 0) }, red));
            sniper.Add((new[] { RenderGen.Sniper(0, 0) }, blue));
            sniper.Add((new[] { RenderGen.Heavy(0, 0) }, blue));
            sniper.Add((new[] { RenderGen.Tank(0, 0) }, blue));
            Strip(F("sniper.png"), sniper, gap, 1);
            // The head alone (the layer the game turns), and the hull alone.
            var sniperLayers = new List<(LitCanvas[], Color)>();
            for (int d = 0; d < 8; d += 2) sniperLayers.Add((new[] { RenderGen.Sniper2x2(d * Mathf.PI / 4, 0, d * Mathf.PI / 4, UnitLayer.Hull) }, blue));
            for (int d = 0; d < 8; d += 2) sniperLayers.Add((new[] { RenderGen.Sniper2x2(d * Mathf.PI / 4, 0, d * Mathf.PI / 4, UnitLayer.Turret) }, blue));
            Strip(F("sniper-layers.png"), sniperLayers, gap, 1);

            // The Gun turret: the head in 8 facings over the base, each barrel firing, damaged, the base alone, red; the old one and a Tank for scale.
            var turret = new List<(LitCanvas[], Color)>();
            for (int d = 0; d < 8; d++) turret.Add((new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(d * Mathf.PI / 4) }, blue));
            turret.Add((new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(0, 1) }, blue));
            turret.Add((new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(0, 2) }, blue));
            turret.Add((new[] { RenderGen.GunTurret2x2Base(1), RenderGen.GunTurret2x2Head(Mathf.PI / 4) }, blue));
            turret.Add((new[] { RenderGen.GunTurret2x2Base(0) }, blue));
            turret.Add((new[] { RenderGen.GunTurret2x2Base(1) }, blue));
            turret.Add((new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(5 * Mathf.PI / 4) }, red));
            turret.Add((new[] { RenderGen.GunTurretBase(0), RenderGen.GunTurretHead(0) }, blue));
            turret.Add((new[] { RenderGen.Tank(0, 0) }, blue));
            Strip(F("turret.png"), turret, gap, 1, RenderGen.Lift);

            // The renamed Bomber and Mine: the same art under the new names (spec §2.6), every facing and frame.
            var bomber = new List<(LitCanvas, Color)>();
            for (int d = 0; d < 8; d++) bomber.Add((RenderGen.SuicideBomber(d * Mathf.PI / 4, 0), blue));
            for (int f = 1; f < 4; f++) bomber.Add((RenderGen.SuicideBomber(0, f), blue));
            bomber.Add((RenderGen.SuicideBomber(Mathf.PI, 2), red));
            Strip(F("bomber.png"), bomber, gap, 1);
            Strip(F("mine.png"), new List<(LitCanvas, Color)> { (RenderGen.Bomb(0), blue), (RenderGen.Bomb(1), blue), (RenderGen.Bomb(0), red), (RenderGen.Bomb(1), red) }, gap, 1);

            // Every player colour, one row per asset.
            var colours = new (string name, Func<int, LitCanvas[]> make, float lift)[]
            {
                ("sniper", i => new[] { RenderGen.Sniper2x2(-Mathf.PI / 4, 0) }, 0f),
                ("turret", i => new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(-Mathf.PI / 4) }, RenderGen.Lift),
                ("bomber", i => new[] { RenderGen.SuicideBomber(-Mathf.PI / 4, 0) }, 0f),
                ("mine", i => new[] { RenderGen.Bomb(0) }, 0f),
            };
            foreach (var (name, make, lift) in colours)
            {
                var row = new List<(LitCanvas[], Color)>();
                for (int i = 0; i < 8; i++) row.Add((make(i), Player(i)));
                Strip(F($"{name}-colours.png"), row, gap, 1, lift);
            }

            Save(F("wrecks.png"), Wrecks(blue, out int ww, out int wh), ww, wh, 1);
            Save(F("scene.png"), OnslaughtScene(blue, red, out int sw, out int sh), sw, sh, 1);
        }

        /// <summary>A sprite finished as the game draws it, then charred with the wreck tint.</summary>
        private static void Charred(Color32[] dst, int w, int h, LitCanvas c, Color team, int cx, int cy, float lift = 0f)
        {
            Color32[] img = Img(c, team, true, out int sw, out int sh, lift);
            Color tint = Decals.WreckTint;
            for (int i = 0; i < img.Length; i++)
                if (img[i].a > 0) img[i] = new Color32((byte)(img[i].r * tint.r), (byte)(img[i].g * tint.g), (byte)(img[i].b * tint.b), img[i].a);
            Over(dst, w, h, img, sw, sh, cx - sw / 2, cy - Mathf.RoundToInt(c.PivotY));
        }

        /// <summary>
        /// The new wrecks on the floor: the 2×2 Sniper charred on its large scorch with its head thrown a tile off (a shot death), and the
        /// 2×2 Gun turret's rubble with its head thrown off; the old 1×1 Sniper's wreck for comparison.
        /// </summary>
        private static Color32[] Wrecks(Color blue, out int w, out int h)
        {
            int W = 12 * T, H = 4 * T;
            w = W;
            h = H;
            var px = new Color32[W * H];
            Tile(px, W, H, (x, y) => TileAt(TileType.Ground, 0, (x * 3 + y) & 3));
            void Mark(EffectFrame f, int cx, int cy) => Over(px, W, H, f.Pixels, f.Width, f.Height, cx - f.Width / 2, cy - f.Height / 2);

            // The Sniper: the hull where it died, the head a tile away and turned.
            int sx = 2 * T, sy = 2 * T;
            Mark(EffectGen.Scorch(2, 0, 11), sx, sy);
            Charred(px, W, H, RenderGen.Sniper2x2(Mathf.PI / 6, 0, Mathf.PI / 6, UnitLayer.Hull), blue, sx, sy);
            Charred(px, W, H, RenderGen.Sniper2x2(-2.2f, 0, -2.2f, UnitLayer.Turret), blue, sx + T + 20, sy - 30);

            // The Gun turret: rubble over its footprint, the damaged base sunk into it, the head thrown off.
            int tx = 6 * T, ty = 2 * T;
            Mark(EffectGen.Rubble(2, 0, 23), tx, ty);
            Charred(px, W, H, RenderGen.GunTurret2x2Head(2.6f), blue, tx - T - 4, ty + 8, RenderGen.Lift);

            // The old 1×1 Sniper's wreck as the game draws it today.
            int ox = 10 * T, oy = 2 * T;
            Mark(EffectGen.Scorch(1, 0, 5), ox, oy);
            Charred(px, W, H, RenderGen.Sniper(Mathf.PI / 6, 0), blue, ox, oy);
            return px;
        }

        /// <summary>
        /// Open floor near an HQ: two 2×2 Gun turrets in front of the base, a blue gun line of 2×2 Snipers behind Heavies and
        /// Tanks, a Scout forward, against a red army with Bombers in front.
        /// </summary>
        private static Color32[] OnslaughtScene(Color blue, Color red, out int w, out int h)
        {
            int W = SceneW * T, H = SceneH * T;
            w = W;
            h = H;
            var px = new Color32[W * H];
            Tile(px, W, H, (x, y) => TileAt(TileType.Ground, 0, ((x * 73856093) ^ (y * 19349663)) & 3));
            bool Rock(int tx, int ty) => false;
            const int x0 = 0, y0 = 0;
            float2 hq = new float2(3.5f, SceneH / 2f);
            var used = new HashSet<int2>();
            for (int dy = -3; dy < 3; dy++)
            for (int dx = -3; dx < 3; dx++)
                used.Add(new int2((int)hq.x + dx, (int)hq.y + dy));
            bool Free(int tx, int ty, int size)
            {
                for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                    if (tx + dx < 0 || ty + dy < 0 || tx + dx >= SceneW || ty + dy >= SceneH || Rock(x0 + tx + dx, y0 + ty + dy) || used.Contains(new int2(tx + dx, ty + dy))) return false;
                return true;
            }
            void Claim(int tx, int ty, int size)
            {
                for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                    used.Add(new int2(tx + dx, ty + dy));
            }
            // The first free spot near a wanted tile, searched outward.
            int2? Near(float2 want, int size)
            {
                for (int r = 0; r < 8; r++)
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = (int)want.x + dx, ty = (int)want.y + dy;
                    if (Free(tx, ty, size)) { Claim(tx, ty, size); return new int2(tx, ty); }
                }
                return null;
            }
            var draws = new List<(float order, Action draw)>();
            void Put(Func<LitCanvas[]> make, Color team, float2 want, int size, float lift = 0f)
            {
                int2? at = Near(want, size);
                if (at == null) return;
                float2 c = (float2)at.Value + size / 2f;
                draws.Add((-c.y, () => { foreach (LitCanvas l in make()) Sprite(px, W, H, l, team, (int)(c.x * T), (int)(c.y * T), lift); }));
            }
            Put(() => new[] { RenderGen.Base() }, blue, hq - 2.5f, 5, RenderGen.Lift);
            float east = 0f, west = Mathf.PI;
            Put(() => new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(0.25f) }, blue, hq + new float2(4, 2), 2, RenderGen.Lift);
            Put(() => new[] { RenderGen.GunTurret2x2Base(0), RenderGen.GunTurret2x2Head(-0.2f, 1) }, blue, hq + new float2(4, -4), 2, RenderGen.Lift);
            Put(() => new[] { RenderGen.Sniper2x2(east, 0, 0.15f) }, blue, hq + new float2(7, 3), 2);
            Put(() => new[] { RenderGen.Sniper2x2(east, 2, -0.1f) }, blue, hq + new float2(7, -2), 2);
            Put(() => new[] { RenderGen.Heavy(east, 0) }, blue, hq + new float2(10, 1), 2);
            Put(() => new[] { RenderGen.Heavy(east, 2) }, blue, hq + new float2(10, -3), 2);
            for (int k = 0; k < 4; k++) Put(() => new[] { RenderGen.Tank(east, k % 3) }, blue, hq + new float2(12, -4 + k * 2.5f), 1);
            Put(() => new[] { RenderGen.Scout(east, 0) }, blue, hq + new float2(15, 0), 1);
            for (int k = 0; k < 5; k++) Put(() => new[] { RenderGen.SuicideBomber(west, k % 2) }, red, hq + new float2(18, -4 + k * 2), 1);
            for (int k = 0; k < 4; k++) Put(() => new[] { RenderGen.Tank(west, k % 2) }, red, hq + new float2(21, -3 + k * 2), 1);
            Put(() => new[] { RenderGen.Heavy(west, 0) }, red, hq + new float2(23, 0), 2);
            Put(() => new[] { RenderGen.Sniper2x2(west, 0, west - 0.2f) }, red, hq + new float2(25, -3), 2);
            draws.Sort((a, b) => a.order.CompareTo(b.order));
            foreach (var (_, draw) in draws) draw();
            return px;
        }
    }
}
