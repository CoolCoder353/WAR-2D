using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Net.Replication;

namespace WAR2D.Client.Render
{
    /// <summary>Where each building type's frames are in the <c>Buildings</c> atlas, and the construction overlay's in <c>Effects</c>.</summary>
    public sealed class BuildingFrames
    {
        private readonly ArtAtlas buildings;
        private readonly Dictionary<BuildingType, (int first, int frames, int directions)> byType = new Dictionary<BuildingType, (int, int, int)>();

        /// <summary>The Gun turret head's first cell, frames per facing and facings (0 frames when the atlas has none).</summary>
        public (int first, int frames, int directions) Head { get; }

        /// <summary>The overlay's first cell and frame count in the Effects atlas (0 frames when there is none).</summary>
        public int OverlayFirst { get; }
        public int OverlayFrames { get; }

        public BuildingFrames(ArtAtlas buildings, ArtAtlas effects)
        {
            this.buildings = buildings;
            Head = buildings != null && buildings.FirstFrame(ArtManifest.TurretHead) >= 0
                ? (buildings.FirstFrame(ArtManifest.TurretHead), Math.Max(1, buildings.Frames(ArtManifest.TurretHead)), Math.Max(1, buildings.Directions(ArtManifest.TurretHead)))
                : (0, 0, 1);
            OverlayFirst = effects != null ? Math.Max(0, effects.FirstFrame("ConstructionOverlay")) : 0;
            OverlayFrames = effects != null && effects.FirstFrame("ConstructionOverlay") >= 0 ? effects.Frames("ConstructionOverlay") : 0;
        }

        /// <summary>A type's first cell, frames per facing and facings (a type the atlas lacks draws cell 0).</summary>
        public (int first, int frames, int directions) Of(BuildingType type)
        {
            if (byType.TryGetValue(type, out var f)) return f;
            string id = type.ToString();
            f = buildings != null && buildings.FirstFrame(id) >= 0
                ? (buildings.FirstFrame(id), Math.Max(1, buildings.Frames(id)), Math.Max(1, buildings.Directions(id)))
                : (0, 1, 1);
            byType[type] = f;
            return f;
        }
    }

    /// <summary>
    /// Turns the client's buildings into draw instances for <see cref="InstancedBuildingRenderer"/>: each building at
    /// its footprint's visual centre in the facing of its rotation, looping its idle frames (ghosts hold frame 0),
    /// sorted so farther buildings draw first (the 3/4 view); a Gun turret's base shows its damaged frame by health
    /// (not looped) and its head is drawn over it in the facing <see cref="ClientBuildings.Heads"/> keeps, recoiling
    /// just after a shot (v0.13); blueprints fade in with progress under the
    /// <c>ConstructionOverlay</c> with a gold progress bar under them; damaged finished buildings get a health bar.
    /// Pure, apart from reading <see cref="ArtStyle.FramesPerSecond"/>.
    /// </summary>
    public static class BuildingPacking
    {
        /// <summary><see cref="UnitInstance.pad"/> bits the building shader reads.</summary>
        public const int GhostFlag = 1, FlashFlag = 2, FinishFlag = 4;

        /// <summary>The progress bar's colour (the HUD's readout gold).</summary>
        public static readonly uint ProgressColor = InstancedUnitRenderer.Pack(new Color32(0xFF, 0xC8, 0x57, 0xFF));

        private const float StartAlpha = 0.25f, RisingAlpha = 0.85f, BarGap = 0.15f;

        /// <summary>A Gun turret's base draws its damaged frame below this share of its health (a visual constant, as <c>WallRules.DamageState</c>).</summary>
        public const float TurretDamagedBelow = 0.5f;

        /// <summary>Seconds a turret's head shows its recoil frame after a shot.</summary>
        public const float RecoilSeconds = 0.1f;

        /// <summary><see cref="UnitInstance.rotation"/> of a turret head: the building shader ignores it, the sort draws the head after its base.</summary>
        private const float HeadLayer = 1f;

