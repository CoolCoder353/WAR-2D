using UnityEngine;
using WAR2D.Art.Style;

namespace WAR2D.Art.Render
{
    /// <summary>
    /// The v0.20 Onslaught effects (spec <c>2026-11-06-v0.20-onslaught-design.md</c> §8.1 and §8.2), drawn for the owner's asset
    /// review: the Bomber's leap and landing, burning and scorch, the Sniper's laser, shot and the armour spark, the bigger
    /// Artillery shell and blast, the twin-barrelled Gun turret's flashes and stream, and deaths by size and cause. Same rules as
    /// the rest of <see cref="EffectGen"/> (palette colours, dithered fades) except the laser, which glows like the flame. None
    /// is in the atlases until the owner approves it (<c>docs/art/v0.20-asset-review.md</c>).
    /// </summary>
    public static partial class EffectGen
    {
        private static Color32 White => new Color32(255, 255, 255, 255);

        // ---------- Bomber leap (§2.2, §8.2) ----------

        /// <summary>The airborne Bomber's shadow on the ground: smaller and fainter the higher it is (<paramref name="height"/> 0..1).</summary>
        public static EffectFrame LeapShadow(float height) => Puff(0.3f - 0.12f * height, ArtStyle.Soot, 1f - 0.35f * height);

        /// <summary>
        /// The leap's spark trail at <paramref name="t"/> (0 = just thrown, 1 = gone), one tile: a few hot sparks and a dithered
        /// streak, trailing left (turned to the leap's heading when drawn), cooling through the fire ramp.
        /// </summary>
        public static EffectFrame LeapSparks(float t, int seed)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(seed, "leap sparks");
            float c = T / 2f;
            for (int k = 0; k < 9; k++)
            {
                float x = c - rng.Range(26) - t * 8, y = c + (rng.Range(17) - 8) * (0.5f + t);
                Color32 col = ArtStyle.FireRamp[Mathf.Min(6, 1 + (int)(t * 5) + rng.Range(2))];
                if (Bayer((int)x, (int)y) > 1.1f - t) continue;
                img.Set((int)x, (int)y, col);
                if (t < 0.5f) img.Set((int)x + 1, (int)y, col);
            }
            return img.Done();
        }

