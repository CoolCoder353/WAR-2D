using System;
using UnityEngine;
using WAR2D.Art.Style;

namespace WAR2D.Art.Render
{
    /// <summary>A finished effect frame: pixels in <see cref="ArtStyle.EffectPalette"/> (team parts in the team key), y up.</summary>
    public readonly struct EffectFrame
    {
        public readonly Color32[] Pixels;
        public readonly int Width, Height;

        public EffectFrame(Color32[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Effect and overlay frames (plan v0.8 Task 11), drawn straight in palette colours: fire from
    /// <see cref="ArtStyle.FireRamp"/>, smoke and debris from the machine and rock greys, fades by ordered
    /// dithering (no partial alpha, so they stay pixel art). Deterministic.
    /// </summary>
    public static partial class EffectGen
    {
        private const int T = RenderGen.TileSize;

        private static Color32 Role(byte role, int shade = 2)
        {
            Color c = StylePalettes.Charcoal.Colours[role];
            float s = PixelFinish.Shades[shade];
            return new Color(Mathf.Clamp01(c.r * s), Mathf.Clamp01(c.g * s), Mathf.Clamp01(c.b * s), 1f);
        }

        private static Color32 Outline => StylePalettes.Charcoal.Colours[StylePalette.Outline];

        /// <summary>4×4 ordered-dither threshold in (0, 1) for an art pixel.</summary>
        public static float Bayer(int x, int y)
        {
            int[] m = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            return (m[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f;
        }

        private sealed class Img
        {
            public readonly Color32[] Px;
            public readonly int W, H;
            public Img(int w, int h) { W = w; H = h; Px = new Color32[w * h]; }
            public void Set(int x, int y, Color32 c) { if ((uint)x < (uint)W && (uint)y < (uint)H) Px[y * W + x] = c; }
            public Color32 Get(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? Px[y * W + x] : default;
            public EffectFrame Done() => new EffectFrame(Px, W, H);

            /// <summary>Every transparent pixel next to an opaque one becomes <paramref name="c"/>.</summary>
            public void OutlineWith(Color32 c)
            {
                var copy = (Color32[])Px.Clone();
                bool Opaque(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H && copy[y * W + x].a > 0;
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (!Opaque(x, y) && (Opaque(x - 1, y) || Opaque(x + 1, y) || Opaque(x, y - 1) || Opaque(x, y + 1))) Px[y * W + x] = c;
            }
        }

        /// <summary>
        /// A blast <paramref name="tiles"/> square at <paramref name="t"/> (0 = flash, 1 = last wisps): a
        /// noisy fireball cooling through the fire ramp, then dithered smoke and a few embers.
        /// </summary>
        public static EffectFrame Blast(int tiles, float t, int seed)
        {
            int s = tiles * T;
            var img = new Img(s, s);
            float c = s / 2f, fireR = s * (0.14f + 0.26f * Mathf.Sqrt(t)), smokeR = s * (0.22f + 0.24f * t);
            float heat = Mathf.Clamp01(1.15f - t * 1.6f), smoke = t < 0.15f ? 0f : Mathf.Clamp01(1.2f - t) * Mathf.Clamp01(t * 3f);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                float n = Noise.Fbm(x, y, s, 3, 3, seed) - 0.5f;
                float fire = (1f - d / fireR + n * 0.7f) * heat * 1.7f;
                if (fire > 0.12f)
                {
                    int i = fire > 1.1f ? 0 : fire > 0.9f ? 1 : fire > 0.72f ? 2 : fire > 0.55f ? 3 : fire > 0.4f ? 4 : fire > 0.26f ? 5 : 6;
                    img.Set(x, y, ArtStyle.FireRamp[i]);
                    continue;
                }
                float puff = (1f - d / smokeR + n * 0.9f) * smoke * 1.6f;
                if (puff > Bayer(x / 2, y / 2))
                    img.Set(x, y, Noise.Fbm(x, y, s, 4, 2, seed + 7) > 0.5f ? Role(StylePalette.Body2, 1) : Role(StylePalette.Body1, 1));
            }
            // Embers flung out while it burns down.
            if (t > 0.2f && t < 0.9f)
            {
                var rng = new ArtRandom(seed, "embers");
                for (int k = 0; k < 6 * tiles; k++)
                {
                    float a = rng.Range(360) * Mathf.Deg2Rad, r = s * (0.18f + 0.3f * t) * (0.7f + rng.Range(60) / 100f);
                    int ex = (int)(c + Mathf.Cos(a) * r), ey = (int)(c + Mathf.Sin(a) * r);
                    Color32 ember = ArtStyle.FireRamp[2 + Mathf.Min(4, (int)(t * 5))];
                    img.Set(ex, ey, ember);
                    img.Set(ex + 1, ey, ember);
                }
            }
            return img.Done();
        }

        /// <summary>A muzzle flash: a hot star, smaller in frame 1.</summary>
        public static EffectFrame MuzzleFlash(int frame)
        {
            var img = new Img(T, T);
            int c = T / 2, core = frame == 0 ? 3 : 2, ray = frame == 0 ? 10 : 6;
            for (int y = -core; y <= core; y++)
            for (int x = -core; x <= core; x++)
                if (x * x + y * y <= core * core) img.Set(c + x, c + y, ArtStyle.FireRamp[0]);
            for (int d = 0; d < 8; d++)
            {
                float a = d * Mathf.PI / 4f;
                int len = d % 2 == 0 ? ray : ray * 2 / 3;
                for (int r = core; r <= len; r++)
                    img.Set(c + Mathf.RoundToInt(Mathf.Cos(a) * r), c + Mathf.RoundToInt(Mathf.Sin(a) * r), ArtStyle.FireRamp[r < len * 2 / 3 ? 1 : 2]);
            }
            return img.Done();
        }

        /// <summary>
        /// A shot's streak along +x, heading right (turned to its heading when drawn, stretched along it), hard-edged as the sprites
        /// are: hot at the head, then amber, then orange breaking up in dither at the tail. No glow.
        /// </summary>
        private static EffectFrame Shot(int tiles, int width)
        {
            int w = tiles * T;
            var img = new Img(w, width);
            for (int x = 0; x < w; x++)
            {
                float t = (x + 0.5f) / w;
                int band = t > 0.62f ? 0 : t > 0.3f ? 2 : 3;
                for (int y = 0; y < width; y++)
                    if (t > 0.3f || Bayer(x, y) < t / 0.3f) img.Set(x, y, ArtStyle.FireRamp[band]);
            }
            return img.Done();
        }

        /// <summary>The Tank's and Heavy's shot, 2 px thick (and the Builder's, Digger's and Gun turret's).</summary>
        public static EffectFrame Tracer() => Shot(1, 2);

        /// <summary>The Scout's shot, 1 px thick.</summary>
        public static EffectFrame ScoutTracer() => Shot(1, 1);

        /// <summary>The Sniper's shot, 1 px thick and long.</summary>
        public static EffectFrame SniperTracer() => Shot(4, 1);

        /// <summary>Muzzle flash sizes: tiles from the centre to the tip of the tongue, per gun.</summary>
        public const float BurstScoutTiles = 0.22f, BurstSniperTiles = 0.26f, BurstTankTiles = 0.3f, BurstHeavyTiles = 0.34f;

        /// <summary>
        /// Paints a jagged burst pointing at <paramref name="forwardDegrees"/> into <paramref name="img"/>: a tongue and two short side
        /// jets, reaching <paramref name="r"/> pixels, in hard bands of the fire ramp.
        /// </summary>
        private static void Burst(Img img, float cx, float cy, float r, float forwardDegrees)
        {
            for (int y = 0; y < img.H; y++)
            for (int x = 0; x < img.W; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy, d = Mathf.Sqrt(dx * dx + dy * dy);
                float off = Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, forwardDegrees));
                float reach = off < 30f ? 1f - off / 34f : off < 62f && off > 34f ? 0.4f * (1f - Mathf.Abs(off - 48f) / 14f) : 0.14f;
                float v = d / (r * reach);
                if (v < 1f) img.Set(x, y, ArtStyle.FireRamp[v < 0.42f ? 0 : v < 0.72f ? 1 : 3]);
            }
        }

        /// <summary>A gun's muzzle flash pointing along +x (turned to the barrel when drawn), with a charcoal outline; smaller in frame 1.</summary>
        public static EffectFrame MuzzleBurst(float tiles, int frame)
        {
            var img = new Img(T, T);
            Burst(img, T / 2f, T / 2f, tiles * T * (frame == 0 ? 1f : 0.7f), 0f);
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>A lit canvas finished as the units' sprites are, without a team colour.</summary>
        private static EffectFrame Finished(LitCanvas c)
        {
            Color32[] lit = c.Render(Color.white, shadow: false);
            return new EffectFrame(PixelFinish.FinishTeam(lit, new bool[lit.Length], c.Width, c.Height, ArtStyle.Palette, ArtStyle.TeamKeyRamp, Outline), c.Width, c.Height);
        }

        /// <summary>
        /// The Artillery shell in flight, facing <paramref name="facing"/> of <see cref="ArtManifest.TurretDirections"/>: the
        /// lit body (<see cref="RenderGen.ArtilleryShellBody"/>) with a small flame at its tail that flickers between the two frames.
        /// </summary>
        public static EffectFrame ArtilleryShell(int facing, int frame)
        {
            float angle = facing * 2f * Mathf.PI / ArtManifest.TurretDirections, back = 0.24f * T;
            var img = new Img(T, T);
            Burst(img, T / 2f - Mathf.Cos(angle) * back, T / 2f - Mathf.Sin(angle) * back, (frame == 0 ? 0.15f : 0.12f) * T, angle * Mathf.Rad2Deg + 180f);
            img.OutlineWith(Outline);
            Color32[] body = Finished(RenderGen.ArtilleryShellBody(angle)).Pixels;
            for (int i = 0; i < body.Length; i++)
                if (body[i].a == 255 || (body[i].a > 0 && img.Px[i].a == 0)) img.Px[i] = body[i];
            return img.Done();
        }

        /// <summary>A dithered disc of <paramref name="tiles"/> radius in a tile, as the game's own smoke is drawn: thinner toward its edge and at low <paramref name="density"/>.</summary>
        private static EffectFrame Puff(float tiles, Color32 colour, float density)
        {
            var img = new Img(T, T);
            float c = T / 2f, r = tiles * T;
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float k = 1f - Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c)) / r;
                if (k > 0f && k * 1.6f * density > Bayer(x, y)) img.Set(x, y, colour);
            }
            return img.Done();
        }

        /// <summary>The shell's shadow on the ground, under its height in the air.</summary>
        public static EffectFrame ShellShadow() => Puff(0.26f, ArtStyle.Soot, 0.95f);

        /// <summary>A puff of the shell's trail at <paramref name="t"/> (0 = new, 1 = gone): small and dense, spreading as it thins.</summary>
        public static EffectFrame ShellPuff(float t) => Puff(0.08f + 0.1f * t, ArtStyle.ShellSmoke, 1f - 0.72f * t);

        /// <summary>Different craters drawn; each fades through <see cref="DecalFrames"/> frames.</summary>
        public const int CraterVariants = 3;

        /// <summary>
        /// What a shell leaves: about a tile wide on a 2-tile canvas, in hard bands (a pit, a lit and a shaded wall, a raised rim), with
        /// a dithered ring of soot and thrown stones. Fades by dithering, as the other ground marks do.
        /// </summary>
        public static EffectFrame Crater(int variant, int frame) => CraterSized(variant, frame, 2, T * (0.46f + 0.02f * variant), 8);

        /// <summary>A crater of radius <paramref name="r"/> px on a <paramref name="tiles"/>-square canvas, throwing <paramref name="stones"/> stones.</summary>
        private static EffectFrame CraterSized(int variant, int frame, int tiles, float r, int stones)
        {
            int s = tiles * T;
            var img = new Img(s, s);
            var rng = new ArtRandom(101 + variant, "crater");
            float c = s / 2f;
            var bump = new float[10];
            for (int i = 0; i < bump.Length; i++) bump[i] = 0.86f + 0.28f * rng.Range(100) / 100f;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                float at = (Mathf.Atan2(dy, dx) + Mathf.PI) / (2f * Mathf.PI) * bump.Length;
                int i0 = (int)at % bump.Length;
                float v = d / (r * Mathf.Lerp(bump[i0], bump[(i0 + 1) % bump.Length], at - Mathf.Floor(at)));
                bool towardLight = dx * -0.55f + dy * 0.6f > 0f; // the light comes from the upper left
                Color32 colour;
                if (v < 0.55f) colour = ArtStyle.CraterPit;
                else if (v < 0.8f) colour = towardLight ? ArtStyle.CraterWallDark : ArtStyle.CraterWallLit;
                else if (v < 1f) colour = towardLight ? ArtStyle.CraterRimLit : ArtStyle.CraterRimDark;
                else if (v < 1.75f && (1.75f - v) * 0.8f > Bayer(x, y)) colour = ArtStyle.Soot;
                else continue;
                if (Kept(x, y, frame)) img.Set(x, y, colour);
            }
            for (int k = 0; k < stones; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, dist = r * (1.15f + rng.Range(80) / 100f);
                int size = 2 + rng.Range(2);
                if (!Kept(k, k * 2, frame)) continue;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    img.Set((int)(c + Mathf.Cos(a) * dist) + x, (int)(c + Mathf.Sin(a) * dist) + y, y == size - 1 ? ArtStyle.StoneLit : ArtStyle.StoneDark);
            }
            return img.Done();
        }

        /// <summary>The effects drawn with partial alpha, off the palette: the flame's soft glow and the raised barrel's soft shadow. Everything else stays pixel art.</summary>
        public static bool IsSoft(string id) => id == "FlameStream" || id == "FlameSplash" || id == "BarrelShadow";

        private static readonly Color FlameHot = new Color(1f, 0.97f, 0.82f), FlameYellow = new Color(1f, 0.8f, 0.3f), FlameOrange = new Color(1f, 0.45f, 0.1f), FlameEmber = new Color(0.8f, 0.15f, 0.03f);

        /// <summary>
        /// A soft image built up as light added to the ground in display colours, then stored as the colour and alpha that give the same
        /// result over the dark ground when the game blends them in linear light (without this a faint glow comes out far too strong).
        /// </summary>
        private sealed class Glow
        {
            private readonly Color[] light;
            public readonly int W, H;
            public Glow(int w, int h) { W = w; H = h; light = new Color[w * h]; }

            public void Add(int x, int y, Color c, float alpha) => light[y * W + x] += c * alpha;

            /// <summary>A round blob, bright at its centre.</summary>
            public void Blob(float cx, float cy, float r, Color c, float alpha)
            {
                for (int y = Mathf.Max(0, (int)(cy - r)); y <= cy + r && y < H; y++)
                for (int x = Mathf.Max(0, (int)(cx - r)); x <= cx + r && x < W; x++)
                {
                    float k = 1f - Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) / r;
                    if (k > 0f) Add(x, y, c, alpha * k * k);
                }
            }

            /// <summary>Dims the light by <paramref name="keep"/> at each pixel (1 keeps it).</summary>
            public void Fade(Func<int, int, float> keep)
            {
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    light[y * W + x] *= Mathf.Clamp01(keep(x, y));
            }

            public EffectFrame Done()
            {
                const float ground = 0.16f;
                float under = Mathf.GammaToLinearSpace(ground);
                float Lit(float c) => Mathf.GammaToLinearSpace(Mathf.Clamp01(ground + c));
                var px = new Color32[W * H];
                for (int i = 0; i < px.Length; i++)
                {
                    Color c = light[i];
                    float a = Mathf.Clamp01((Lit(Mathf.Max(c.r, Mathf.Max(c.g, c.b))) - under) / (1f - under));
                    if (a * 255f < 1f) continue;
                    float Stored(float v) => Mathf.LinearToGammaSpace(Mathf.Clamp01((Lit(v) - under * (1f - a)) / a));
                    px[i] = new Color(Stored(c.r), Stored(c.g), Stored(c.b), a);
                }
                return new EffectFrame(px, W, H);
            }
        }

        /// <summary>
        /// The Artillery shell's impact (v0.12), 4 tiles square for its 2-tile splash: a white flash and a
        /// shockwave ring that runs out to the splash radius and fades, over a fireball that burns down into
        /// smoke (<see cref="Blast"/>) and a scorch of dithered soot.
        /// </summary>
        public static EffectFrame ShellImpact(float t, int seed)
        {
            EffectFrame blast = Blast(4, t * 0.85f, seed);
            var img = new Img(blast.Width, blast.Height);
            Array.Copy(blast.Pixels, img.Px, img.Px.Length);
            float c = img.W / 2f, ring = img.W * (0.12f + 0.38f * Mathf.Sqrt(t)), width = 3f - 2f * t;
            Color32 wave = t < 0.3f ? ArtStyle.FireRamp[1] : t < 0.6f ? ArtStyle.FireRamp[3] : Role(StylePalette.Body3, 2);
            for (int y = 0; y < img.H; y++)
            for (int x = 0; x < img.W; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                if (Mathf.Abs(d - ring) < width && Bayer(x, y) < 1.1f - t) img.Set(x, y, wave);
            }
            if (t < 0.12f)
                for (int y = -6; y <= 6; y++)
                for (int x = -6; x <= 6; x++)
                    if (x * x + y * y <= 36) img.Set((int)c + x, (int)c + y, new Color32(255, 255, 255, 255));
            return img.Done();
        }

        /// <summary>
        /// The Flamethrower's stream, 2 tiles long by 1, from the nozzle at the left edge to the target at the right, turned to its
        /// heading and stretched to its length when drawn: a white-hot core and an orange sheath that die away toward the target
        /// under flames that widen and cool. Nothing is drawn past the right edge, and it fades out on the way there.
        /// </summary>
        public static EffectFrame FlameStream(int frame)
        {
            var glow = new Glow(2 * T, T);
            var rng = new ArtRandom(31 + frame, "flame stream");
            float R() => rng.Range(1000) / 1000f;
            float mid = T / 2f, w = glow.W;
            void Streak(float width, Color head, float alpha)
            {
                for (int y = 0; y < glow.H; y++)
                for (int x = 0; x < glow.W; x++)
                {
                    float t = 1f - (x + 0.5f) / w; // 1 at the nozzle
                    float edge = Mathf.Clamp01(width * 0.5f * (0.35f + 0.65f * t) + 0.5f - Mathf.Abs(y + 0.5f - mid));
                    glow.Add(x, y, Color.Lerp(FlameOrange, head, Mathf.Clamp01(t * 1.6f)), alpha * edge * Mathf.Pow(t, 1.3f));
                }
            }
            Streak(11f, FlameOrange, 0.6f);
            Streak(5f, FlameHot, 0.8f);
            for (int i = 0; i < 150; i++)
            {
                float t = Mathf.Pow(R(), 0.75f);
                Color c = t < 0.22f ? Color.Lerp(FlameHot, FlameYellow, t / 0.22f) : t < 0.6f ? Color.Lerp(FlameYellow, FlameOrange, (t - 0.22f) / 0.38f) : Color.Lerp(FlameOrange, FlameEmber, (t - 0.6f) / 0.4f);
                glow.Blob(t * w, mid + (R() - 0.5f) * (0.08f + 0.5f * t) * T, (0.05f + 0.2f * t) * T, c, 0.3f);
            }
            glow.Fade((x, y) => (w - 1 - x) / 24f);
            return glow.Done();
        }

        /// <summary>Tiles of splash radius <see cref="FlameSplash"/> is drawn for: scaled to the real one when shown.</summary>
        public const float FlameSplashTiles = 1.5f;

        /// <summary>v0.20: the splash radius the atlas's FlameSplash (<see cref="FlameSplashSized"/>) is drawn for; the client scales it to the real one.</summary>
        public const float FlameSpreadArtTiles = 2.5f;

        /// <summary>
        /// Fire where a flame lands, 3 tiles square: soft blobs scattered over the splash circle, hot and strong at its centre and
        /// weak at its edge as the damage is, flickering between the frames. Nothing is drawn outside the circle.
        /// </summary>
        public static EffectFrame FlameSplash(int frame)
        {
            var glow = new Glow(3 * T, 3 * T);
            var rng = new ArtRandom(43 + frame, "flame splash");
            float R() => rng.Range(1000) / 1000f;
            float c = glow.W / 2f, radius = FlameSplashTiles * T;
            for (int i = 0; i < 26; i++)
            {
                float angle = R() * 2f * Mathf.PI, k = Mathf.Sqrt(R());
                glow.Blob(c + Mathf.Cos(angle) * k * radius * 0.94f, c + Mathf.Sin(angle) * k * radius * 0.94f, (0.1f + 0.1f * R()) * T, Color.Lerp(FlameYellow, FlameEmber, k), 0.55f * (1f - 0.5f * k));
            }
            glow.Fade((x, y) => (radius - Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c))) / 4f);
            return glow.Done();
        }

        /// <summary>
        /// A raised Artillery barrel's soft shadow on the ground, centred on the turret's pivot like its sprite: it runs from under the
        /// barrel's root to far down-right of its muzzle, because the muzzle stands high.
        /// </summary>
        public static EffectFrame BarrelShadow(int facing)
        {
            int s = 3 * T;
            var img = new Img(s, s);
            float angle = facing * 2f * Mathf.PI / ArtManifest.TurretDirections, pivot = RenderGen.TurretPivot(UnitType.Artillery);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), o = new Vector2(s / 2f, s / 2f);
            Vector2 a = o + dir * (8f - pivot) + new Vector2(4.8f, -5.1f), b = o + dir * (40f - pivot) + new Vector2(24f, -21.8f), d = b - a;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - a;
                float t = Mathf.Clamp01(Vector2.Dot(p, d) / d.sqrMagnitude), half = 4f * (0.8f + 0.4f * t);
                float alpha = 0.55f * Mathf.Clamp01((half + 1.5f - (p - d * t).magnitude) / 3f);
                if (alpha > 0f) img.Set(x, y, new Color32(ArtStyle.Soot.r, ArtStyle.Soot.g, ArtStyle.Soot.b, (byte)(alpha * 255f)));
            }
            return img.Done();
        }

        /// <summary>
        /// The Gun turret's muzzle flash (v0.13), 3 frames, from the tile's centre along +x (drawn at the muzzle,
        /// turned to the head's facing): a hot tongue with side jets from the muzzle brake, a smaller one, then a
        /// puff of dithered smoke with a last spark.
        /// </summary>
        public static EffectFrame TurretMuzzle(int frame)
        {
            var img = new Img(T, T);
            int c = T / 2;
            if (frame < 2)
            {
                float len = frame == 0 ? 18f : 11f, width = frame == 0 ? 5f : 3.2f;
                for (int y = -7; y <= 7; y++)
                for (int x = 0; x <= (int)len; x++)
                {
                    float along = x / len, half = width * Mathf.Sin(Mathf.PI * Mathf.Min(1f, along * 1.3f + 0.12f));
                    float d = Mathf.Abs(y) / Mathf.Max(0.5f, half);
                    if (d > 1f) continue;
                    img.Set(c + x, c + y, d < 0.45f && along < 0.7f ? ArtStyle.FireRamp[0] : d < 0.8f ? ArtStyle.FireRamp[1] : ArtStyle.FireRamp[2]);
                }
                // Side jets from the brake.
                int jet = frame == 0 ? 7 : 4;
                foreach (int s in new[] { -1, 1 })
                    for (int k = 1; k <= jet; k++)
                    {
                        Color32 col = k < jet - 1 ? ArtStyle.FireRamp[1] : ArtStyle.FireRamp[3];
                        img.Set(c + 1 + k / 3, c + s * (2 + k), col);
                        if (frame == 0 && k < jet - 1) img.Set(c + 2 + k / 3, c + s * (2 + k), ArtStyle.FireRamp[2]);
                    }
                return img.Done();
            }
            for (int y = -8; y <= 8; y++)
            for (int x = -1; x <= 18; x++)
            {
                float dx = (x - 8) / 9.5f, dy = y / 6.5f;
                float puff = 1f - (dx * dx + dy * dy) + (Noise.Fbm(x, y, T, 4, 2, 61) - 0.5f) * 0.8f;
                if (puff > Bayer(c + x, c + y) * 1.4f)
                    img.Set(c + x, c + y, Noise.Fbm(x, y, T, 6, 2, 67) > 0.5f ? Role(StylePalette.Body3, 2) : Role(StylePalette.Body2, 2));
            }
            img.Set(c + 9, c + 2, ArtStyle.FireRamp[3]);
            img.Set(c + 4, c - 3, ArtStyle.FireRamp[4]);
            return img.Done();
        }

        /// <summary>
        /// The Jammer's pulse (v0.13), 3 tiles square (drawn over the 2×2 Jammer, centred on it) at
        /// <paramref name="t"/> (0 = start, 1 = gone): two rings of static running out from the dish, their
        /// bright edges broken by noise, each with a dithered wake that thins as it spreads.
        /// </summary>
        public static EffectFrame JamPulse(float t)
        {
            int s = 3 * T;
            var img = new Img(s, s);
            // The dish (RenderGen.Jammer, centre 6 px east and 14 px north of the footprint's), lifted by its rim height.
            float cx = s / 2f + 6, cy = s / 2f + 14 + 26 * RenderGen.Lift;
            float maxR = s - cy - 4;
            Color32 edge = Role(StylePalette.Lamp, t < 0.5f ? 3 : 2), wake = Role(StylePalette.Body3, 3);
            void Ring(float ring, float strength, int seed)
            {
                if (ring < 6 || strength <= 0) return;
                for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy, d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > ring + 1f || d < ring - 12f) continue;
                    float n = Noise.Fbm(Mathf.Atan2(dy, dx) * 40f + 128f, d * 0.6f, 256, 16, 2, seed);
                    if (d > ring - 1.5f)
                    {
                        if (n > 0.62f - 0.35f * strength) img.Set(x, y, edge);
                    }
                    else if ((d - (ring - 12f)) / 10.5f * strength > Bayer(x, y) * 1.2f && n > 0.45f) img.Set(x, y, wake);
                }
            }
            Ring(10f + (maxR - 10f) * t, 1f - t * 0.75f, 71);
            Ring(10f + (maxR - 10f) * (t - 0.35f), (1f - t) * 0.8f, 73);
            return img.Done();
        }

        /// <summary>The ring under a selected unit: four light arcs with a dark edge.</summary>
        public static EffectFrame SelectionRingSmall()
        {
            var img = new Img(T, T);
            float c = T / 2f;
            Color32 ring = Role(StylePalette.Lamp);
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Repeat(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, 90f);
                if (d >= 26.5f && d <= 28.5f && a > 12f && a < 78f) img.Set(x, y, ring);
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>
        /// The ring under a roaming unit (Roam, J; Figma "Playtest fixes" item 11): twelve short gold dashes with a dark edge, a
        /// little outside the selection ring so both read together.
        /// </summary>
        public static EffectFrame RoamRing()
        {
            var img = new Img(T, T);
            float c = T / 2f;
            Color32 ring = Role(StylePalette.Warn);
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Repeat(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, 30f);
                if (d >= 29.5f && d <= 31f && a > 8f && a < 22f) img.Set(x, y, ring);
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>Corner brackets for a selected building, drawn 9-sliced (borders <see cref="RingBorder"/>) over its footprint.</summary>
        public static EffectFrame SelectionRingLarge()
        {
            int s = 2 * T;
            var img = new Img(s, s);
            Color32 ring = Role(StylePalette.Lamp);
            const int inset = 3, arm = 16, w = 2;
            for (int i = 0; i < arm; i++)
            for (int k = 0; k < w; k++)
            foreach (var (x0, y0, sx, sy) in new[] { (inset, inset, 1, 1), (s - 1 - inset, inset, -1, 1), (inset, s - 1 - inset, 1, -1), (s - 1 - inset, s - 1 - inset, -1, -1) })
            {
                img.Set(x0 + sx * i, y0 + sy * k, ring);
                img.Set(x0 + sx * k, y0 + sy * i, ring);
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>The 9-slice border of <see cref="SelectionRingLarge"/> in art pixels (the brackets fit inside it).</summary>
        public const int RingBorder = 24;

        /// <summary>A flag on a pole; <paramref name="team"/> draws the cloth in the team key (rally flags).</summary>
        public static EffectFrame Flag(int frame, bool team)
        {
            var img = new Img(T, T);
            int px = 26, bottom = 14, top = 46;
            for (int y = bottom; y <= top; y++) { img.Set(px, y, Role(StylePalette.Body3, 3)); img.Set(px + 1, y, Role(StylePalette.Body3, 2)); }
            for (int x = px - 2; x <= px + 3; x++) img.Set(x, bottom - 1, Role(StylePalette.Body1));
            for (int x = 0; x < 16; x++)
            {
                int wave = Mathf.RoundToInt(Mathf.Sin((x + frame * 4) * 0.45f) * 1.5f);
                int h = 10 - x * 6 / 16;
                for (int y = 0; y < h; y++)
                {
                    // Lit top edge, shaded bottom edge (Lamp shade 1 isn't in the palette: too close to a player grey).
                    int step = y < 2 ? 3 : y > h - 3 ? 0 : 2;
                    img.Set(px + 2 + x, top - 1 - y + wave, team ? ArtStyle.TeamKeyRamp[step + 1] : Role(StylePalette.Lamp, step));
                }
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>Rock chunks flung out of a dug tile.</summary>
        public static EffectFrame DigDebris(int frame)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(3, "debris");
            float t = (frame + 1) / 4f;
            for (int k = 0; k < 9; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, r = 6 + t * (14 + rng.Range(10));
                int x = (int)(T / 2 + Mathf.Cos(a) * r), y = (int)(T / 2 + Mathf.Sin(a) * r - t * t * 6);
                int size = 1 + rng.Range(2);
                for (int j = 0; j <= size; j++)
                for (int i = 0; i <= size; i++)
                    img.Set(x + i, y + j, Role(i + j == 0 ? StylePalette.Rock3 : StylePalette.Rock1, 2));
            }
            img.OutlineWith(Outline);
            return img.Done();
        }

        /// <summary>Welding sparks for repairs.</summary>
        public static EffectFrame RepairSparks(int frame)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(5 + frame, "sparks");
            for (int k = 0; k < 10; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, r = 3 + rng.Range(18);
                int x = (int)(T / 2 + Mathf.Cos(a) * r), y = (int)(T / 2 + Mathf.Sin(a) * r);
                img.Set(x, y, ArtStyle.FireRamp[r < 8 ? 0 : r < 14 ? 1 : 2]);
            }
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                img.Set(T / 2 + x, T / 2 + y, ArtStyle.FireRamp[0]);
            return img.Done();
        }

        /// <summary>Diagonal hazard stripes, half dithered, scrolling with the frame (tiles seamlessly).</summary>
        public static EffectFrame ConstructionOverlay(int frame)
        {
            var img = new Img(T, T);
            Color32 warn = Role(StylePalette.Warn, 1), dark = Role(StylePalette.Body0);
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                if (Bayer(x, y) > 0.5f) continue;
                img.Set(x, y, ((x + y + frame * 4) / 8 & 1) == 0 ? warn : dark);
            }
            return img.Done();
        }

        /// <summary>Health bar parts (frame 0 the back, frame 1 the white fill), <c>48×4</c> art pixels centred in a tile.</summary>
        public static EffectFrame HealthBar(int frame)
        {
            var img = new Img(T, T);
            const int w = 48, h = 4;
            int x0 = (T - w) / 2, y0 = (T - h) / 2;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                if (frame == 0) img.Set(x0 + x, y0 + y, edge ? Outline : Role(StylePalette.Body0));
                else if (!edge) img.Set(x0 + x, y0 + y, new Color32(255, 255, 255, 255));
            }
            return img.Done();
        }

        /// <summary>Frames a ground mark (scorch, rubble, tracks) fades through: frame 0 whole, the last a quarter left.</summary>
        public const int DecalFrames = 4;

        /// <summary>True when a ground mark keeps the pixel at (x, y) in fade frame <paramref name="frame"/> (dithered away a quarter at a time).</summary>
        private static bool Kept(int x, int y, int frame) => Bayer(x, y) < 1f - frame / (float)DecalFrames;

        /// <summary>
        /// The soot left where a unit died (polish item 3), <paramref name="tiles"/> square, fading over <see cref="DecalFrames"/>
        /// frames: a dark blotch with a ragged dithered rim and a few charred flecks flung out.
        /// </summary>
        public static EffectFrame Scorch(int tiles, int frame, int seed)
        {
            int s = tiles * T;
            var img = new Img(s, s);
            float c = s / 2f, r = s * 0.3f;
            Color32 soot = Role(StylePalette.Body0), ash = Role(StylePalette.Body1, 1);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - c, dy = (y + 0.5f - c) * 1.25f; // flattened: the ground in the 3/4 view
                float d = Mathf.Sqrt(dx * dx + dy * dy) / r + (Noise.Fbm(x, y, s, 4, 3, seed) - 0.5f) * 0.6f;
                if (d > 1.15f) continue;
                bool rim = d > 0.75f;
                if (rim && Bayer(x, y) > (1.15f - d) * 2.2f) continue;
                if (!Kept(x, y, frame)) continue;
                img.Set(x, y, d < 0.45f || Noise.Fbm(x, y, s, 3, 2, seed + 5) > 0.62f ? soot : ash);
            }
            var rng = new ArtRandom(seed, "scorch flecks");
            for (int k = 0; k < 5 * tiles; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, d = r * (1.1f + rng.Range(40) / 100f);
                int x = (int)(c + Mathf.Cos(a) * d), y = (int)(c + Mathf.Sin(a) * d * 0.8f);
                if (Kept(x, y, frame)) img.Set(x, y, soot);
            }
            return img.Done();
        }

        /// <summary>
        /// What a destroyed building leaves over its footprint (polish item 3), <paramref name="tiles"/> square, fading over
        /// <see cref="DecalFrames"/> frames: a soot patch strewn with outlined chunks of rock and machine plating.
        /// </summary>
        public static EffectFrame Rubble(int tiles, int frame, int seed)
        {
            int s = tiles * T;
            EffectFrame soot = Scorch(tiles, 0, seed);
            var img = new Img(s, s);
            // A thinner soot bed than a unit's scorch, so the chunks read on it.
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                if (soot.Pixels[y * s + x].a > 0 && Bayer(x + 1, y + 2) < 0.6f) img.Set(x, y, soot.Pixels[y * s + x]);
            var chunks = new Img(s, s);
            var rng = new ArtRandom(seed, "rubble");
            Color32[] stone = { Role(StylePalette.Rock1, 2), Role(StylePalette.Rock3, 2), Role(StylePalette.Body1), Role(StylePalette.Body2, 1) };
            for (int k = 0; k < 7 * tiles * tiles; k++)
            {
                int w = 3 + rng.Range(4 + tiles), h = 2 + rng.Range(3 + tiles);
                int x0 = rng.Range(s - w - 4) + 2, y0 = rng.Range(s - h - 4) + 2;
                Color32 col = stone[rng.Range(stone.Length)];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if ((x + y) < w + h - 3) chunks.Set(x0 + x, y0 + y, y == h - 1 ? Role(StylePalette.Rock3, 2) : col); // lit top edge, cut corner
            }
            chunks.OutlineWith(Outline);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                if (chunks.Px[y * s + x].a > 0) img.Set(x, y, chunks.Px[y * s + x]);
            for (int i = 0; i < img.Px.Length; i++)
                if (img.Px[i].a > 0 && !Kept(i % s, i / s, frame)) img.Px[i] = default;
            return img.Done();
        }

        /// <summary>Facings drawn for track marks: 45° apart through a half turn (a mark looks the same driven either way).</summary>
        public const int TrackDirections = 4;

        /// <summary>
        /// Tread marks (polish item G), half a tile long, for <paramref name="direction"/> (0 = east–west, then 45° steps
        /// anticlockwise), fading over <see cref="DecalFrames"/> frames: two solid tread bands crossed by lighter cleats. Decals draws
        /// direction 0 turned to the vehicle's exact heading.
        /// </summary>
        public static EffectFrame Tracks(int direction, int frame)
        {
            var img = new Img(T, T);
            float a = direction * Mathf.PI / 4f, cos = Mathf.Cos(a), sin = Mathf.Sin(a), c = T / 2f;
            Color32 rut = Role(StylePalette.Body0), edge = Role(StylePalette.Body1, 1);
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float px = x + 0.5f - c, py = y + 0.5f - c;
                float along = px * cos + py * sin, across = -px * sin + py * cos;
                if (Mathf.Abs(along) > 16f) continue;
                float band = Mathf.Abs(Mathf.Abs(across) - 10f);
                if (band > 3f) continue;
                // Solid bands, wide enough to survive the mipmaps at medium zoom (playtest 2026-10-30: the old dithered ones
                // looked noisy up close and vanished further out); the cleats are lighter cross-bars, not holes.
                if (!Kept(x, y, frame)) continue;
                bool cleat = Mathf.Repeat(along, 6f) < 2f;
                img.Set(x, y, band < 2f && !cleat ? rut : edge);
            }
            return img.Done();
        }

        /// <summary>
        /// A puff of smoke from a damaged building (polish item C) at <paramref name="t"/> (0 = new, 1 = gone): a dithered grey
        /// ball that grows and thins as it rises (the drawing drifts it upward).
        /// </summary>
        public static EffectFrame Smoke(float t, int seed)
        {
            var img = new Img(T, T);
            float c = T / 2f, r = T * (0.12f + 0.16f * t), density = 1.1f - t * 0.95f;
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                float n = Noise.Fbm(x, y + t * 20f, T, 4, 2, seed) - 0.5f;
                float puff = (1f - Mathf.Sqrt(dx * dx + dy * dy) / r + n * 0.8f) * density * 1.5f;
                if (puff > Bayer(x / 2, y / 2))
                    img.Set(x, y, n > 0.08f ? Role(StylePalette.Body3, 1) : n > -0.12f ? Role(StylePalette.Body2, 1) : Role(StylePalette.Body1, 1));
            }
            return img.Done();
        }

        /// <summary>
        /// A tongue of flame on a badly damaged building (polish item C), frame <paramref name="frame"/> of 4: it licks up, peaks,
        /// gutters and leaves a wisp.
        /// </summary>
        public static EffectFrame Fire(int frame)
        {
            var img = new Img(T, T);
            float[] height = { 12f, 20f, 15f, 7f }, width = { 5f, 6.5f, 5.5f, 3.5f };
            float h = height[frame % 4], w = width[frame % 4], cx = T / 2f, by = T / 2f - 8f;
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float up = (y + 0.5f - by) / h;
                if (up < 0f || up > 1f) continue;
                float sway = Mathf.Sin(up * 3f + frame * 1.7f) * 1.5f * up;
                float half = w * (1f - up * up) + (Noise.Fbm(x, y, T, 4, 2, 81 + frame) - 0.5f) * 3f;
                float d = Mathf.Abs(x + 0.5f - cx - sway) / Mathf.Max(0.5f, half);
                if (d > 1f) continue;
                float heat = (1f - d) * (1.2f - up);
                int i = heat > 0.85f ? 0 : heat > 0.6f ? 1 : heat > 0.4f ? 2 : heat > 0.22f ? 3 : 4;
                img.Set(x, y, ArtStyle.FireRamp[Mathf.Min(6, i + (frame == 3 ? 2 : 0))]);
            }
            return img.Done();
        }

        /// <summary>
        /// A glint off the gem tile a Miner works (polish item I), frame <paramref name="frame"/> of 4: a four-pointed star that
        /// flares and shrinks, with two smaller sparks.
        /// </summary>
        public static EffectFrame GemSparkle(int frame)
        {
            var img = new Img(T, T);
            int[] arm = { 3, 7, 5, 2 };
            Color32 white = new Color32(255, 255, 255, 255), pale = ArtStyle.FireRamp[0], lamp = Role(StylePalette.Lamp, 3);
            void Star(int cx, int cy, int len)
            {
                img.Set(cx, cy, white);
                for (int k = 1; k <= len; k++)
                {
                    Color32 col = k <= len / 2 ? pale : lamp;
                    img.Set(cx + k, cy, col); img.Set(cx - k, cy, col);
                    img.Set(cx, cy + k, col); img.Set(cx, cy - k, col);
                }
            }
            Star(T / 2, T / 2 + 2, arm[frame % 4]);
            if (frame >= 1) Star(T / 2 - 9, T / 2 - 6, arm[(frame + 2) % 4] / 2);
            if (frame >= 2) Star(T / 2 + 8, T / 2 - 3, 1);
            return img.Done();
        }

        /// <summary>
        /// A bullet striking its target (polish item A), frame <paramref name="frame"/> of 3: a white-hot point with short sparks
        /// flying out, which cool and scatter.
        /// </summary>
        public static EffectFrame HitSpark(int frame)
        {
            var img = new Img(T, T);
            var rng = new ArtRandom(13, "hitspark");
            int c = T / 2;
            float t = (frame + 1) / 3f;
            if (frame == 0)
            {
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    img.Set(c + dx, c + dy, dx == 0 && dy == 0 ? new Color32(255, 255, 255, 255) : ArtStyle.FireRamp[0]);
            }
            for (int k = 0; k < 7; k++)
            {
                float a = rng.Range(360) * Mathf.Deg2Rad, reach = 3 + rng.Range(5);
                float r0 = reach * (t - 0.34f), r1 = reach * t * 1.6f;
                for (float r = Mathf.Max(0f, r0); r <= r1; r += 1f)
                    img.Set((int)(c + Mathf.Cos(a) * r), (int)(c + Mathf.Sin(a) * r), ArtStyle.FireRamp[Mathf.Min(6, frame * 2 + (r > reach ? 1 : 0))]);
            }
            return img.Done();
        }

        /// <summary>
        /// A puff of ground dust at <paramref name="t"/> (0 = new, 1 = gone): where a unit is made (polish item E), and ringed
        /// around a building as it is finished (item J). A dithered tan ball, low and wide, that spreads and thins.
        /// </summary>
        public static EffectFrame DustPuff(float t, int seed)
        {
            var img = new Img(T, T);
            float c = T / 2f, rx = T * (0.16f + 0.24f * t), ry = rx * 0.7f, density = 1.15f - t;
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float dx = (x + 0.5f - c) / rx, dy = (y + 0.5f - c + t * 3f) / ry;
                float n = Noise.Fbm(x + t * 9f, y, T, 4, 2, seed) - 0.5f;
                float puff = (1f - Mathf.Sqrt(dx * dx + dy * dy) + n * 0.7f) * density * 1.6f;
                if (puff > Bayer(x / 2, y / 2))
                    img.Set(x, y, Role(n > 0.05f ? StylePalette.Floor2 : StylePalette.Floor1, n > 0.15f ? 3 : 2));
            }
            return img.Done();
        }

        /// <summary>A frame centred on a larger transparent canvas of <paramref name="w"/> × <paramref name="h"/> (whole tiles for the atlas).</summary>
        private static EffectFrame Padded(EffectFrame f, int w, int h)
        {
            if (f.Width == w && f.Height == h) return f;
            var px = new Color32[w * h];
            int ox = (w - f.Width) / 2, oy = (h - f.Height) / 2;
            for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                int tx = x + ox, ty = y + oy;
                if ((uint)tx < (uint)w && (uint)ty < (uint)h) px[ty * w + tx] = f.Pixels[y * f.Width + x];
            }
            return new EffectFrame(px, w, h);
        }

        /// <summary>The frame for cell <paramref name="cell"/> of an effect id, or null when it has no generator.</summary>
        public static EffectFrame? For(string id, int cell, int frames)
        {
            float t = frames > 1 ? (float)cell / (frames - 1) : 0f;
            switch (id)
            {
                case "Explosion": return Blast(2, t, 11);
                case "BombBlast": return Blast(3, t, 23);
                case "MuzzleFlash": return MuzzleFlash(cell);
                case "BurstScout": return MuzzleBurst(BurstScoutTiles, cell);
                case "BurstSniper": return MuzzleBurst(BurstSniperTiles, cell);
                case "BurstTank": return MuzzleBurst(BurstTankTiles, cell);
                case "BurstHeavy": return MuzzleBurst(BurstHeavyTiles, cell);
                case "Tracer": return Tracer();
                case "ScoutTracer": return ScoutTracer();
                case "SniperTracer": return SniperTrail(t); // v0.20
                case "ArtilleryShell": return HeavyShell((cell / frames) * 2f * Mathf.PI / 16f, cell % frames); // v0.20: the larger shell
                case "ShellShadow": return ShellShadow();
                case "ShellPuff": return ShellPuff(t);
                case "Crater": return Crater(cell / frames, cell % frames); // variant-major, like units
                case "FlameSplash": return FlameSplashSized(cell, 2.5f); // v0.20: over the 2.5-tile spread
                case "BarrelShadow": return BarrelShadow(cell);
                case "ShellImpact": return ShellImpact(t, 37);
                case "FlameStream": return FlameStream(cell);
                case "BomberBlast": return Blast(4, t, 29);
                case "TurretMuzzle": return TurretMuzzle(cell);
                case "JamPulse": return JamPulse(t);
                case "SelectionRingSmall": return SelectionRingSmall();
                case "SelectionRingLarge": return SelectionRingLarge();
                case "RoamRing": return RoamRing();
                case "WaypointFlag": return Flag(cell, false);
                case "RallyFlag": return Flag(cell, true);
                case "DigDebris": return DigDebris(cell);
                case "RepairSparks": return RepairSparks(cell);
                case "ConstructionOverlay": return ConstructionOverlay(cell);
                case "HealthBar": return HealthBar(cell);
                case "Scorch": return Scorch(1, cell, 41);
                case "ScorchLarge": return Scorch(2, cell, 43);
                case "Rubble1": return Rubble(1, cell, 47);
                case "Rubble2": return Rubble(2, cell, 53);
                case "Rubble3": return Rubble(3, cell, 59);
                case "Rubble5": return Rubble(5, cell, 61);
                case "Tracks": return Tracks(cell / frames, cell % frames); // direction-major, like units
                case "Smoke": return Smoke(t, 67);
                case "Fire": return Fire(cell);
                case "GemSparkle": return GemSparkle(cell);
                case "HitSpark": return HitSpark(cell);
                case "DustPuff": return DustPuff(t, 71);
                // v0.20 Onslaught (EffectGen.Onslaught), approved 2026-11-06.
                case "LeapSparks": return LeapSparks(t, 7);
                case "LeapShadow": return LeapShadow(t);
                case "BomberLanding": return ShockBlast(5, 2f, t, 29);
                case "BurnFlames1": return Padded(BurnFlames(1, cell), T, 2 * T);
                case "BurnFlames2": return Padded(BurnFlames(2, cell), 2 * T, 3 * T);
                case "BurnFlames3": return Padded(BurnFlames(3, cell), 3 * T, 4 * T);
                case "SootSmoke": return SootSmoke(t, 3);
                case "FlameScorch": return FlameScorch(cell, 9);
                case "AimLaser": return AimLaser(t);
                case "MuzzleGlint": return Padded(MuzzleGlint(cell), T, T);
                case "SniperHit": return SniperHit(cell);
                case "ArmourSparks": return ArmourSparks(cell);
                case "IncomingShell": return IncomingShell(cell);
                case "LaunchPuff": return LaunchPuff(t, 5);
                case "ArtilleryBlast": return ShockBlast(8, 3.5f, t, 37, dust: true);
                case "CraterLarge": return CraterLarge(cell / frames, cell % frames);
                case "TwinMuzzle": return TwinMuzzle(cell);
                case "TracerStream": return TracerStream(cell);
                case "DeathSmall": return DeathSmall(t, 41);
                case "DeathLarge": return DeathLarge(t, 43);
                case "Debris": return Debris(t, 47);
                case "DustCloud": return DustCloud(2, t, 53);
                default: return null;
            }
        }
    }
}