        /// <summary>What a packed building or head needs to step each frame: its frames and the times its recoil, flash and pulse count from.</summary>
        public struct Anim
        {
            /// <summary>The frame with no step added, how many it loops through (1: it holds), its hold and phase (an idle loop).</summary>
            public uint First;
            public int Frames, Hold;
            public float Phase;
            /// <summary>A Gun turret's head, and its last shot (<c>Time.time</c>).</summary>
            public bool Head;
            public float FiredAt;
            /// <summary>Its last hit and its finish (<c>Time.time</c>; -infinity for none), and whether it is a ghost (neither shows).</summary>
            public float HitAt, FinishedAt;
            public bool Ghost;
        }

        public sealed class Lists
        {
            public readonly List<UnitInstance> Buildings = new List<UnitInstance>();
            /// <summary>Parallel to <see cref="Buildings"/>: how each steps (<see cref="Animate"/>).</summary>
            public readonly List<Anim> Anims = new List<Anim>();
            public readonly List<UnitInstance> Overlays = new List<UnitInstance>();
            /// <summary>Health and progress bars (the bar art: frame = how full).</summary>
            public readonly List<UnitInstance> Bars = new List<UnitInstance>();

            public void Clear()
            {
                Buildings.Clear();
                Anims.Clear();
                Overlays.Clear();
                Bars.Clear();
            }
        }

        // Ties break on x, so equal-y neighbours keep a fixed order (List.Sort is not stable) and don't flicker.
        private static readonly Comparison<UnitInstance> FartherFirst = (a, b) =>
        {
            int byY = b.position.y.CompareTo(a.position.y);
            if (byY != 0) return byY;
            int byX = a.position.x.CompareTo(b.position.x);
            return byX != 0 ? byX : a.rotation.CompareTo(b.rotation);
        };

        /// <summary>Lays the buildings out (<see cref="Layout"/>) and steps them to <paramref name="time"/> (<see cref="Animate"/>).</summary>
        public static void Pack(IEnumerable<ClientBuildings.Entry> entries, BuildingFrames frames, Func<int, uint> colorOf, Func<BuildingType, int2> sizeOf,
            float time, Func<int, float> hitAtOf, Lists into, Func<int, ClientBuildings.TurretHead> headOf = null,
            int barMode = (int)HealthBarMode.DamagedOnly, Func<int, bool> selected = null, Func<int, float> trailAtOf = null,
            Func<int, float> finishedAtOf = null, Rect? view = null)
        {
            Layout(entries, frames, colorOf, sizeOf, hitAtOf, into, headOf, barMode, selected, trailAtOf, finishedAtOf, view);
            Animate(into, frames, time);
        }

