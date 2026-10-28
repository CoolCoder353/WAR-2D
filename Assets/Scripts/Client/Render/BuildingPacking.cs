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
        public const int GhostFlag = 1, FlashFlag = 2;

        /// <summary>The progress bar's colour (the HUD's readout gold).</summary>
        public static readonly uint ProgressColor = InstancedUnitRenderer.Pack(new Color32(0xFF, 0xC8, 0x57, 0xFF));

        private const float StartAlpha = 0.25f, RisingAlpha = 0.85f, BarGap = 0.15f;

        /// <summary>A Gun turret's base draws its damaged frame below this share of its health (a visual constant, as <c>WallRules.DamageState</c>).</summary>
        public const float TurretDamagedBelow = 0.5f;

        /// <summary>Seconds a turret's head shows its recoil frame after a shot.</summary>
        public const float RecoilSeconds = 0.1f;

        /// <summary><see cref="UnitInstance.rotation"/> of a turret head: the building shader ignores it, the sort draws the head after its base.</summary>
        private const float HeadLayer = 1f;

        public sealed class Lists
        {
            public readonly List<UnitInstance> Buildings = new List<UnitInstance>();
            public readonly List<UnitInstance> Overlays = new List<UnitInstance>();
            /// <summary>Health and progress bars (the bar art: frame = how full).</summary>
            public readonly List<UnitInstance> Bars = new List<UnitInstance>();

            public void Clear()
            {
                Buildings.Clear();
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

        /// <param name="colorOf">An owner's packed team colour.</param>
        /// <param name="flashing">True for a building that was just hit (it flashes red).</param>
        /// <param name="headOf">A Gun turret's head (facing and last shot, on the <paramref name="time"/> clock); null faces every head east.</param>
        /// <param name="barMode">The Gameplay setting's <see cref="HealthBarMode"/>: which finished buildings get a health bar.</param>
        /// <param name="selected">True for a selected building (SelectedOnly); null when none is.</param>
        public static void Pack(IEnumerable<ClientBuildings.Entry> entries, BuildingFrames frames, Func<int, uint> colorOf, Func<BuildingType, int2> sizeOf,
            float time, Func<int, bool> flashing, Lists into, Func<int, ClientBuildings.TurretHead> headOf = null,
            int barMode = (int)HealthBarMode.DamagedOnly, Func<int, bool> selected = null)
        {
            into.Clear();
            foreach (ClientBuildings.Entry e in entries)
            {
                BuildingData d = e.Data;
                int2 size = sizeOf(d.buildingType);
                float2 centre = Footprint.VisualCenter((int2)math.round(d.position), size);
                (int first, int count, int directions) = frames.Of(d.buildingType);
                int facing = directions <= 1 ? 0 : ((int)math.round(d.rotation / 90f) % directions + directions) % directions;
                float health = e.Health.maxHealth > 0f ? e.Health.currentHealth / e.Health.maxHealth : 1f;
                int frame = d.buildingType == BuildingType.GunTurret ? (count > 1 && health < TurretDamagedBelow ? 1 : 0)
                    : e.Ghost || count <= 1 ? 0 : AnimationFrame(d.buildingType, centre, time, count);
                bool rising = e.Progress < 100;
                float progress = e.Progress / 100f;
                uint color = colorOf(d.ownerId) & 0x00FFFFFFu;
                float alpha = rising ? math.lerp(StartAlpha, RisingAlpha, progress) : 1f;
                color |= (uint)math.round(alpha * 255f) << 24;
                int flags = (e.Ghost ? GhostFlag : 0) | (!e.Ghost && flashing(d.id) ? FlashFlag : 0);
                into.Buildings.Add(new UnitInstance { position = centre, frame = (uint)(first + facing * count + frame), color = color, scale = 1f, pad = flags, health = 1f });
                if (d.buildingType == BuildingType.GunTurret && frames.Head.frames > 0)
                {
                    ClientBuildings.TurretHead head = headOf != null ? headOf(d.id) : new ClientBuildings.TurretHead { FiredAt = float.NegativeInfinity };
                    var (headFirst, headFrames, headDirections) = frames.Head;
                    int headFacing = head.Facing % headDirections;
                    int recoil = !e.Ghost && headFrames > 1 && time - head.FiredAt < RecoilSeconds && time >= head.FiredAt ? 1 : 0;
                    into.Buildings.Add(new UnitInstance
                    {
                        position = centre, rotation = HeadLayer, frame = (uint)(headFirst + headFacing * headFrames + recoil),
                        color = color, scale = 1f, pad = flags, health = 1f,
                    });
                }

                if (rising && !e.Ghost)
                {
                    if (frames.OverlayFrames > 0)
                        into.Overlays.Add(new UnitInstance
                        {
                            position = centre,
                            frame = (uint)(frames.OverlayFirst + (int)(time * 4f) % frames.OverlayFrames),
                            color = 0xFFFFFFFFu,
                            scale = size.x,
                        });
                    into.Bars.Add(Bar(centre - new float2(0f, size.y * 0.5f + BarGap), size, progress, ProgressColor));
                    continue;
                }
                if (!e.Ghost && WAR2D.Client.Overlays.OverlayRules.ShowHealthBar(barMode, health, selected != null && selected(d.id))) into.Bars.Add(Bar(centre + new float2(0f, size.y * 0.5f + BarGap), size, health, InstancedUnitRenderer.BarColor(health)));
            }
            into.Buildings.Sort(FartherFirst);
        }

        private static UnitInstance Bar(float2 at, int2 size, float full, uint color) => new UnitInstance
        {
            position = at,
            frame = (uint)math.clamp((int)math.round(math.saturate(full) * (InstancedUnitRenderer.BarFrames - 1)), 0, InstancedUnitRenderer.BarFrames - 1),
            color = color,
            health = full,
            scale = size.x,
        };

        /// <summary>The idle frame at <paramref name="time"/>: the Spawner's lamp and the Jammer's aerial lamps blink once a second, the rest turn every tick; neighbours are staggered.</summary>
        private static int AnimationFrame(BuildingType type, float2 centre, float time, int frames)
        {
            int hold = type == BuildingType.Barracks || type == BuildingType.Jammer ? math.max(1, ArtStyle.FramesPerSecond / 2) : 1;
            float phase = (centre.x * 0.37f + centre.y * 0.61f) % 1f;
            return (int)((time + phase) * ArtStyle.FramesPerSecond) / hold % frames;
        }
    }
}
