using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Net.Replication;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Draws every building the client knows (seen, ghosts, blueprints and the bombs it may know of) as one
    /// instanced call from the <c>Buildings</c> atlas, then the construction overlays (<c>Effects</c> atlas) and the
    /// health and progress bars, rebuilt each frame from <see cref="ClientBuildings"/> by
    /// <see cref="BuildingPacking"/>. Replaces the GameObject per building; clicks resolve through
    /// <see cref="BuildingPicker"/>. Buildings draw under the units' selection rings and the units.
    /// </summary>
    public sealed class InstancedBuildingRenderer : IDisposable
    {
        public const string ShaderPath = "Shaders/InstancedBuilding";
        private const int QuadVertices = 6;

        private readonly Material buildingMaterial, overlayMaterial, barMaterial;
        private readonly Texture2D barAtlas;
        private readonly GraphicsBuffer frameRects, frameGeom, overlayRects, barRects;
        private GraphicsBuffer buildings, overlays, bars;
        private int buildingCapacity, overlayCapacity, barCapacity;
        private NativeArray<UnitInstance> scratch;
        private readonly BuildingFrames frames;
        private readonly BuildingPacking.Lists lists = new BuildingPacking.Lists();
        /// <summary>Per building: its health fraction at the last layout, when it was last hit, its bar's trail then, and whether it was a ghost.</summary>
        private readonly Dictionary<int, (float health, float hitAt, float trail, bool ghost)> hits = new Dictionary<int, (float, float, float, bool)>();
        /// <summary>Per building: its progress at the last layout and when it was finished (polish J's pulse).</summary>
        private readonly Dictionary<int, (byte progress, float finishedAt)> finish = new Dictionary<int, (byte, float)>();
        private readonly List<int> gone = new List<int>();
        private readonly Func<int, float> hitAtOf, trailAtOf, finishedAtOf;
        private Func<int, ClientBuildings.TurretHead> headOf;
        private ClientBuildings headsOf;
        // What the last layout was made from: the buildings and their change count, the setting, the selection, the colours and the view.
        private ClientBuildings laidFrom;
        private int laidVersion = -1, laidBarMode = -1, laidSelection = int.MinValue, laidColours = -1;
        private Rect? laidView;

        /// <summary>The share of the view's size a layout reaches past each edge, so a camera that moves a little needs no new one.</summary>
        private const float LayoutMargin = 0.25f;

        /// <param name="atlas">The <c>Buildings</c> atlas; without one, every building draws as a white square.</param>
        public InstancedBuildingRenderer(ArtAtlas atlas, ArtAtlas effects)
        {
            Shader shader = Resources.Load<Shader>(ShaderPath);
            Shader quads = Resources.Load<Shader>(InstancedUnitRenderer.ShaderPath);
            if (shader == null || quads == null) throw new InvalidOperationException($"Resources/{ShaderPath}.shader or {InstancedUnitRenderer.ShaderPath} is missing");
            frames = new BuildingFrames(atlas, effects);
            hitAtOf = id => hits.TryGetValue(id, out var h) ? h.hitAt : float.NegativeInfinity;
            trailAtOf = id => hits.TryGetValue(id, out var h) ? h.trail : 0f;
            finishedAtOf = id => finish.TryGetValue(id, out var f) ? f.finishedAt : float.NegativeInfinity;

            buildingMaterial = new Material(shader) { name = "Building instances", renderQueue = 2996 };
            buildingMaterial.SetTexture("_MainTex", atlas != null ? atlas.Texture : Texture2D.whiteTexture);
            buildingMaterial.SetTexture("_MaskTex", atlas != null && atlas.Mask != null ? atlas.Mask : Texture2D.blackTexture);
            buildingMaterial.SetFloat("_TeamGain", InstancedUnitRenderer.TeamGain());
            buildingMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            Vector4[] rects = InstancedUnitRenderer.UvRects(atlas);
            frameRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rects.Length, 16);
            frameRects.SetData(rects);
            Vector4[] geom = Geometry(atlas);
            frameGeom = new GraphicsBuffer(GraphicsBuffer.Target.Structured, geom.Length, 16);
            frameGeom.SetData(geom);
            buildingMaterial.SetBuffer("_FrameRects", frameRects);
            buildingMaterial.SetBuffer("_FrameGeom", frameGeom);

            overlayMaterial = new Material(quads) { name = "Construction overlays", renderQueue = 2997 };
            overlayMaterial.SetFloat("_UnitSize", (float)ArtStyle.TileSize / ArtStyle.PixelsPerUnit);
            overlayMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            overlayMaterial.SetFloat("_TeamGain", 1f);
            overlayMaterial.SetTexture("_MainTex", effects != null ? effects.Texture : Texture2D.whiteTexture);
            overlayMaterial.SetTexture("_MaskTex", Texture2D.blackTexture);
            Vector4[] effectRects = InstancedUnitRenderer.UvRects(effects);
            overlayRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, effectRects.Length, 16);
            overlayRects.SetData(effectRects);
            overlayMaterial.SetBuffer("_FrameRects", overlayRects);

            barAtlas = InstancedUnitRenderer.BuildBarAtlas();
            barMaterial = new Material(quads) { name = "Building bars", renderQueue = 3001 };
            barMaterial.SetFloat("_UnitSize", (float)InstancedUnitRenderer.BarWidth / ArtStyle.PixelsPerUnit);
            barMaterial.SetFloat("_Aspect", (float)InstancedUnitRenderer.BarHeight / InstancedUnitRenderer.BarWidth);
            barMaterial.SetFloat("_PixelsPerUnit", ArtStyle.PixelsPerUnit);
            barMaterial.SetFloat("_TeamGain", 1f);
            barMaterial.SetTexture("_MainTex", barAtlas);
            barMaterial.SetTexture("_MaskTex", Texture2D.whiteTexture);
            barMaterial.SetFloat("_Trail", 1f);
            var barCells = new Vector4[InstancedUnitRenderer.BarFrames];
            for (int i = 0; i < barCells.Length; i++) barCells[i] = new Vector4((float)i / barCells.Length, 0f, 1f / barCells.Length, 1f);
            barRects = new GraphicsBuffer(GraphicsBuffer.Target.Structured, barCells.Length, 16);
            barRects.SetData(barCells);
            barMaterial.SetBuffer("_FrameRects", barRects);

            scratch = new NativeArray<UnitInstance>(256, Allocator.Persistent);
        }

        /// <summary>Each atlas cell's size and pivot in world units (one tile square without an atlas).</summary>
        public static Vector4[] Geometry(ArtAtlas atlas)
        {
            if (atlas == null || atlas.Data.frames.Count == 0) return new[] { new Vector4(1f, 1f, 0.5f, 0.5f) };
            float ppu = Mathf.Max(1, atlas.Data.pixelsPerUnit);
            var geom = new Vector4[atlas.Data.frames.Count];
            for (int i = 0; i < geom.Length; i++)
            {
                AtlasFrame f = atlas.Data.frames[i];
                geom[i] = new Vector4(f.w / ppu, f.h / ppu, f.pivotX / ppu, f.pivotY / ppu);
            }
            return geom;
        }

        /// <summary>How many buildings the last <see cref="Draw"/> drew.</summary>
        public int LastDrawn => lists.Buildings.Count;

        /// <summary>The team colour (RGB) the last draw gave the first building of an owner, or 0.</summary>
        public uint LastColourOf(int ownerId, ClientBuildings known)
        {
            foreach (UnitInstance u in lists.Buildings)
                foreach (ClientBuildings.Entry e in known.Entries.Values)
                    if (e.Data.ownerId == ownerId && math.all(Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType)) == u.position))
                        return u.color & 0x00FFFFFFu;
            return 0;
        }

        /// <summary>
        /// Packs and draws the buildings. <paramref name="colorOf"/> gives an owner's packed team colour; <paramref name="barMode"/>
        /// (a <see cref="HealthBarMode"/>) and <paramref name="selected"/> pick which health bars show.
        /// </summary>
        /// <param name="selection">Changes whenever the selection does (negative: unknown, so every frame lays out); <paramref name="colours"/>
        /// whenever an owner's colour does; <paramref name="everyFrame"/> for buildings changed other than through <see cref="ClientBuildings"/>
        /// (a replay's per-player view).</param>
        /// <remarks>
        /// The layout (<see cref="BuildingPacking.Layout"/>, with the hits and finishes it shows) is made again only when the buildings, the
        /// setting, the selection, the colours or the view change; each frame then steps it to <paramref name="time"/> and draws it (2026-11-09:
        /// laying out every known building every frame cost a 100,000-unit client on three cores 0.8 ms of its frame).
        /// </remarks>
        public void Draw(ClientBuildings known, float time, Func<int, uint> colorOf, int barMode = (int)HealthBarMode.DamagedOnly, Func<int, bool> selected = null,
            Rect? view = null, int selection = -1, int colours = 0, bool everyFrame = false)
        {
            bool inside = view.HasValue == laidView.HasValue && (!view.HasValue || (laidView.Value.Contains(view.Value.min) && laidView.Value.Contains(view.Value.max)));
            if (everyFrame || selection < 0 || !inside || known != laidFrom || known.Version != laidVersion || barMode != laidBarMode || selection != laidSelection
                || colours != laidColours)
            {
                laidFrom = known;
                laidVersion = known.Version;
                laidBarMode = barMode;
                laidSelection = selection;
                laidColours = colours;
                if (view.HasValue)
                {
                    Rect v = view.Value;
                    view = new Rect(v.xMin - v.width * LayoutMargin, v.yMin - v.height * LayoutMargin, v.width * (1f + 2f * LayoutMargin), v.height * (1f + 2f * LayoutMargin));
                }
                laidView = view;
                TrackHits(known, time);
                if (headsOf != known)
                {
                    headsOf = known;
                    headOf = known.HeadOf;
                }
                BuildingPacking.Layout(known.Entries.Values, frames, colorOf, WorldStateManager.GetBuildingSize, hitAtOf, lists, headOf, barMode, selected, trailAtOf, finishedAtOf, view);
                Submit(lists.Bars, ref bars, ref barCapacity, barMaterial, true); // a bar's trail drains in the shader
            }
            else Submit(lists.Bars, ref bars, ref barCapacity, barMaterial, false);
            BuildingPacking.Animate(lists, frames, time);
            Submit(lists.Buildings, ref buildings, ref buildingCapacity, buildingMaterial, true);
            Submit(lists.Overlays, ref overlays, ref overlayCapacity, overlayMaterial, true);
        }

        /// <summary>
        /// A building whose health dropped since the last frame flashes red briefly (it was hit), and its bar keeps the lost part as a
        /// draining trail (<see cref="HitFeedback"/>). A ghost that comes back into sight with less health than last seen took that
        /// damage out of sight: no flash and no trail.
        /// </summary>
        private void TrackHits(ClientBuildings known, float now)
        {
            foreach (ClientBuildings.Entry e in known.Entries.Values)
            {
                int id = e.Data.id;
                float health = e.Health.maxHealth > 0f ? e.Health.currentHealth / e.Health.maxHealth : 1f;
                if (hits.TryGetValue(id, out var h))
                {
                    bool hit = health < h.health && !e.Ghost && !h.ghost;
                    // v0.16: fights on the minimap; an own unpaid building's decay is not one (code review 2026-11-03).
                    if (hit && MinimapCombat.Current != null && !(e.Data.ownerId == WorldEffects.LocalOwner && DefenceView.Unpaid(e)))
                        MinimapCombat.Current.Hit(Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType)),
                            e.Data.ownerId == WorldEffects.LocalOwner, now);
                    float hitAt = hit ? now : h.hitAt;
                    // The trail at a hit: where the last one's had drained to, at least the health (HitFeedback.TrailNow drains it after).
                    float trail = e.Ghost || h.ghost ? health : hit ? math.max(HitFeedback.TrailNow(h.trail, h.health, now - h.hitAt), health) : h.trail;
                    // Written only when it changes: a write for every building every frame cost more than the rest (thousands at 100,000 units).
                    if (health != h.health || hitAt != h.hitAt || trail != h.trail || e.Ghost != h.ghost) hits[id] = (health, hitAt, trail, e.Ghost);
                }
                else hits[id] = (health, float.NegativeInfinity, health, e.Ghost);
                // A blueprint seen rising that reaches 100 % was just finished; one first seen finished never pulses.
                bool tracked = finish.TryGetValue(id, out var f);
                bool done = tracked && f.progress < 100 && e.Progress >= 100 && !e.Ghost;
                if (!tracked || done || f.progress != e.Progress) finish[id] = (e.Progress, done ? now : tracked ? f.finishedAt : float.NegativeInfinity);
            }
            if (hits.Count == known.Entries.Count) return; // every known building is in hits: none has gone
            gone.Clear();
            foreach (int id in hits.Keys) if (!known.Entries.ContainsKey(id)) gone.Add(id);
            foreach (int id in gone)
            {
                hits.Remove(id);
                finish.Remove(id);
            }
        }

        private void Submit(List<UnitInstance> list, ref GraphicsBuffer buffer, ref int capacity, Material material, bool upload)
        {
            int count = list.Count;
            if (count == 0) return;
            if (!upload && buffer != null && capacity >= count)
            {
                Graphics.RenderPrimitives(new RenderParams(material) { worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f) }, MeshTopology.Triangles, QuadVertices, count);
                return;
            }
            if (buffer == null || capacity < count)
            {
                buffer?.Dispose();
                capacity = math.max(256, math.ceilpow2(count));
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, InstancedUnitRenderer.InstanceStride);
                material.SetBuffer("_Instances", buffer);
            }
            if (scratch.Length < count)
            {
                scratch.Dispose();
                scratch = new NativeArray<UnitInstance>(math.ceilpow2(count), Allocator.Persistent);
            }
            for (int i = 0; i < count; i++) scratch[i] = list[i];
            buffer.SetData(scratch, 0, 0, count);
            Graphics.RenderPrimitives(new RenderParams(material) { worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f) }, MeshTopology.Triangles, QuadVertices, count);
        }

        public void Dispose()
        {
            buildings?.Dispose();
            overlays?.Dispose();
            bars?.Dispose();
            frameRects.Dispose();
            frameGeom.Dispose();
            overlayRects.Dispose();
            barRects.Dispose();
            if (scratch.IsCreated) scratch.Dispose();
            UnityEngine.Object.Destroy(buildingMaterial);
            UnityEngine.Object.Destroy(overlayMaterial);
            UnityEngine.Object.Destroy(barMaterial);
            UnityEngine.Object.Destroy(barAtlas);
        }
    }
}