        /// <param name="colorOf">An owner's packed team colour.</param>
        /// <param name="hitAtOf">When a building was last hit (<c>Time.time</c>; -infinity for never): it flashes red, and its bar's trail drains from then.</param>
        /// <param name="headOf">A Gun turret's head (facing and last shot, on the <c>Time.time</c> clock); null faces every head east.</param>
        /// <param name="barMode">The Gameplay setting's <see cref="HealthBarMode"/>: which finished buildings get a health bar.</param>
        /// <param name="selected">True for a selected building (SelectedOnly); null when none is.</param>
        /// <param name="trailAtOf">A building's recent-damage trail at its last hit (<see cref="HitFeedback"/>: the bar's shader drains it); null draws none.</param>
        /// <param name="finishedAtOf">When a building was just finished (it pulses bright; -infinity for none); null pulses none.</param>
        /// <param name="view">The world rect on screen: a building outside it (with a tile for its bar and head) packs nothing (2026-11-07: every
        /// building on the map was packed and sorted each frame); null packs them all.</param>
        /// <remarks>Everything a building shows over time (its idle loop, a head's recoil, a flash, a finish pulse, a bar's trail) is left to
        /// <see cref="Animate"/> and the shaders, so a layout holds until the buildings, the setting, the selection or the view change.</remarks>
        public static void Layout(IEnumerable<ClientBuildings.Entry> entries, BuildingFrames frames, Func<int, uint> colorOf, Func<BuildingType, int2> sizeOf,
            Func<int, float> hitAtOf, Lists into, Func<int, ClientBuildings.TurretHead> headOf = null,
            int barMode = (int)HealthBarMode.DamagedOnly, Func<int, bool> selected = null, Func<int, float> trailAtOf = null,
            Func<int, float> finishedAtOf = null, Rect? view = null)
        {
            into.Clear();
            foreach (ClientBuildings.Entry e in entries)
            {
                BuildingData d = e.Data;
                int2 size = sizeOf(d.buildingType);
                float2 centre = Footprint.VisualCenter((int2)math.round(d.position), size);
                if (view.HasValue && !view.Value.Overlaps(new Rect(centre.x - size.x * 0.5f - 1f, centre.y - size.y * 0.5f - 1f, size.x + 2f, size.y + 2f))) continue;
                (int first, int count, int directions) = frames.Of(d.buildingType);
                int facing = directions <= 1 ? 0 : ((int)math.round(d.rotation / 90f) % directions + directions) % directions;
                float health = e.Health.maxHealth > 0f ? e.Health.currentHealth / e.Health.maxHealth : 1f;
                bool turret = d.buildingType == BuildingType.GunTurret;
                // A Gun turret's base holds its damaged frame; a ghost holds frame 0; the rest loop their idle frames.
                int held = turret ? (count > 1 && health < TurretDamagedBelow ? 1 : 0) : 0;
                bool loops = !turret && !e.Ghost && count > 1;
                bool rising = e.Progress < 100;
                float progress = e.Progress / 100f;
                uint color = colorOf(d.ownerId) & 0x00FFFFFFu;
                float alpha = rising ? math.lerp(StartAlpha, RisingAlpha, progress) : 1f;
                color |= (uint)math.round(alpha * 255f) << 24;
                float hitAt = e.Ghost ? float.NegativeInfinity : hitAtOf(d.id);
                float finishedAt = e.Ghost || finishedAtOf == null ? float.NegativeInfinity : finishedAtOf(d.id);
                var anim = new Anim
                {
                    First = (uint)(first + facing * count + held), Frames = loops ? count : 1, Hold = Hold(d.buildingType), Phase = Phase(centre),
                    HitAt = hitAt, FinishedAt = finishedAt, Ghost = e.Ghost, FiredAt = float.NegativeInfinity,
                };
                into.Buildings.Add(new UnitInstance { position = centre, frame = anim.First, color = color, scale = 1f, health = 1f });
                into.Anims.Add(anim);
                if (turret && frames.Head.frames > 0)
                {
                    ClientBuildings.TurretHead head = headOf != null ? headOf(d.id) : new ClientBuildings.TurretHead { FiredAt = float.NegativeInfinity };
                    var (headFirst, headFrames, headDirections) = frames.Head;
                    int headFacing = head.Facing % headDirections;
                    anim.First = (uint)(headFirst + headFacing * headFrames);
                    anim.Frames = headFrames;
                    anim.Head = true;
                    anim.FiredAt = head.FiredAt;
                    into.Buildings.Add(new UnitInstance { position = centre, rotation = HeadLayer, frame = anim.First, color = color, scale = 1f, health = 1f });
                    into.Anims.Add(anim);
                }

                if (rising && !e.Ghost)
                {
                    if (frames.OverlayFrames > 0)
                        into.Overlays.Add(new UnitInstance
                        {
                            position = centre,
                            rotation = float.NegativeInfinity, // the unit shader's hit time: an overlay never flashes
                            frame = (uint)frames.OverlayFirst,
                            color = 0xFFFFFFFFu,
                            scale = size.x,
                        });
                    into.Bars.Add(Bar(centre - new float2(0f, size.y * 0.5f + BarGap), size, progress, ProgressColor));
                    continue;
                }
                if (!e.Ghost && WAR2D.Client.Overlays.OverlayRules.ShowHealthBar(barMode, health, selected != null && selected(d.id)))
                    into.Bars.Add(Bar(centre + new float2(0f, size.y * 0.5f + BarGap), size, health, InstancedUnitRenderer.BarColor(health), trailAtOf != null ? trailAtOf(d.id) : health, hitAt));
            }
            SortFartherFirst(into);
        }

