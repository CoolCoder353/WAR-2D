using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace WAR2D.Spike
{
    /// <summary>
    /// One unit's draw data, exactly the 32 bytes the shader's <c>StructuredBuffer&lt;UnitInstance&gt;</c>
    /// declares: <c>float2 position; float rotation; uint frame; uint color; float health; float scale; float pad</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UnitInstance
    {
        /// <summary>World position in tiles; the quad is centred here.</summary>
        public float2 position;

        /// <summary>Facing, radians (the shader rotates the quad by it).</summary>
        public float rotation;

        /// <summary>Atlas frame index into the renderer's <c>_FrameRects</c> buffer.</summary>
        public uint frame;

        /// <summary>RGBA packed as R | G &lt;&lt; 8 | B &lt;&lt; 16 | A &lt;&lt; 24, the shader's tint.</summary>
        public uint color;

        /// <summary>0 to 1; the health-bar draw uses it, units only carry it.</summary>
        public float health;

        /// <summary>1 for a small unit, 2 for a large one (multiplier on the material's unit size).</summary>
        public float scale;

        /// <summary>Unused padding that keeps the instance at 32 bytes.</summary>
        public float pad;
    }

    /// <summary>
    /// Draws a whole army as one GPU-instanced primitives call (plus one more for health bars), from a
    /// <c>StructuredBuffer</c> the CPU fills each frame. The spike's own shader
    /// (<c>SpikeInstancedUnit.shader</c>) is kept in a <c>Resources</c> folder, so the player build
    /// contains it.
    /// </summary>
    public sealed class InstancedUnitRenderer : IDisposable
    {
        /// <summary>Resources name of the shader (Assets/Spike/Render/Resources/SpikeInstancedUnit.shader).</summary>
        public const string ShaderName = "SpikeInstancedUnit";

        /// <summary>The instance stride the shader and the buffer must agree on.</summary>
        public const int InstanceStride = 32;

        /// <summary>One quad is two triangles.</summary>
        private const int QuadVertices = 6;

        /// <summary>The eight player tints the reference load uses, opaque and saturated enough to read at a few pixels.</summary>
        public static readonly Color32[] TeamColors =
        {
            new Color32(230, 60, 60, 255), new Color32(70, 110, 235, 255),
            new Color32(80, 200, 90, 255), new Color32(235, 210, 70, 255),
            new Color32(220, 90, 210, 255), new Color32(80, 215, 220, 255),
            new Color32(235, 140, 60, 255), new Color32(230, 230, 235, 255),
        };

        /// <summary>Packs a colour the way the shader unpacks it.</summary>
        public static uint Pack(Color32 c) => (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);

        /// <summary>Green at full health to red at none, the health bar's tint.</summary>
        public static uint BarColor(float health)
        {
            float h = math.saturate(health);
            return Pack(new Color32((byte)(255f * (1f - h)), (byte)(255f * h), 40, 255));
        }

        private readonly Material unitMaterial, barMaterial;
        private readonly GraphicsBuffer instances, barInstances, unitFrameRects, barFrameRects;
        private readonly int capacity, barCapacity;
        private bool capacityLogged;

        /// <summary>
        /// Loads the spike shader and allocates the instance buffers. An instance's <c>frame</c> maps to
        /// a uv rect in <paramref name="unitAtlas"/>; the spike uses the one-frame atlas
        /// <c>Resources/tank.png</c> with the rect (0, 0, 1, 1), because the game's unit sprites are
        /// whole images rather than an atlas pack. <paramref name="barAtlas"/> packs
        /// <paramref name="barFrames"/> health-bar lengths (frame 0 empty, the last full) that the bar
        /// draw picks from a damaged unit's health.
        /// </summary>
        public InstancedUnitRenderer(int capacity, int barCapacity, Texture2D unitAtlas, Texture2D barAtlas,
            int barFrames, float unitSize)
        {
            Shader shader = LoadShader();
            this.capacity = capacity;
            this.barCapacity = barCapacity;
            BarFrames = barFrames;

            unitMaterial = new Material(shader) { name = "Spike unit instances", renderQueue = 2000 };
            unitMaterial.SetFloat("_UnitSize", unitSize);
            unitMaterial.SetTexture("_MainTex", unitAtlas);
            barMaterial = new Material(shader) { name = "Spike health bars", renderQueue = 2001 };
            barMaterial.SetFloat("_UnitSize", 1f);
            barMaterial.SetTexture("_MainTex", barAtlas);

            instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, InstanceStride);
            barInstances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, barCapacity), InstanceStride);
            unitFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            unitFrameRects.SetData(new[] { new Vector4(0f, 0f, 1f, 1f) });
            barFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, barFrames, 16);
            barFrameRects.SetData(FrameRects(barFrames));

            unitMaterial.SetBuffer("_Instances", instances);
            unitMaterial.SetBuffer("_FrameRects", unitFrameRects);
            barMaterial.SetBuffer("_Instances", barInstances);
            barMaterial.SetBuffer("_FrameRects", barFrameRects);
        }

        /// <summary>Frames in the health-bar atlas.</summary>
        public int BarFrames { get; }

        /// <summary>Draw calls a units-plus-bars frame submits.</summary>
        public const int DrawsPerFrame = 2;

        /// <summary>The spike shader, loaded from Resources so the player build keeps it.</summary>
        public static Shader LoadShader()
        {
            var shader = Resources.Load<Shader>(ShaderName);
            if (shader == null) throw new InvalidOperationException($"Resources/{ShaderName}.shader is missing from the build");
            return shader;
        }

        /// <summary>One uv rect per atlas frame, frames laid out left to right.</summary>
        public static Vector4[] FrameRects(int frames)
        {
            var rects = new Vector4[frames];
            for (int i = 0; i < frames; i++) rects[i] = new Vector4((float)i / frames, 0f, 1f / frames, 1f);
            return rects;
        }

        /// <summary>
        /// Uploads <paramref name="count"/> instances and draws them as one instanced call. The data is
        /// copied whole: every unit moves every frame in this benchmark, so a partial upload would send
        /// the same bytes (the rendering row's fallback 1 is only worth walking when most instances are
        /// still).
        /// </summary>
        public void Draw(NativeArray<UnitInstance> data, int count)
        {
            if (count <= 0) return;
            if (count > capacity)
            {
                if (!capacityLogged)
                {
                    capacityLogged = true;
                    Debug.LogError($"[Render] {count} instances exceed the renderer's {capacity}");
                }
                count = capacity;
            }
            instances.SetData(data, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(unitMaterial) { worldBounds = WorldBounds },
                MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Draws the damaged units' health bars, compacted into their own buffer.</summary>
        public void DrawHealthBars(NativeArray<UnitInstance> bars, int count)
        {
            if (count <= 0) return;
            count = math.min(count, barCapacity);
            barInstances.SetData(bars, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(barMaterial) { worldBounds = WorldBounds },
                MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Big enough for any map the spike generates; the draws are not culled per instance.</summary>
        private static Bounds WorldBounds => new Bounds(Vector3.zero, Vector3.one * 100000f);

        public void Dispose()
        {
            instances.Dispose();
            barInstances.Dispose();
            unitFrameRects.Dispose();
            barFrameRects.Dispose();
            UnityEngine.Object.Destroy(unitMaterial);
            UnityEngine.Object.Destroy(barMaterial);
        }
    }

    /// <summary>Turns evaluated positions and the units' static look (tint, size, health) into instances.</summary>
    [BurstCompile]
    public struct PackUnitsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Rotations;
        [ReadOnly] public NativeArray<uint> Colors;
        [ReadOnly] public NativeArray<float> Scale;
        [ReadOnly] public NativeArray<float> Health;
        [WriteOnly] public NativeArray<UnitInstance> Instances;

        public void Execute(int i) => Instances[i] = new UnitInstance
        {
            position = Positions[i],
            rotation = Rotations[i],
            frame = 0,
            color = Colors[i],
            health = Health[i],
            scale = Scale[i],
            pad = 0f,
        };
    }

    /// <summary>
    /// Compacts the damaged units' health bars into their own buffer, so the bar draw is over 20 % of
    /// the army rather than all of it. The bar sits above the unit, and its atlas frame is the unit's
    /// health rounded to a frame, which is the bar's length.
    /// </summary>
    [BurstCompile]
    public struct PackHealthBarsJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Damaged;
        [ReadOnly] public NativeArray<float> Scale;

        /// <summary>Atlas frames; frame <c>n</c> is the bar at <c>n / (frames - 1)</c> health.</summary>
        public int BarFrames;

        /// <summary>Half the world size of a small unit's quad, so the bar clears it.</summary>
        public float UnitHalfSize;

        [WriteOnly] public NativeArray<UnitInstance> Bars;
        [WriteOnly] public NativeArray<int> Count;

        public void Execute()
        {
            int n = 0;
            for (int i = 0; i < Health.Length; i++)
            {
                if (Damaged[i] == 0) continue;
                float health = math.saturate(Health[i]);
                int frame = math.clamp((int)math.round(health * (BarFrames - 1)), 0, BarFrames - 1);
                Bars[n++] = new UnitInstance
                {
                    position = Positions[i] + new float2(0f, Scale[i] * UnitHalfSize + 0.175f),
                    rotation = 0f,
                    frame = (uint)frame,
                    color = InstancedUnitRenderer.BarColor(health),
                    health = health,
                    scale = 1f,
                    pad = 0f,
                };
            }
            Count[0] = n;
        }
    }
}
