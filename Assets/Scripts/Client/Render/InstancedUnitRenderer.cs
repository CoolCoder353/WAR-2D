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
    /// One unit's draw data, the 40 bytes the shader's <c>StructuredBuffer&lt;UnitInstance&gt;</c> declares:
    /// <c>float2 position; float rotation; uint frame; uint color; float health; float scale; float pad; float2 velocity</c>.
    /// <c>frame</c> is the atlas cell (<see cref="UnitFrames"/>: the facing picks a rendered direction, the
    /// shader doesn't rotate the quad), <c>color</c> the owner's colour for the team mask, <c>pad</c> bit 1 selected and <c>rotation</c>
    /// the time of the unit's last hit (the shader flashes it). A health bar's <c>rotation</c> is its recent-damage trail at the last hit
    /// and its <c>pad</c> that hit's time (<see cref="HitFeedback"/>: the shader drains the trail). <c>velocity</c>
    /// (tiles a second) carries the instance on from <c>position</c> by the time since it was packed (<see cref="InstancedUnitRenderer.DrawAheadProperty"/>).
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
        public float2 velocity;
    }

    /// <summary>
    /// Draws every unit as one GPU-instanced call (plus one for the damaged units' health bars) from a
    /// structured buffer filled each frame, with frames from the <c>Units</c> atlas and its team mask. The
    /// shader lives in <c>Resources/Shaders</c> so builds keep it.
    /// </summary>
    public sealed class InstancedUnitRenderer : IDisposable
    {
        public const string ShaderPath = "Shaders/InstancedUnit";
        public const int InstanceStride = 40;

        /// <summary>
        /// The global shader float of seconds since the units were last packed: each instance is drawn that far along its velocity, so
        /// units move smoothly on the frames between packs (<see cref="Redraw"/>).
        /// </summary>
        public static readonly int DrawAheadProperty = Shader.PropertyToID("_UnitDrawAhead");

        /// <summary>The global shader float of this frame's <c>Time.time</c>, the clock hits are stamped on (flash and trail).</summary>
        public static readonly int TimeProperty = Shader.PropertyToID("_UnitTime");

        private int unitCount, turretCount, ringCount, barCount;
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

        private readonly Material unitMaterial, barMaterial, ringMaterial, turretMaterial;
        private readonly Texture2D barAtlas;
        private GraphicsBuffer instances, barInstances, ringInstances, turretInstances;
        private readonly GraphicsBuffer unitFrameRects, barFrameRects, ringFrameRects;
        private int capacity, barCapacity, ringCapacity, turretCapacity;

        /// <summary>The selection ring's cell in the <c>Effects</c> atlas (0 without one).</summary>
        public uint RingFrame { get; }

        /// <summary>The 2×2-tile selection ring's cell, for the large units (the small ring's when the atlas lacks it).</summary>
        public uint LargeRingFrame { get; }

        /// <summary>The cell of the dashed ring under a roaming unit (<c>RoamRing</c>), in the ring material's cells.</summary>
        public uint RoamRingFrame { get; }

        /// <summary>The first cell of the raised barrel's shadow (<c>BarrelShadow</c>, one per turret facing), in the ring material's cells: it lies under the units too.</summary>
        public uint BarrelShadowFrame { get; }

        /// <param name="atlas">The <c>Units</c> atlas; without one, every unit draws as a white square.</param>
        public InstancedUnitRenderer(ArtAtlas atlas, int initialCapacity = 4096)
        {
            Shader shader = Resources.Load<Shader>(ShaderPath);
            if (shader == null) throw new InvalidOperationException($"Resources/{ShaderPath}.shader is missing");
            Shader.SetGlobalVector("_HitFeedback", new Vector4(HitFeedback.FlashSeconds, HitFeedback.HoldSeconds, HitFeedback.DrainPerSecond, 0f));
            barAtlas = BuildBarAtlas();
            unitMaterial = new Material(shader) { name = "Unit instances", renderQueue = 3000 };
            // Unit frames are one tile square (64 px at PPU 64).
            unitMaterial.SetFloat("_UnitSize", (float)ArtStyle.TileSize / ArtStyle.PixelsPerUnit);
            unitMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            unitMaterial.SetFloat("_TeamGain", TeamGain());
            unitMaterial.SetTexture("_MainTex", atlas != null ? atlas.Texture : Texture2D.whiteTexture);
            unitMaterial.SetTexture("_MaskTex", atlas != null && atlas.Mask != null ? atlas.Mask : Texture2D.blackTexture);
            // Turrets: the same atlas over the hulls, as a second call so every turret covers every hull.
            turretMaterial = new Material(unitMaterial) { name = "Unit turrets", renderQueue = 3001 };
            barMaterial = new Material(shader) { name = "Unit health bars", renderQueue = 3002 };
            barMaterial.SetFloat("_UnitSize", (float)BarWidth / ArtStyle.PixelsPerUnit);
            barMaterial.SetFloat("_Aspect", (float)BarHeight / BarWidth);
            barMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            barMaterial.SetFloat("_TeamGain", 1f);
            barMaterial.SetTexture("_MainTex", barAtlas);
            barMaterial.SetTexture("_MaskTex", Texture2D.whiteTexture); // the whole bar takes the bar colour
            barMaterial.SetFloat("_Trail", 1f);
            Vector4[] cells = UvRects(atlas);
            unitFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cells.Length, 16);
            unitFrameRects.SetData(cells);
            barFrameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, BarFrames, 16);
            var rects = new Vector4[BarFrames];
            for (int i = 0; i < BarFrames; i++) rects[i] = new Vector4((float)i / BarFrames, 0f, 1f / BarFrames, 1f);
            barFrameRects.SetData(rects);
            unitMaterial.SetBuffer("_FrameRects", unitFrameRects);
            turretMaterial.SetBuffer("_FrameRects", unitFrameRects);
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
            LargeRingFrame = (uint)Mathf.Max(0, effects != null ? effects.FirstFrame("SelectionRingLarge") : 0);
            RoamRingFrame = (uint)Mathf.Max(0, effects != null ? effects.FirstFrame("RoamRing") : 0);
            BarrelShadowFrame = (uint)Mathf.Max(0, effects != null ? effects.FirstFrame("BarrelShadow") : 0);

            EnsureCapacity(ref instances, ref capacity, initialCapacity, unitMaterial);
            EnsureCapacity(ref turretInstances, ref turretCapacity, 1024, turretMaterial);
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

        /// <summary>Draws the last uploaded rings, units, turrets and bars again (a frame between packs).</summary>
        public void Redraw()
        {
            Render(ringMaterial, ringCount);
            Render(unitMaterial, unitCount);
            Render(turretMaterial, turretCount);
            Render(barMaterial, barCount);
        }

        private static void Render(Material material, int count)
        {
            if (count > 0) Graphics.RenderPrimitives(new RenderParams(material) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Uploads and draws <paramref name="count"/> unit instances.</summary>
        public void Draw(NativeArray<UnitInstance> data, int count)
        {
            unitCount = count;
            if (count <= 0) return;
            EnsureCapacity(ref instances, ref capacity, count, unitMaterial);
            instances.SetData(data, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(unitMaterial) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Uploads and draws the turrets of the units on screen (after the hulls, which they stand on).</summary>
        public void DrawTurrets(NativeArray<UnitInstance> turrets, int count)
        {
            turretCount = count;
            if (count <= 0) return;
            EnsureCapacity(ref turretInstances, ref turretCapacity, count, turretMaterial);
            turretInstances.SetData(turrets, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(turretMaterial) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Uploads and draws the selected units' rings (before the units, which draw over them).</summary>
        public void DrawRings(NativeArray<UnitInstance> rings, int count)
        {
            ringCount = count;
            if (count <= 0) return;
            EnsureCapacity(ref ringInstances, ref ringCapacity, count, ringMaterial);
            ringInstances.SetData(rings, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(ringMaterial) { worldBounds = WorldBounds }, MeshTopology.Triangles, QuadVertices, count);
        }

        /// <summary>Uploads and draws the damaged units' health bars.</summary>
        public void DrawHealthBars(NativeArray<UnitInstance> bars, int count)
        {
            barCount = count;
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
            turretInstances?.Dispose();
            unitFrameRects.Dispose();
            barFrameRects.Dispose();
            ringFrameRects.Dispose();
            UnityEngine.Object.Destroy(unitMaterial);
            UnityEngine.Object.Destroy(barMaterial);
            UnityEngine.Object.Destroy(ringMaterial);
            UnityEngine.Object.Destroy(turretMaterial);
            UnityEngine.Object.Destroy(barAtlas);
        }
    }

    /// <summary>
    /// Packs predicted units into instances, over the job threads (2026-11-07: 0.8 ms of the main thread at 100,000 units on one
    /// core). Each unit in <see cref="View"/> writes its own instance and its own bar and ring slots; <see cref="GatherPackedJob"/>
    /// then keeps those instances and appends the damaged ones' health bars and the selected ones' rings to their lists in unit order,
    /// as the one-thread job did.
    /// </summary>
    [BurstCompile]
    public struct PackUnitsJob : IJobParallelFor
    {
        /// <summary>Bits of <see cref="Emits"/>: which of a unit's slots hold a result this frame.</summary>
        public const byte EmitBar = 1, EmitRing = 2, EmitRoamRing = 4, EmitShown = 8;

        [ReadOnly] public NativeArray<int> Known;
        [ReadOnly] public NativeArray<float2> Positions;
        /// <summary>Parallel to <see cref="Known"/>: 0 for a unit away from the view whose prediction waits for a later turn (not drawn).</summary>
        [ReadOnly] public NativeArray<byte> Refreshed;
        /// <summary>Parallel to <see cref="Known"/>: how fast the drawn position moves (tiles a second), for the frames between packs.</summary>
        [ReadOnly] public NativeArray<float2> Velocity;
        /// <summary>By index: when the unit was last hit (Time.time; -infinity for never). The shader flashes it and drains its bar's trail.</summary>
        [ReadOnly] public NativeArray<float> HitAt;
        [ReadOnly] public NativeArray<uint> Frame;       // by index: the atlas cell
        [ReadOnly] public NativeArray<uint> Color;       // by index
        [ReadOnly] public NativeArray<float> Health01;   // by index
        [ReadOnly] public NativeArray<float> Scale;      // by index: tiles across (2 for large units)
        [ReadOnly] public NativeArray<byte> Selected;    // by index: bit 1 selected, bit 2 roaming
        /// <summary>v0.20 (Figma B1): by index, 1 hides the health bar (an airborne Bomber's).</summary>
        [ReadOnly] public NativeArray<byte> HideBar;
        [ReadOnly] public NativeArray<float> Trail;      // by index: the health bar's recent-damage level at the last hit
        public float UnitHalfSize;
        /// <summary>
        /// The view with a margin (xMin, yMin, xMax, yMax): a unit outside it packs nothing (2026-11-07: every known unit was uploaded and
        /// drawn each frame, 100,000 instances at the 100k benchmark's zoom for about 13,000 on screen).
        /// </summary>
        public float4 View;
        public uint RingFrame, LargeRingFrame, RoamRingFrame;
        /// <summary>The Gameplay setting's <see cref="HealthBarMode"/>: which units get a bar.</summary>
        public int BarMode;
        public NativeArray<UnitInstance> Instances;
        /// <summary>Parallel to <see cref="Known"/>: the unit's bar, and its two rings (selected, roaming) at 2i and 2i + 1.</summary>
        public NativeArray<UnitInstance> BarSlots;
        [NativeDisableParallelForRestriction] public NativeArray<UnitInstance> RingSlots;
        public NativeArray<byte> Emits;

        public void Execute(int i)
        {
            float2 at = Positions[i];
            // Not shown (off screen, or hidden by a replay's per-player view): nothing, not even a bar.
            if (Refreshed[i] == 0 || at.x < View.x || at.y < View.y || at.x > View.z || at.y > View.w || Scale[Known[i]] < 0f)
            {
                Emits[i] = 0;
                return;
            }
            int index = Known[i];
            byte emit = EmitShown;
            float health = Health01[index];
            float scale = Scale[index] > 0f ? Scale[index] : 1f;
            bool selected = (Selected[index] & 1) != 0;
            float2 velocity = Velocity[i];
            if (selected)
            {
                RingSlots[2 * i] = new UnitInstance { position = Positions[i], rotation = float.NegativeInfinity, frame = scale > 1.5f ? LargeRingFrame : RingFrame, color = 0xFFFFFFFFu, scale = scale > 1.5f ? 2f : 1f, velocity = velocity };
                emit |= EmitRing;
            }
            if ((Selected[index] & 2) != 0)
            {
                RingSlots[2 * i + 1] = new UnitInstance { position = Positions[i], rotation = float.NegativeInfinity, frame = RoamRingFrame, color = 0xFFFFFFFFu, scale = scale > 1.5f ? 2f : 1f, velocity = velocity };
                emit |= EmitRoamRing;
            }
            Instances[i] = new UnitInstance
            {
                position = Positions[i],
                rotation = HitAt[index],
                frame = Frame[index],
                color = Color[index],
                health = health,
                scale = scale,
                pad = (uint)(Selected[index] & 1),
                velocity = velocity,
            };
            if (WAR2D.Client.Overlays.OverlayRules.ShowHealthBar(BarMode, health, selected) && !(HideBar.IsCreated && HideBar[index] != 0))
            {
                int frame = math.clamp((int)math.round(health * (InstancedUnitRenderer.BarFrames - 1)), 0, InstancedUnitRenderer.BarFrames - 1);
                BarSlots[i] = new UnitInstance
                {
                    position = Positions[i] + new float2(0f, UnitHalfSize * scale + 0.175f),
                    rotation = Trail[index],
                    frame = (uint)frame,
                    color = InstancedUnitRenderer.BarColor(health),
                    health = health,
                    scale = 1f,
                    pad = HitAt[index],
                    velocity = velocity,
                };
                emit |= EmitBar;
            }
            Emits[i] = emit;
        }
    }

    /// <summary>
    /// Moves <see cref="PackUnitsJob"/>'s shown instances to the front of <see cref="Instances"/> (in place, in unit order) and appends
    /// its bars and rings to their lists in unit order (rings: selected, then roaming).
    /// </summary>
    [BurstCompile]
    public struct GatherPackedJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Emits;
        [ReadOnly] public NativeArray<UnitInstance> BarSlots, RingSlots;
        public NativeArray<UnitInstance> Instances;
        public NativeArray<int> ShownCount;
        public NativeArray<UnitInstance> Bars;
        public NativeArray<int> BarCount;
        public NativeArray<UnitInstance> Rings;
        public NativeArray<int> RingCount;

        public void Execute()
        {
            int shown = 0, bars = 0, rings = 0;
            for (int i = 0; i < Emits.Length; i++)
            {
                byte emit = Emits[i];
                if (emit == 0) continue;
                Instances[shown++] = Instances[i]; // shown <= i: in place
                if ((emit & PackUnitsJob.EmitRing) != 0) Rings[rings++] = RingSlots[2 * i];
                if ((emit & PackUnitsJob.EmitRoamRing) != 0) Rings[rings++] = RingSlots[2 * i + 1];
                if ((emit & PackUnitsJob.EmitBar) != 0) Bars[bars++] = BarSlots[i];
            }
            ShownCount[0] = shown;
            BarCount[0] = bars;
            RingCount[0] = rings;
        }
    }
}