        /// <summary>
        /// Steps the laid-out buildings to <paramref name="time"/>: each idle loop's frame, a head's recoil, the hit flash and the finish
        /// pulse, and the construction overlay's frame; the same as a layout made at that time.
        /// </summary>
        public static void Animate(Lists into, BuildingFrames frames, float time)
        {
            for (int i = 0; i < into.Buildings.Count; i++)
            {
                Anim a = into.Anims[i];
                UnitInstance u = into.Buildings[i];
                int step;
                // v0.20: the 2×2 head's frames 1 and 2 fire its left and right barrels; ten shots a second alternate them.
                if (a.Head) step = !a.Ghost && a.Frames > 1 && time - a.FiredAt < RecoilSeconds && time >= a.FiredAt ? 1 + ((int)(time * 20f) & 1) % (a.Frames - 1) : 0;
                else step = a.Frames > 1 ? (int)((time + a.Phase) * ArtStyle.FramesPerSecond) / a.Hold % a.Frames : 0;
                u.frame = a.First + (uint)step;
                u.pad = (a.Ghost ? GhostFlag : 0) | (!a.Ghost && HitFeedback.Flashing(a.HitAt, time) ? FlashFlag : 0)
                    | (!a.Ghost && HitFeedback.Finishing(a.FinishedAt, time) ? FinishFlag : 0);
                into.Buildings[i] = u;
            }
            if (frames.OverlayFrames <= 0) return;
            uint overlay = (uint)(frames.OverlayFirst + (int)(time * 4f) % frames.OverlayFrames);
            for (int i = 0; i < into.Overlays.Count; i++)
            {
                UnitInstance u = into.Overlays[i];
                u.frame = overlay;
                into.Overlays[i] = u;
            }
        }

        private static readonly List<int> order = new List<int>();
        private static readonly List<UnitInstance> sortedBuildings = new List<UnitInstance>();
        private static readonly List<Anim> sortedAnims = new List<Anim>();

        /// <summary>Sorts the buildings farther first (the 3/4 view), their steps with them.</summary>
        private static void SortFartherFirst(Lists into)
        {
            order.Clear();
            for (int i = 0; i < into.Buildings.Count; i++) order.Add(i);
            List<UnitInstance> buildings = into.Buildings;
            order.Sort((a, b) => FartherFirst(buildings[a], buildings[b]));
            sortedBuildings.Clear();
            sortedAnims.Clear();
            foreach (int i in order)
            {
                sortedBuildings.Add(buildings[i]);
                sortedAnims.Add(into.Anims[i]);
            }
            buildings.Clear();
            buildings.AddRange(sortedBuildings);
            into.Anims.Clear();
            into.Anims.AddRange(sortedAnims);
        }

        /// <summary>
        /// A bar <paramref name="full"/> full; <paramref name="trail"/> (as full or more) is the recent-damage level at the hit at
        /// <paramref name="hitAt"/>, which the unit shader draws pale and drains (<see cref="HitFeedback.TrailNow"/>).
        /// </summary>
        private static UnitInstance Bar(float2 at, int2 size, float full, uint color, float trail = 0f, float hitAt = float.PositiveInfinity) => new UnitInstance
        {
            position = at,
            rotation = trail,
            pad = hitAt,
            frame = (uint)math.clamp((int)math.round(math.saturate(full) * (InstancedUnitRenderer.BarFrames - 1)), 0, InstancedUnitRenderer.BarFrames - 1),
            color = color,
            health = full,
            scale = size.x,
        };

        /// <summary>
        /// Frames an idle frame holds: the Spawner's lamp and the Jammer's aerial lamps blink once a second, the rest turn every tick
        /// (<see cref="Animate"/>: <c>(int)((time + phase) * FramesPerSecond) / hold % frames</c>).
        /// </summary>
        private static int Hold(BuildingType type) => type == BuildingType.Barracks || type == BuildingType.Jammer ? math.max(1, ArtStyle.FramesPerSecond / 2) : 1;

        /// <summary>A building's place in its idle loop, so neighbours are staggered.</summary>
        private static float Phase(float2 centre) => (centre.x * 0.37f + centre.y * 0.61f) % 1f;
    }
}