        /// <summary>
        /// A blast with a shockwave ring, <paramref name="tiles"/> square, the ring running out to <paramref name="ringTiles"/> (the
        /// blast radius) at <paramref name="t"/> (0 = flash, 1 = last wisps): a white flash, a fireball cooling into smoke
        /// (<see cref="Blast"/>), and the ring, hot then grey, thinning as it goes. Used for the Bomber's landing (2 tiles),
        /// the Artillery shell (3.5, with dust) and the HQ's finale.
        /// </summary>
        public static EffectFrame ShockBlast(int tiles, float ringTiles, float t, int seed, bool dust = false)
        {
            int s = tiles * T;
            // The fireball takes about half the ring's reach, so a 3.5-tile blast still has a 7-tile fire.
            int fireTiles = Mathf.Max(2, Mathf.RoundToInt(ringTiles * 1.15f));
            EffectFrame blast = Blast(fireTiles, t * 0.85f, seed);
            var img = new Img(s, s);
            float c = s / 2f;
            float ringMax = ringTiles * T, ring = ringMax * (0.15f + 0.85f * Mathf.Sqrt(Mathf.Clamp01(t * 1.4f))), width = 3.5f - 2.5f * t;
            if (dust)
            {
                // A low dust ring kicked up behind the shockwave, dithered and flattened for the 3/4 view.
                float dr = ring * 0.82f;
                for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x + 0.5f - c, dy = (y + 0.5f - c) * 1.3f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float n = Noise.Fbm(x, y, s, 4, 2, seed + 3) - 0.5f;
                    float k = (1f - Mathf.Abs(d - dr) / (T * 0.55f) + n * 0.8f) * Mathf.Clamp01(1.3f - t) * Mathf.Clamp01(t * 4f);
                    if (k > Bayer(x / 2, y / 2)) img.Set(x, y, Role(n > 0 ? StylePalette.Floor2 : StylePalette.Floor1, n > 0.12f ? 3 : 2));
                }
            }
            int off = (s - blast.Width) / 2;
            for (int y = 0; y < blast.Height; y++)
            for (int x = 0; x < blast.Width; x++)
                if (blast.Pixels[y * blast.Width + x].a > 0) img.Set(off + x, off + y, blast.Pixels[y * blast.Width + x]);
            Color32 wave = t < 0.25f ? ArtStyle.FireRamp[1] : t < 0.5f ? ArtStyle.FireRamp[3] : Role(StylePalette.Body3, 2);
            if (t < 0.95f)
                for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                    if (Mathf.Abs(d - ring) < width && Bayer(x, y) < 1.1f - t) img.Set(x, y, wave);
                }
            if (t < 0.1f)
            {
                int r = Mathf.RoundToInt(ringTiles * 4 + 3);
                for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                    if (x * x + y * y <= r * r) img.Set((int)c + x, (int)c + y, White);
            }
            return img.Done();
        }

        // ---------- Burning (§7.3, §8.2) ----------

        /// <summary>
        /// Flames over a burning unit or building <paramref name="tiles"/> square, frame <paramref name="frame"/> of 4: small
        /// tongues scattered over the footprint, each a step out of phase so the fire flickers, and a few rising embers.
        /// </summary>
        public static EffectFrame BurnFlames(int tiles, int frame)
        {
            int s = tiles * T;
            var img = new Img(s, s + T / 2);
            var rng = new ArtRandom(61 + tiles, "burn flames");
            int count = tiles == 1 ? 3 : tiles == 2 ? 6 : 4 * tiles;
            float[] height = { 9f, 15f, 12f, 6f }, width = { 3.5f, 4.5f, 4f, 2.5f };
            for (int k = 0; k < count; k++)
            {
                float cx = s * (0.2f + 0.6f * rng.Range(100) / 100f), by = s * (0.18f + 0.55f * rng.Range(100) / 100f);
                int f = (frame + k) % 4;
                float scale = 0.8f + 0.5f * rng.Range(100) / 100f + (tiles > 1 ? 0.3f : 0f);
                float h = height[f] * scale, w = width[f] * scale;
                for (int y = (int)by; y < by + h + 1 && y < img.H; y++)
                for (int x = (int)(cx - w - 2); x <= cx + w + 2; x++)
                {
                    float up = (y + 0.5f - by) / h;
                    if (up < 0f || up > 1f) continue;
                    float sway = Mathf.Sin(up * 3f + (frame + k) * 1.7f) * 1.2f * up;
                    float d = Mathf.Abs(x + 0.5f - cx - sway) / Mathf.Max(0.5f, w * (1f - up * up));
                    if (d > 1f) continue;
                    float heat = (1f - d) * (1.2f - up);
                    int i = heat > 0.85f ? 0 : heat > 0.6f ? 1 : heat > 0.4f ? 2 : heat > 0.22f ? 3 : 4;
                    img.Set(x, y, ArtStyle.FireRamp[Mathf.Min(6, i + (f == 3 ? 1 : 0))]);
                }
            }
            var embers = new ArtRandom(71 + frame, "burn embers");
            for (int k = 0; k < 2 + 2 * tiles; k++)
            {
                int ex = embers.Range(s), ey = s / 2 + embers.Range(s / 2 + T / 2 - 1);
                img.Set(ex, ey, ArtStyle.FireRamp[2 + embers.Range(3)]);
            }
            return img.Done();
        }

        /// <summary>A puff of the thin, dark smoke column over a fire, or of a fire-killed wreck's smoulder, at <paramref name="t"/> (0 = new, 1 = gone).</summary>
        public static EffectFrame SootSmoke(float t, int seed)
        {
            var img = new Img(T, T);
            float c = T / 2f, r = T * (0.07f + 0.13f * t), density = 1f - t * 0.9f;
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                float n = Noise.Fbm(x, y + t * 20f, T, 4, 2, seed) - 0.5f;
                float puff = (1f - Mathf.Sqrt(dx * dx + dy * dy) / r + n * 0.8f) * density * 1.4f;
                if (puff > Bayer(x / 2, y / 2)) img.Set(x, y, n > 0.05f ? Role(StylePalette.Body1, 1) : Role(StylePalette.Body0));
            }
            return img.Done();
        }

        /// <summary>
        /// Scorch along a flame stream, 3 tiles long by 1 (turned to the stream's heading when drawn), fading over
        /// <see cref="DecalFrames"/> frames: a dark smear that widens toward the far end, where the flame splashes.
        /// </summary>
        public static EffectFrame FlameScorch(int frame, int seed)
        {
            int w = 3 * T, h = T;
            var img = new Img(w, h);
            Color32 soot = Role(StylePalette.Body0), ash = Role(StylePalette.Body1, 1);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float along = (x + 0.5f) / w, half = 3f + 16f * along * along;
                float d = Mathf.Abs(y + 0.5f - h / 2f) / half + (Noise.Fbm(x, y, w, 6, 3, seed) - 0.5f) * 0.7f;
                if (d > 1f || along < 0.08f) continue;
                if (d > 0.6f && Bayer(x, y) > (1f - d) * 2.4f) continue;
                if (!Kept(x, y, frame)) continue;
                img.Set(x, y, d < 0.4f ? soot : ash);
            }
            return img.Done();
        }

        /// <summary>Fire where a flame lands, drawn for a splash of <paramref name="tiles"/> radius (v0.20: 2.5), on a canvas just large enough.</summary>
        public static EffectFrame FlameSplashSized(int frame, float tiles)
        {
            int s = Mathf.CeilToInt(tiles * 2 + 0.01f) * T;
            var glow = new Glow(s, s);
            var rng = new ArtRandom(43 + frame, "flame splash");
            float R() => rng.Range(1000) / 1000f;
            float c = glow.W / 2f, radius = tiles * T;
            int blobs = Mathf.RoundToInt(26 * tiles * tiles / (FlameSplashTiles * FlameSplashTiles));
            for (int i = 0; i < blobs; i++)
            {
                float angle = R() * 2f * Mathf.PI, k = Mathf.Sqrt(R());
                glow.Blob(c + Mathf.Cos(angle) * k * radius * 0.94f, c + Mathf.Sin(angle) * k * radius * 0.94f, (0.1f + 0.1f * R()) * T, Color.Lerp(FlameYellow, FlameEmber, k), 0.55f * (1f - 0.5f * k));
            }
            glow.Fade((x, y) => (radius - Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c))) / 4f);
            return glow.Done();
        }

        // ---------- Sniper (§5.3, §8.2) ----------

        private static readonly Color LaserRed = new Color(1f, 0.16f, 0.1f), LaserCore = new Color(1f, 0.75f, 0.65f);

        /// <summary>
        /// The Sniper's aim laser, 4 tiles long along +x (stretched to its length and turned when drawn), at aim
        /// <paramref name="charge"/> (0 = the aim starts, 1 = it fires): a faint red line that brightens and gains a hot core, with
        /// a small dot where it lands. Soft, like the flame.
        /// </summary>
        public static EffectFrame AimLaser(float charge)
        {
            var glow = new Glow(4 * T, 7);
            float a = 0.18f + 0.55f * charge;
            for (int x = 0; x < glow.W; x++)
            {
                glow.Add(x, 3, LaserRed, a);
                if (charge > 0.6f) glow.Add(x, 3, LaserCore, (charge - 0.6f) * 1.2f);
                float halo = a * 0.25f * charge;
                glow.Add(x, 2, LaserRed, halo);
                glow.Add(x, 4, LaserRed, halo);
            }
            glow.Blob(glow.W - 3, 3, 3.5f, LaserRed, 0.5f + 0.5f * charge);
            return glow.Done();
        }

        /// <summary>A glint at the Sniper's muzzle as its aim completes, frame <paramref name="frame"/> of 3: a four-pointed star that flares and shrinks.</summary>
        public static EffectFrame MuzzleGlint(int frame)
        {
            var img = new Img(T / 2, T / 2);
            int c = T / 4, arm = frame == 1 ? 9 : frame == 0 ? 5 : 3;
            for (int r = -arm; r <= arm; r++)
            {
                Color32 col = Mathf.Abs(r) < 2 ? White : ArtStyle.FireRamp[Mathf.Abs(r) < arm / 2 + 1 ? 0 : 1];
                img.Set(c + r, c, col);
                img.Set(c, c + r, col);
            }
            if (frame == 1)
                for (int d = 1; d <= 2; d++)
                {
                    img.Set(c + d, c + d, ArtStyle.FireRamp[0]);
                    img.Set(c - d, c - d, ArtStyle.FireRamp[0]);
                    img.Set(c + d, c - d, ArtStyle.FireRamp[0]);
                    img.Set(c - d, c + d, ArtStyle.FireRamp[0]);
                }
            return img.Done();
        }

        /// <summary>
        /// The Sniper's shot, 4 tiles long along +x, at <paramref name="t"/> (0 = fired, 1 = gone after 0.3 s): a bright, hard,
        /// 2 px line, white at the core, that dithers away from the muzzle end first.
        /// </summary>
        public static EffectFrame SniperTrail(float t)
        {
            var img = new Img(4 * T, 4);
            for (int x = 0; x < img.W; x++)
            {
                float along = (x + 0.5f) / img.W;
                for (int y = 0; y < 4; y++)
                {
                    bool core = y == 1 || y == 2;
                    if (!core && t > 0.3f) continue;
                    if (Bayer(x, y) < t * 1.4f - along * 0.4f) continue;
                    img.Set(x, y, core ? (t < 0.4f ? White : ArtStyle.FireRamp[0]) : ArtStyle.FireRamp[2]);
                }
            }
            return img.Done();
        }

        /// <summary>The Sniper's hit, frame <paramref name="frame"/> of 4: a bigger white burst than a bullet's, with long sparks and a puff.</summary>
        public static EffectFrame SniperHit(int frame)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(19, "sniper hit");
            int c = T / 2;
            float t = (frame + 1) / 4f;
            int core = frame == 0 ? 4 : frame == 1 ? 2 : 0;
            for (int dy = -core; dy <= core; dy++)
            for (int dx = -core; dx <= core; dx++)
                if (dx * dx + dy * dy <= core * core) img.Set(c + dx, c + dy, dx * dx + dy * dy <= core ? White : ArtStyle.FireRamp[0]);
            for (int k = 0; k < 11; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, reach = 6 + rng.Range(10);
                float r0 = reach * (t - 0.3f), r1 = reach * t * 1.5f;
                for (float r = Mathf.Max(0f, r0); r <= r1; r += 1f)
                    img.Set((int)(c + Mathf.Cos(a) * r), (int)(c + Mathf.Sin(a) * r), ArtStyle.FireRamp[Mathf.Min(6, frame + (r > reach ? 2 : 0))]);
            }
            return img.Done();
        }

        /// <summary>
        /// A hit that a Heavy's armour cuts to 1 (§7.2), frame <paramref name="frame"/> of 4: a ring of short sparks glancing off
        /// round the impact and a bright steel arc, so small arms read as bouncing off.
        /// </summary>
        public static EffectFrame ArmourSparks(int frame)
        {
            var img = new Img(T, T);
            int c = T / 2;
            float r = 5 + frame * 3.5f;
            Color32 steel = Role(StylePalette.Body3, 4);
            // The arc: the side of the ring facing the shot (from the left).
            if (frame < 3)
                for (int a = 110; a <= 250; a += 4)
                {
                    float rad = a * Mathf.Deg2Rad;
                    img.Set((int)(c + Mathf.Cos(rad) * r * 0.7f), (int)(c + Mathf.Sin(rad) * r * 0.7f), frame == 0 ? White : steel);
                }
            for (int k = 0; k < 8; k++)
            {
                float a = (k * 45 + 22) * Mathf.Deg2Rad;
                for (float d = r; d < r + 3 - frame * 0.5f; d += 1f)
                    if (Bayer(k, (int)d) < 1.1f - frame * 0.25f)
                        img.Set((int)(c + Mathf.Cos(a) * d), (int)(c + Mathf.Sin(a) * d), ArtStyle.FireRamp[Mathf.Min(6, frame + 1)]);
            }
            return img.Done();
        }

        // ---------- Artillery (§4, §8.2) ----------

        /// <summary>The bigger v0.20 shell in flight at <paramref name="angle"/>, with a flickering tail flame (frame 0 or 1).</summary>
        public static EffectFrame HeavyShell(float angle, int frame)
        {
            float back = 0.3f * T;
            var img = new Img(T, T);
            Burst(img, T / 2f - Mathf.Cos(angle) * back, T / 2f - Mathf.Sin(angle) * back, (frame == 0 ? 0.2f : 0.16f) * T, angle * Mathf.Rad2Deg + 180f);
            img.OutlineWith(Outline);
            Color32[] body = Finished(RenderGen.HeavyShellBody(angle)).Pixels;
            for (int i = 0; i < body.Length; i++)
                if (body[i].a == 255 || (body[i].a > 0 && img.Px[i].a == 0)) img.Px[i] = body[i];
            return img.Done();
        }

        /// <summary>
        /// The shell dropping nearly straight down for a client that sees only where it lands (§4.3), frame <paramref name="frame"/>
        /// of 2: the shell nose-down at the bottom of a 1×2-tile frame with dithered speed streaks above it, so no direction shows.
        /// </summary>
        public static EffectFrame IncomingShell(int frame)
        {
            var img = new Img(T, 2 * T);
            int cx = T / 2;
            for (int y = 18; y < img.H; y++)
            {
                float k = (y - 18f) / (img.H - 18f);
                foreach (int dx in new[] { -4, 0, 4 })
                {
                    if (dx != 0 && (y + frame * 3) % 7 < 3) continue;
                    if (Bayer(cx + dx, y) < k * 1.1f) continue;
                    img.Set(cx + dx, y, dx == 0 ? ArtStyle.ShellSmoke : Role(StylePalette.Body2, 2));
                }
            }
            Color32[] body = Finished(RenderGen.HeavyShellBody(-Mathf.PI / 2)).Pixels;
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                Color32 p = body[y * T + x];
                if (p.a > 0) img.Set(x, y - 10, p);
            }
            return img.Done();
        }

        /// <summary>The Artillery's launch: a big ring of muzzle smoke at <paramref name="t"/> (0 = fired, 1 = gone), two tiles square, drifting and thinning.</summary>
        public static EffectFrame LaunchPuff(float t, int seed)
        {
            int s = 2 * T;
            var img = new Img(s, s);
            float c = s / 2f, r = T * (0.2f + 0.55f * Mathf.Sqrt(t)), density = 1.15f - t;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                float n = Noise.Fbm(x, y, s, 4, 3, seed) - 0.5f;
                float k = (1f - Mathf.Abs(d - 0.7f) * 2.2f + n * 0.9f) * density * 1.5f;
                if (t < 0.15f && d < 0.5f) img.Set(x, y, ArtStyle.FireRamp[d < 0.25f ? 1 : 3]);
                else if (k > Bayer(x / 2, y / 2)) img.Set(x, y, n > 0.08f ? ArtStyle.ShellSmoke : Role(StylePalette.Body2, 1));
            }
            return img.Done();
        }

        /// <summary>The v0.20 shell's crater: about two tiles wide on a 3-tile canvas, otherwise as <see cref="Crater"/>.</summary>
        public static EffectFrame CraterLarge(int variant, int frame) => CraterSized(variant, frame, 3, T * (0.92f + 0.04f * variant), 14);

        // ---------- Gun turret (§6.3, §8.2) ----------

        /// <summary>
        /// The 2×2 Gun turret's twin muzzle flash along +x, centred between the barrels: both barrels flash every frame, the one
        /// firing larger (frame 0 the left, 1 the right) as the head alternates them. One tile, outlined.
        /// </summary>
        public static EffectFrame TwinMuzzle(int frame)
        {
            var img = new Img(T, T);
            float side = frame == 0 ? RenderGen.Turret2x2BarrelSide : -RenderGen.Turret2x2BarrelSide;
            Burst(img, T / 2f, T / 2f + side, 0.3f * T, 0f);
            Burst(img, T / 2f, T / 2f - side, 0.2f * T, 0f);
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>
        /// The Gun turret's fire at ten shots a second, <paramref name="length"/> px along +x (cut to the target's distance, never
        /// stretched, so the dashes keep their size and speed as the target closes): two streams of broken
        /// tracer dashes, one from each barrel (<see cref="RenderGen.Turret2x2BarrelSide"/> either side of the centre line, so
        /// the frame is centred between the barrels), half a beat apart and offset per frame (0–2) so they crawl.
        /// </summary>
        public static EffectFrame TracerStream(int frame, int length = 2 * T)
        {
            int gap = Mathf.RoundToInt(2 * RenderGen.Turret2x2BarrelSide);
            var img = new Img(Mathf.Max(1, length), gap + 4);
            for (int b = 0; b < 2; b++)
            {
                int lane = 1 + b * gap;
                for (int x = 0; x < img.W; x++)
                {
                    // Frames move the dashes toward +x (the target), each dash's hot head leading.
                    int phase = ((x - frame * 7 + b * 10) % 21 + 21) % 21;
                    if (phase > 11) continue;
                    Color32 col = phase >= 8 ? ArtStyle.FireRamp[0] : phase >= 4 ? ArtStyle.FireRamp[2] : ArtStyle.FireRamp[3];
                    img.Set(x, lane, col);
                    if (phase >= 6) img.Set(x, lane + 1, ArtStyle.FireRamp[3]);
                }
            }
            return img.Done();
        }

        // ---------- Deaths (§8.1) ----------

        /// <summary>A small unit's death (Scout, Tank, Builder, Digger, Flamethrower) at <paramref name="t"/>: a sharp pop with a shock ring, two tiles square.</summary>
        public static EffectFrame DeathSmall(float t, int seed) => ShockBlast(2, 0.75f, t, seed);

        /// <summary>
        /// A large unit's death (Heavy, Artillery, Sniper) at <paramref name="t"/>, four tiles square: a first blast, then a second
        /// one a beat later just off centre (the ammunition going up), each cooling into smoke.
        /// </summary>
        public static EffectFrame DeathLarge(float t, int seed)
        {
            int s = 4 * T;
            var img = new Img(s, s);
            void Stamp(EffectFrame f, int ox, int oy)
            {
                for (int y = 0; y < f.Height; y++)
                for (int x = 0; x < f.Width; x++)
                    if (f.Pixels[y * f.Width + x].a > 0) img.Set(ox + x, oy + y, f.Pixels[y * f.Width + x]);
            }
            EffectFrame first = ShockBlast(3, 1.2f, Mathf.Min(1f, t * 1.3f), seed);
            Stamp(first, (s - first.Width) / 2, (s - first.Height) / 2);
            if (t > 0.3f)
            {
                EffectFrame second = Blast(2, (t - 0.3f) / 0.7f, seed + 11);
                Stamp(second, s / 2 - second.Width / 2 + 18, s / 2 - second.Height / 2 + 10);
            }
            return img.Done();
        }

        /// <summary>
        /// Debris thrown by a blast death at <paramref name="t"/> (0 = thrown, 1 = landed), three tiles square: outlined chunks of
        /// plating and track flying out on arcs (higher mid-flight, drawn up the screen) and landing, with a little smoke behind them.
        /// </summary>
        public static EffectFrame Debris(float t, int seed)
        {
            int s = 3 * T;
            var img = new Img(s, s);
            var rng = new ArtRandom(seed, "debris");
            Color32[] cols = { Role(StylePalette.Body1), Role(StylePalette.Body2, 1), Role(StylePalette.Body3, 2), Role(StylePalette.Body0) };
            float c = s / 2f;
            for (int k = 0; k < 14; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, reach = T * (0.5f + rng.Range(100) / 100f);
                float d = reach * Mathf.Sqrt(t), lift = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t)) * (10 + rng.Range(14));
                int w = 2 + rng.Range(4), h = 2 + rng.Range(3);
                int x0 = (int)(c + Mathf.Cos(a) * d), y0 = (int)(c + Mathf.Sin(a) * d * 0.8f + lift);
                Color32 col = cols[rng.Range(cols.Length)];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    img.Set(x0 + x, y0 + y, y == h - 1 ? Role(StylePalette.Body3, 3) : col);
                if (t < 0.6f && Bayer(k, 3) < 0.7f - t) img.Set(x0 - (int)(Mathf.Cos(a) * 3), y0 - 2, Role(StylePalette.Body2, 1));
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>
        /// Dust from a collapsing building, <paramref name="tiles"/> plus one square, at <paramref name="t"/> (0 = starts, 1 = settled):
        /// a low, wide, tan cloud rolling out past the footprint and thinning.
        /// </summary>
        public static EffectFrame DustCloud(int tiles, float t, int seed)
        {
            int s = (tiles + 1) * T;
            var img = new Img(s, s);
            float c = s / 2f, rx = s * (0.28f + 0.2f * t), ry = rx * 0.75f, density = 1.2f - t;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = (x + 0.5f - c) / rx, dy = (y + 0.5f - c + t * 6f) / ry;
                float n = Noise.Fbm(x + t * 12f, y, s, 5, 3, seed) - 0.5f;
                float puff = (1f - Mathf.Sqrt(dx * dx + dy * dy) + n * 0.9f) * density * 1.5f;
                if (puff > Bayer(x / 2, y / 2)) img.Set(x, y, Role(n > 0.05f ? StylePalette.Floor2 : StylePalette.Floor1, n > 0.15f ? 3 : 2));
            }
            return img.Done();
        }
    }
}
