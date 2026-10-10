using System;
using UnityEngine;

namespace WAR2D.Art.Render
{
    /// <summary>A surface for <see cref="LitCanvas"/> shapes: base colour (or team colour), shininess and glow.</summary>
    public readonly struct Material
    {
        public readonly Color Albedo;
        /// <summary>Specular strength (0 matte rock to ~0.8 polished metal) and its sharpness.</summary>
        public readonly float Specular, Gloss;
        /// <summary>True for team-coloured parts: lit as white and tinted by the owner's colour (and written to the team mask).</summary>
        public readonly bool Team;
        /// <summary>Light emitted regardless of lighting (lamps, gems).</summary>
        public readonly float Glow;

        public Material(Color albedo, float specular = 0.25f, float gloss = 24f, bool team = false, float glow = 0f)
        {
            Albedo = albedo;
            Specular = specular;
            Gloss = gloss;
            Team = team;
            Glow = glow;
        }
    }

    /// <summary>
    /// A small procedural renderer for sprites in the owner's chosen look (2026-10-11: "Factorio combined
    /// with the current style"): shapes are signed-distance functions with bevelled height profiles and a
    /// material; panel grooves, rivets and grime noise are added on top; <see cref="Render"/> lights the
    /// height field from the top left (diffuse, specular, ambient occlusion), adds a drop shadow, and
    /// downsamples the supersampled buffer. Coordinates are in output pixels, y up (as Unity textures).
    /// Deterministic: no randomness besides the seeded <see cref="Noise"/>.
    /// </summary>
    public sealed class LitCanvas
    {
        /// <summary>Render pixels per output pixel on each axis.</summary>
        public const int Supersample = 2;

        public readonly int Width, Height; // output pixels
        /// <summary>
        /// Output pixels from the bottom to the footprint's centre (where the sprite is anchored on the map).
        /// Buildings drawn with <c>lift</c> are taller than their footprint, so their canvas has headroom above it.
        /// </summary>
        public readonly float PivotY;
        private readonly int rw, rh;       // render pixels
        private readonly float[] height, alpha, spec, gloss, glow, team;
        private readonly Color[] albedo;

        /// <summary>Light direction (toward the light): from the top left and above.</summary>
        public static readonly Vector3 LightDir = new Vector3(-0.55f, 0.6f, 0.9f).normalized;

        public LitCanvas(int width, int height) : this(width, height, height / 2f) { }

        public LitCanvas(int width, int height, float pivotY)
        {
            Width = width;
            Height = height;
            PivotY = pivotY;
            rw = width * Supersample;
            rh = height * Supersample;
            int n = rw * rh;
            this.height = new float[n];
            alpha = new float[n];
            spec = new float[n];
            gloss = new float[n];
            glow = new float[n];
            team = new float[n];
            albedo = new Color[n];
        }

        /// <summary>
        /// Draws a shape: <paramref name="sdf"/> (output pixels, negative inside) gets a flat top at
        /// <paramref name="top"/> with its rim bevelled down by <paramref name="bevelDrop"/> over
        /// <paramref name="bevel"/> pixels. Later shapes cover earlier ones. With <paramref name="stack"/> the
        /// height adds to what's underneath (a plate on a hull) instead of replacing it.
        /// </summary>
        public void Shape(Func<float, float, float> sdf, Material m, float top, float bevel = 1.5f, float bevelDrop = 2f, bool stack = false)
        {
            float s = 1f / Supersample;
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                float d = sdf((x + 0.5f) * s, (y + 0.5f) * s);
                if (d > s) continue;
                int i = y * rw + x;
                float coverage = Mathf.Clamp01(0.5f - d / s);
                float inner = Mathf.Clamp01(-d / Mathf.Max(bevel, 1e-3f));
                float h = top - bevelDrop * (1 - Mathf.SmoothStep(0, 1, inner));
                alpha[i] = Mathf.Max(alpha[i], coverage);
                if (coverage < 0.5f) continue;
                height[i] = stack ? height[i] + Mathf.Max(0, h) : h;
                albedo[i] = m.Albedo;
                spec[i] = m.Specular;
                gloss[i] = m.Gloss;
                glow[i] = m.Glow;
                team[i] = m.Team ? 1 : 0;
            }
        }

