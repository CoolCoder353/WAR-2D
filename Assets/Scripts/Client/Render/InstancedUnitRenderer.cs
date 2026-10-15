using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Style;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// One unit's draw data, the 32 bytes the shader's <c>StructuredBuffer&lt;UnitInstance&gt;</c> declares:
    /// <c>float2 position; float rotation; uint frame; uint color; float health; float scale; float pad</c>.
    /// <c>frame</c> is the atlas cell (<see cref="UnitFrames"/>: the facing picks a rendered direction, the
    /// shader doesn't rotate the quad), <c>color</c> the owner's colour for the team mask, and <c>pad</c> the
    /// selection highlight (1 = selected).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UnitInstance
    {
        public float2 position;
        public float rotation;
        public uint frame;
        public uint color;
        public float health;
        public float scale;
        public float pad;
    }

    /// <summary>
    /// Draws every unit as one GPU-instanced call (plus one for the damaged units' health bars) from a
    /// structured buffer filled each frame, with frames from the <c>Units</c> atlas and its team mask. The
    /// shader lives in <c>Resources/Shaders</c> so builds keep it.
    /// </summary>
    public sealed class InstancedUnitRenderer : IDisposable
    {
        public const string ShaderPath = "Shaders/InstancedUnit";
        public const int InstanceStride = 32;
        public const int BarFrames = 16;

        /// <summary>Health bar size in art pixels: drawn 1:1 at <see cref="ArtStyle.PixelsPerUnit"/>.</summary>
        public const int BarWidth = 48, BarHeight = 4;
        private const int QuadVertices = 6;

        /// <summary>Owner tints, indexed by the owner's slot in <c>GameCore.PlayerOrder</c>.</summary>

        public static uint Pack(Color32 c) => (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);

        /// <summary>Green at full health to red at none.</summary>
        public static uint BarColor(float health)
        {
            float h = math.saturate(health);
            return (uint)(255f * (1f - h)) | ((uint)(255f * h) << 8) | (40u << 16) | (255u << 24);
        }

        private readonly Material unitMaterial, barMaterial, ringMaterial;
        private readonly Texture2D barAtlas;
        private GraphicsBuffer instances, barInstances, ringInstances;
        private readonly GraphicsBuffer unitFrameRects, barFrameRects, ringFrameRects;
        private int capacity, barCapacity, ringCapacity;

        /// <summary>The selection ring's cell in the <c>Effects</c> atlas (0 without one).</summary>
        public uint RingFrame { get; }

        /// <param name="atlas">The <c>Units</c> atlas; without one, every unit draws as a white square.</param>
        public InstancedUnitRenderer(ArtAtlas atlas, int initialCapacity = 4096)
        {
            Shader shader = Resources.Load<Shader>(ShaderPath);
            if (shader == null) throw new InvalidOperationException($"Resources/{ShaderPath}.shader is missing");
            barAtlas = BuildBarAtlas();
            unitMaterial = new Material(shader) { name = "Unit instances", renderQueue = 3000 };
            // Unit frames are one tile square (64 px at PPU 64).
            unitMaterial.SetFloat("_UnitSize", (float)ArtStyle.TileSize / ArtStyle.PixelsPerUnit);
            unitMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            unitMaterial.SetFloat("_TeamGain", TeamGain());
            unitMaterial.SetTexture("_MainTex", atlas != null ? atlas.Texture : Texture2D.whiteTexture);
            unitMaterial.SetTexture("_MaskTex", atlas != null && atlas.Mask != null ? atlas.Mask : Texture2D.blackTexture);
            barMaterial = new Material(shader) { name = "Unit health bars", renderQueue = 3001 };
            barMaterial.SetFloat("_UnitSize", (float)BarWidth / ArtStyle.PixelsPerUnit);
            barMaterial.SetFloat("_Aspect", (float)BarHeight / BarWidth);
            barMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            barMaterial.SetFloat("_TeamGain", 1f);
            barMaterial.SetTexture("_MainTex", barAtlas);
            barMaterial.SetTexture("_MaskTex", Texture2D.whiteTexture); // the whole bar takes the bar colour
            Vector4[] cells = UvRects(atlas);
            unitFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cells.Length, 16);
            unitFrameRects.SetData(cells);
            barFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, BarFrames, 16);
            var rects = new Vector4[BarFrames];
            for (int i = 0; i < BarFrames; i++) rects[i] = new Vector4((float)i / BarFrames, 0f, 1f / BarFrames, 1f);
            barFrameRects.SetData(rects);
            unitMaterial.SetBuffer("_FrameRects", unitFrameRects);
            barMaterial.SetBuffer("_FrameRects", barFrameRects);

            // Selection rings: the Effects atlas, drawn just under the units.
            ArtAtlas effects = ArtAtlas.Load(ArtAtlas.Effects);
            ringMaterial = new Material(shader) { name = "Selection rings", renderQueue = 2999 };
            ringMaterial.SetFloat("_UnitSize", (float)ArtStyle.TileSize / ArtStyle.PixelsPerUnit);
            ringMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            ringMaterial.SetFloat("_TeamGain", 1f);
            ringMaterial.SetTexture("_MainTex", effects != null ? effects.Texture : Texture2D.whiteTexture);
            ringMaterial.SetTexture("_MaskTex", Texture2D.blackTexture);
            Vector4[] ringCells = UvRects(effects);
            ringFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, ringCells.Length, 16);
            ringFrameRects.SetData(ringCells);
            ringMaterial.SetBuffer("_FrameRects", ringFrameRects);
            RingFrame = (uint)Mathf.Max(0, effects != null ? effects.FirstFrame("SelectionRingSmall") : 0);

            EnsureCapacity(ref instances, ref capacity, initialCapacity, unitMaterial);
            EnsureCapacity(ref barInstances, ref barCapacity, initialCapacity / 4, barMaterial);
            EnsureCapacity(ref ringInstances, ref ringCapacity, 1024, ringMaterial);
        }

        /// <summary>
        /// The multiplier that turns the middle team grey back into the owner's colour: the grey is sampled
        /// as sRGB, so in a linear project its value is the linear one.
        /// </summary>
        public static float TeamGain()
        {
            float grey = ArtStyle.TeamGrey[ArtStyle.TeamGrey.Length / 2].r / 255f;
            return 1f / (QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.GammaToLinearSpace(grey) : grey);
        }

        /// <summary>Every atlas cell's rect in UV space (the whole texture when there is no atlas).</summary>
        public static Vector4[] UvRects(ArtAtlas atlas)
        {
            if (atlas == null || atlas.FrameRects.Length == 0) return new[] { new Vector4(0f, 0f, 1f, 1f) };
            float w = atlas.Data.width, h = atlas.Data.height;
            var uv = new Vector4[atlas.FrameRects.Length];
            for (int i = 0; i < uv.Length; i++)
            {
                Vector4 r = atlas.FrameRects[i];
                uv[i] = new Vector4(r.x / w, r.y / h, r.z / w, r.w / h);
            }
            return uv;
        }

        /// <summary>
        /// Health-bar frames: frame n is a bar n / (frames - 1) full, <see cref="BarWidth"/> × <see cref="BarHeight"/>
        /// pixels with a 1 px outline in the palette's outline colour. The fill is white (tinted by the bar colour),
        /// the empty part the darkest machine colour.
        /// </summary>
        public static Texture2D BuildBarAtlas()
        {
            const int w = BarWidth, h = BarHeight, inner = w - 2;
            var tex = new Texture2D(w * BarFrames, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "HealthBars" };
            var pixels = new Color32[w * BarFrames * h];
            Color32 outline = StylePalettes.Charcoal.Colours[StylePalette.Outline];
            Color32 empty = StylePalettes.Charcoal.Colours[StylePalette.Body0];
            // The shader tints the whole bar by the bar colour; the outline and the empty part stay dark under it.
            for (int f = 0; f < BarFrames; f++)
            {
                int filled = (int)math.round((float)f / (BarFrames - 1) * inner);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                    pixels[y * w * BarFrames + f * w + x] = edge ? outline : x - 1 < filled ? new Color32(255, 255, 255, 255) : empty;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static void EnsureCapacity(ref GraphicsBuffer buffer, ref int capacity, int needed, Material material)
        {
            if (buffer != null && capacity >= needed) return;
            buffer?.Dispose();
            capacity = math.max(1024, math.ceilpow2(needed));
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, InstanceStride);
            material.SetBuffer("_Instances", buffer);
        }

        /// <summary>Uploads and draws <paramref name="count"/> unit instances.</summary>
        public void Draw(NativeArray<UnitInstance> data, int count)
        {
            if (count <= 0) return;
            EnsureCapacity(ref instances, ref capacity, count, unitMaterial);
            instances.SetData(data, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(unitMaterial) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Uploads and draws the selected units' rings (before the units, which draw over them).</summary>
        public void DrawRings(NativeArray<UnitInstance> rings, int count)
        {
            if (count <= 0) return;
            EnsureCapacity(ref ringInstances, ref ringCapacity, count, ringMaterial);
            ringInstances.SetData(rings, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(ringMaterial) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Uploads and draws the damaged units' health bars.</summary>
        public void DrawHealthBars(NativeArray<UnitInstance> bars, int count)
        {
            if (count <= 0) return;
            EnsureCapacity(ref barInstances, ref barCapacity, count, barMaterial);
            barInstances.SetData(bars, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(barMaterial) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        private static Bounds WorldBounds => new Bounds(Vector3.zero, Vector3.one * 100000f);

        public void Dispose()
        {
            instances?.Dispose();
            barInstances?.Dispose();
            ringInstances?.Dispose();
            unitFrameRects.Dispose();
            barFrameRects.Dispose();
            ringFrameRects.Dispose();
            UnityEngine.Object.Destroy(unitMaterial);
            UnityEngine.Object.Destroy(barMaterial);
            UnityEngine.Object.Destroy(ringMaterial);
            UnityEngine.Object.Destroy(barAtlas);
        }
    }

    /// <summary>Packs predicted units into instances, the damaged ones' health bars and the selected ones' rings into two more lists.</summary>
    [BurstCompile]
    public struct PackUnitsJob : IJob
    {
        [ReadOnly] public NativeArray<int> Known;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Rotation;   // by index
        [ReadOnly] public NativeArray<uint> Frame;       // by index: the atlas cell
        [ReadOnly] public NativeArray<uint> Color;       // by index
        [ReadOnly] public NativeArray<float> Health01;   // by index
        [ReadOnly] public NativeArray<byte> Selected;    // by index
        public float UnitHalfSize;
        public uint RingFrame;
        public NativeArray<UnitInstance> Instances;
        public NativeArray<UnitInstance> Bars;
        public NativeArray<int> BarCount;
        public NativeArray<UnitInstance> Rings;
        public NativeArray<int> RingCount;

        public void Execute()
        {
            int bars = 0, rings = 0;
            for (int i = 0; i < Known.Length; i++)
            {
                int index = Known[i];
                float health = Health01[index];
                if (Selected[index] != 0)
                    Rings[rings++] = new UnitInstance { position = Positions[i], frame = RingFrame, color = 0xFFFFFFFFu, scale = 1f };
                Instances[i] = new UnitInstance
                {
                    position = Positions[i],
                    rotation = Rotation[index],
                    frame = Frame[index],
                    color = Color[index],
                    health = health,
                    scale = 1f,
                    pad = Selected[index],
                };
                if (health >= 0.999f) continue;
                int frame = math.clamp((int)math.round(health * (InstancedUnitRenderer.BarFrames - 1)), 0, InstancedUnitRenderer.BarFrames - 1);
                Bars[bars++] = new UnitInstance
                {
                    position = Positions[i] + new float2(0f, UnitHalfSize + 0.175f),
                    frame = (uint)frame,
                    color = InstancedUnitRenderer.BarColor(health),
                    health = health,
                    scale = 1f,
                };
            }
            BarCount[0] = bars;
            RingCount[0] = rings;
        }
    }
}
