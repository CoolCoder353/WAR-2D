using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// One unit's draw data, the 32 bytes the shader's <c>StructuredBuffer&lt;UnitInstance&gt;</c> declares:
    /// <c>float2 position; float rotation; uint frame; uint color; float health; float scale; float pad</c>.
    /// <c>pad</c> is the selection highlight (1 = selected).
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
    /// structured buffer filled each frame. The shader lives in <c>Resources/Shaders</c> so builds keep it.
    /// </summary>
    public sealed class InstancedUnitRenderer : IDisposable
    {
        public const string ShaderPath = "Shaders/InstancedUnit";
        public const int InstanceStride = 32;
        public const int BarFrames = 16;
        private const int QuadVertices = 6;

        /// <summary>Owner tints, indexed by the owner's slot in <c>GameCore.PlayerOrder</c>.</summary>
        public static readonly Color32[] TeamColors = PlayerPalette.Standard;

        public static uint Pack(Color32 c) => (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);

        /// <summary>Green at full health to red at none.</summary>
        public static uint BarColor(float health)
        {
            float h = math.saturate(health);
            return (uint)(255f * (1f - h)) | ((uint)(255f * h) << 8) | (40u << 16) | (255u << 24);
        }

        private readonly Material unitMaterial, barMaterial;
        private readonly Texture2D barAtlas;
        private GraphicsBuffer instances, barInstances;
        private readonly GraphicsBuffer unitFrameRects, barFrameRects;
        private int capacity, barCapacity;

        public InstancedUnitRenderer(Texture2D unitTexture, float unitSize, int initialCapacity = 4096)
        {
            Shader shader = Resources.Load<Shader>(ShaderPath);
            if (shader == null) throw new InvalidOperationException($"Resources/{ShaderPath}.shader is missing");
            barAtlas = BuildBarAtlas();
            unitMaterial = new Material(shader) { name = "Unit instances", renderQueue = 3000 };
            unitMaterial.SetFloat("_UnitSize", unitSize);
            unitMaterial.SetTexture("_MainTex", unitTexture != null ? unitTexture : Texture2D.whiteTexture);
            barMaterial = new Material(shader) { name = "Unit health bars", renderQueue = 3001 };
            barMaterial.SetFloat("_UnitSize", 0.8f);
            barMaterial.SetFloat("_Aspect", 0.15f);
            barMaterial.SetTexture("_MainTex", barAtlas);
            unitFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            unitFrameRects.SetData(new[] { new Vector4(0f, 0f, 1f, 1f) });
            barFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, BarFrames, 16);
            var rects = new Vector4[BarFrames];
            for (int i = 0; i < BarFrames; i++) rects[i] = new Vector4((float)i / BarFrames, 0f, 1f / BarFrames, 1f);
            barFrameRects.SetData(rects);
            unitMaterial.SetBuffer("_FrameRects", unitFrameRects);
            barMaterial.SetBuffer("_FrameRects", barFrameRects);
            EnsureCapacity(ref instances, ref capacity, initialCapacity, unitMaterial);
            EnsureCapacity(ref barInstances, ref barCapacity, initialCapacity / 4, barMaterial);
        }

        /// <summary>Health-bar frames: frame n is a bar n / (frames - 1) full, 16×2 pixels each.</summary>
        private static Texture2D BuildBarAtlas()
        {
            const int w = 16, h = 2;
            var tex = new Texture2D(w * BarFrames, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "HealthBars" };
            var pixels = new Color32[w * BarFrames * h];
            for (int f = 0; f < BarFrames; f++)
            {
                int filled = (int)math.round((float)f / (BarFrames - 1) * w);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    pixels[y * w * BarFrames + f * w + x] = x < filled ? new Color32(255, 255, 255, 255) : new Color32(20, 20, 20, 200);
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
            unitFrameRects.Dispose();
            barFrameRects.Dispose();
            UnityEngine.Object.Destroy(unitMaterial);
            UnityEngine.Object.Destroy(barMaterial);
            UnityEngine.Object.Destroy(barAtlas);
        }
    }

    /// <summary>Packs predicted units into instances, and the damaged ones' health bars into a second list.</summary>
    [BurstCompile]
    public struct PackUnitsJob : IJob
    {
        [ReadOnly] public NativeArray<int> Known;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Rotation;   // by index
        [ReadOnly] public NativeArray<uint> Color;       // by index
        [ReadOnly] public NativeArray<float> Health01;   // by index
        [ReadOnly] public NativeArray<byte> Selected;    // by index
        public float UnitHalfSize;
        public NativeArray<UnitInstance> Instances;
        public NativeArray<UnitInstance> Bars;
        public NativeArray<int> BarCount;

        public void Execute()
        {
            int bars = 0;
            for (int i = 0; i < Known.Length; i++)
            {
                int index = Known[i];
                float health = Health01[index];
                Instances[i] = new UnitInstance
                {
                    position = Positions[i],
                    rotation = Rotation[index],
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
        }
    }
}