        /// <summary>A groove along the zero line of <paramref name="sdf"/>: pressed in by <paramref name="depth"/> over <paramref name="width"/> pixels, darkened.</summary>
        public void Groove(Func<float, float, float> sdf, float depth = 1.2f, float width = 0.8f)
        {
            float s = 1f / Supersample;
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (alpha[i] < 0.5f) continue;
                float d = Mathf.Abs(sdf((x + 0.5f) * s, (y + 0.5f) * s));
                if (d >= width) continue;
                float k = 1 - d / width;
                height[i] -= depth * k;
                albedo[i] *= 1 - 0.25f * k;
            }
        }

        /// <summary>A round bump (rivet, bolt head) of radius <paramref name="r"/> raised by <paramref name="h"/>.</summary>
        public void Rivet(float cx, float cy, float r = 0.9f, float h = 0.9f, float shine = 0.3f)
        {
            float s = 1f / Supersample;
            for (int y = (int)((cy - r - 1) * Supersample); y <= (int)((cy + r + 1) * Supersample); y++)
            for (int x = (int)((cx - r - 1) * Supersample); x <= (int)((cx + r + 1) * Supersample); x++)
            {
                if ((uint)x >= (uint)rw || (uint)y >= (uint)rh) continue;
                int i = y * rw + x;
                if (alpha[i] < 0.5f) continue;
                float dx = (x + 0.5f) * s - cx, dy = (y + 0.5f) * s - cy, q = 1 - (dx * dx + dy * dy) / (r * r);
                if (q <= 0) continue;
                height[i] += h * Mathf.Sqrt(q);
                spec[i] = Mathf.Max(spec[i], shine);
            }
        }

        /// <summary>
        /// Surface wear over everything drawn so far: height jitter and colour mottling from tiling noise
        /// (<paramref name="cells"/> lattice cells across the canvas width).
        /// </summary>
        public void Grime(int seed, float bump = 0.35f, float mottle = 0.18f, int cells = 6, int octaves = 4)
        {
            float s = 1f / Supersample;
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (alpha[i] <= 0) continue;
                float px = (x + 0.5f) * s, py = (y + 0.5f) * s;
                float n = Noise.Fbm(px, py, Width, cells, octaves, seed);
                float m = Noise.Fbm(px, py, Width, cells * 2, 3, seed + 7);
                height[i] += (n - 0.5f) * 2 * bump;
                albedo[i] *= 1 + (m - 0.5f) * 2 * mottle;
            }
        }

        /// <summary>Fills the whole canvas as opaque ground at height 0 (terrain tiles).</summary>
        public void Ground(Material m) => Shape((x, y) => -1000f, m, 0f, 0f, 0f);

        /// <summary>Raises (or lowers) the height field by a function of position, where drawn.</summary>
        public void Displace(Func<float, float, float> dh)
        {
            float s = 1f / Supersample;
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (alpha[i] > 0) height[i] += dh((x + 0.5f) * s, (y + 0.5f) * s);
            }
        }

        /// <summary>Recolours drawn pixels by a function of position and current colour.</summary>
        public void Tint(Func<float, float, Color, Color> f)
        {
            float s = 1f / Supersample;
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (alpha[i] > 0) albedo[i] = f((x + 0.5f) * s, (y + 0.5f) * s, albedo[i]);
            }
        }

        /// <summary>
        /// Lights the canvas and downsamples it. Team parts are tinted with <paramref name="teamColour"/>.
        /// <paramref name="shadow"/> adds a soft drop shadow away from the light (for sprites drawn over the
        /// ground; terrain tiles pass false). Returns output-resolution colours, y up.
        /// </summary>
        public Color32[] Render(Color teamColour, bool shadow = true, float ambient = 0.42f, float heightScale = 1f, float lift = 0f)
        {
            var lit = new Color[rw * rh];
            var wall = lift > 0 ? new Color[rw * rh] : null;
            float[] blurred = BoxBlur(height, rw, rh, 5 * Supersample);
            Vector3 view = Vector3.forward;
            Vector3 half = (LightDir + view).normalized;
            float k = heightScale * Supersample * 0.5f;
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (alpha[i] <= 0) continue;
                float hx = (H(x + 1, y) - H(x - 1, y)) * k, hy = (H(x, y + 1) - H(x, y - 1)) * k;
                var n = new Vector3(-hx, -hy, 1f).normalized;
                float diffuse = Mathf.Max(0, Vector3.Dot(n, LightDir));
                float ao = Mathf.Clamp(1f - (blurred[i] - height[i]) * 0.09f, 0.45f, 1f);
                float specular = spec[i] * Mathf.Pow(Mathf.Max(0, Vector3.Dot(n, half)), gloss[i] > 0 ? gloss[i] : 16f);
                Color baseColour = team[i] > 0 ? teamColour * (0.55f + 0.45f * albedo[i].grayscale) : albedo[i];
                Color c = baseColour * (ambient * ao + diffuse * (1 - ambient) * Mathf.Lerp(0.85f, 1f, ao)) + Color.white * specular * ao * 0.6f;
                c += baseColour * glow[i];
                c.a = alpha[i];
                lit[i] = c;
                if (wall != null)
                {
                    // South-facing walls: the light comes from the north-west, so they get ambient and a little bounce.
                    Color wc = baseColour * (ambient * 0.8f + 0.06f) + baseColour * glow[i] * 0.5f;
                    wc.a = alpha[i];
                    wall[i] = wc;
                }
            }

            if (shadow)
            {
                // A soft shadow cast down-right, under the sprite.
                const float reach = 3.5f;
                int ox = (int)(reach * Supersample), oy = -(int)(reach * Supersample);
                float[] shade = BoxBlur(alpha, rw, rh, 2 * Supersample);
                for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int sx = x - ox, sy = y - oy;
                    if ((uint)sx >= (uint)rw || (uint)sy >= (uint)rh) continue;
                    int i = y * rw + x;
                    float a = shade[sy * rw + sx] * 0.5f;
                    if (a <= 0) continue;
                    Color c = lit[i];
                    float over = c.a;
                    lit[i] = new Color(c.r * over, c.g * over, c.b * over, over + a * (1 - over));
                    if (lit[i].a > 0) lit[i] = new Color(lit[i].r / lit[i].a, lit[i].g / lit[i].a, lit[i].b / lit[i].a, lit[i].a);
                }
            }

            if (wall != null) lit = Raise(lit, wall, lift);

            // Downsample (box) with premultiplied alpha.
            var output = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < Supersample; sy++)
                for (int sx = 0; sx < Supersample; sx++)
                {
                    Color c = lit[(y * Supersample + sy) * rw + x * Supersample + sx];
                    r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                }
                float n = Supersample * Supersample;
                output[y * Width + x] = a <= 0 ? new Color32(0, 0, 0, 0)
                    : (Color32)new Color(Mathf.Clamp01(r / a), Mathf.Clamp01(g / a), Mathf.Clamp01(b / a), Mathf.Clamp01(a / n));
            }
            return output;
        }

        /// <summary>
        /// The 3/4 view: every pixel rises up the screen by its height × <paramref name="lift"/>, and the gap it
        /// leaves below becomes its south wall (darker toward the ground). Far rows are drawn first, so nearer
        /// parts cover what's behind them.
        /// </summary>
        private Color[] Raise(Color[] top, Color[] wall, float lift)
        {
            var output = new Color[top.Length];
            for (int i = 0; i < top.Length; i++)
                if (height[i] <= 0.01f && top[i].a > 0) output[i] = top[i]; // the ground and anything flat stays put
            for (int y = rh - 1; y >= 0; y--)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                if (top[i].a <= 0 || height[i] <= 0.01f) continue;
                int rise = Mathf.RoundToInt(height[i] * lift * Supersample);
                for (int k = 0; k <= rise; k++)
                {
                    int yy = y + k;
                    if (yy >= rh) break;
                    Color c;
                    if (k == rise) c = top[i];
                    else
                    {
                        float shade = 0.75f + 0.25f * k / Mathf.Max(1, rise);
                        // Glass (very shiny) facades get floors of windows: lit bands between dark sills.
                        if (spec[i] >= 0.85f) shade *= (k / (2 * Supersample)) % 3 == 0 ? 0.55f : 1.6f;
                        c = wall[i] * shade;
                        c.a = wall[i].a;
                    }
                    output[yy * rw + x] = c;
                }
            }
            return output;
        }

        private float H(int x, int y)
        {
            x = Mathf.Clamp(x, 0, rw - 1);
            y = Mathf.Clamp(y, 0, rh - 1);
            return height[y * rw + x];
        }

        private static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            var tmp = new float[src.Length];
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                float sum = 0;
                int count = 0;
                for (int x = -r; x < w + r; x++)
                {
                    if (x + r < w) { sum += src[y * w + Mathf.Clamp(x + r, 0, w - 1)]; count++; }
                    if (x - r - 1 >= 0) { sum -= src[y * w + x - r - 1]; count--; }
                    if (x >= 0 && x < w) tmp[y * w + x] = sum / Mathf.Max(1, count);
                }
            }
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                int count = 0;
                for (int y = -r; y < h + r; y++)
                {
                    if (y + r < h) { sum += tmp[Mathf.Clamp(y + r, 0, h - 1) * w + x]; count++; }
                    if (y - r - 1 >= 0) { sum -= tmp[(y - r - 1) * w + x]; count--; }
                    if (y >= 0 && y < h) dst[y * w + x] = sum / Mathf.Max(1, count);
                }
            }
            return dst;
        }
    }
}
