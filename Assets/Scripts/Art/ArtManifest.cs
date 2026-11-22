using System;
using System.Collections.Generic;
using WAR2D.Art.Render;

namespace WAR2D.Art
{
    /// <summary>What an art entry is for (and which atlas it is packed into).</summary>
    public enum ArtKind { Unit, Building, Tile, Effect, Icon, Ui }

    /// <summary>One cell of a sprite, as its generator is asked for it.</summary>
    public readonly struct ArtCell
    {
        /// <summary>The frame within the facing, and the facing (or variant, for a variant-major sprite).</summary>
        public readonly int Frame, Direction;
        /// <summary>The cell's place in the sprite: <c>Direction × Frames + Frame</c>.</summary>
        public readonly int Index;
        /// <summary>The sprite's frames per facing, and its facings.</summary>
        public readonly int Frames, Directions;
        /// <summary>The build's seed (<see cref="ArtManifest.Seed"/>).</summary>
        public readonly int Seed;

        public ArtCell(int index, int frames, int directions, int seed)
        {
            Index = index;
            Frames = frames;
            Directions = directions;
            Frame = index % frames;
            Direction = index / frames;
            Seed = seed;
        }

        /// <summary>The facing as an angle: <see cref="Direction"/> of <see cref="Directions"/> round the circle.</summary>
        public float Angle => Direction * 2f * UnityEngine.Mathf.PI / Directions;

        /// <summary>How far through the sprite's frames this cell is, 0 to 1 (0 for a single frame), counted on <see cref="Index"/>.</summary>
        public float T => Frames > 1 ? (float)Index / (Frames - 1) : 0f;
    }

    /// <summary>
    /// One sprite in the art set: its id (exactly the <see cref="UnitType"/>, <see cref="BuildingType"/> or
    /// <see cref="TileType"/> name, or a fixed name for effects, icons and UI), its size in tiles, and how
    /// many frames it has per direction. A sprite has <c>Frames × Directions</c> cells in its atlas.
    /// </summary>
    public readonly struct ArtEntry
    {
        public readonly string Id;
        public readonly ArtKind Kind;
        public readonly int WidthTiles, HeightTiles;
        /// <summary>Frames per direction (animation frames, or tile variants).</summary>
        public readonly int Frames;
        /// <summary>Pre-drawn facings: 8 for units (no quad rotation), 1 for everything else.</summary>
        public readonly int Directions;
        /// <summary>True when the sprite has team-colour pixels (tinted with the owner's palette colour).</summary>
        public readonly bool TeamMask;
        /// <summary>The generator of a lit sprite (finished as pixel art by the build), or null.</summary>
        public readonly Func<ArtCell, LitCanvas> MakeLit;
        /// <summary>The generator of an effect (already palette pixel art), or null.</summary>
        public readonly Func<ArtCell, EffectFrame> MakeEffect;
        /// <summary>The 3/4 lift a lit sprite is rendered with.</summary>
        public readonly float Lift;
        /// <summary>A cache key for a cell, for sprites that share a rendered frame; null gives each cell its own.</summary>
        public readonly Func<ArtCell, string> Key;

        public ArtEntry(string id, ArtKind kind, int widthTiles, int heightTiles, int frames, int directions, bool teamMask)
        {
            Id = id;
            Kind = kind;
            WidthTiles = widthTiles;
            HeightTiles = heightTiles;
            Frames = frames;
            Directions = directions;
            TeamMask = teamMask;
            MakeLit = null;
            MakeEffect = null;
            Lift = 0f;
            Key = null;
        }

        private ArtEntry(ArtEntry e, Func<ArtCell, LitCanvas> lit, Func<ArtCell, EffectFrame> effect, float lift, Func<ArtCell, string> key)
            : this(e.Id, e.Kind, e.WidthTiles, e.HeightTiles, e.Frames, e.Directions, e.TeamMask)
        {
            MakeLit = lit;
            MakeEffect = effect;
            Lift = lift;
            Key = key;
        }

        /// <summary>This entry drawn by <paramref name="make"/>, a lit canvas per cell.</summary>
        public ArtEntry Lit(Func<ArtCell, LitCanvas> make, float lift = 0f, Func<ArtCell, string> key = null) => new ArtEntry(this, make, null, lift, key);

        /// <summary>This entry drawn by <paramref name="make"/>, a finished effect frame per cell.</summary>
        public ArtEntry Fx(Func<ArtCell, EffectFrame> make) => new ArtEntry(this, null, make, 0f, null);

        /// <summary>True when the entry names its generator (the build draws a placeholder for a sprite without one).</summary>
        public bool HasGenerator => MakeLit != null || MakeEffect != null;

        /// <summary>Cell <paramref name="index"/> of this sprite.</summary>
        public ArtCell Cell(int index) => new ArtCell(index, Frames, Directions, ArtManifest.Seed);

        /// <summary>The key the build caches <paramref name="cell"/>'s rendered frame under.</summary>
        public string KeyFor(ArtCell cell) => Key != null ? Key(cell) : $"{Id} {cell.Index}";

        public override string ToString() => $"{Id} ({Kind}, {WidthTiles}×{HeightTiles}, {Frames}×{Directions})";
    }

    /// <summary>
    /// Every sprite the game draws (<c>docs/art/inventory.md</c> is the readable version). Tests fail when a
    /// unit, building or tile type has no entry, so a new type can't ship without art.
    /// </summary>
    public static partial class ArtManifest
    {
        /// <summary>The map seed the terrain variants are drawn with.</summary>
        public const int Seed = 1;

        /// <summary>Facings drawn for each unit (45° apart).</summary>
        public const int UnitDirections = 8;

        /// <summary>Facings drawn for a building whose rotation shows (90° apart, anticlockwise from east).</summary>
        public const int BuildingDirections = 4;

        /// <summary>Wall-style tiles: one variant per 4-neighbour mask (N, E, S, W).</summary>
        public const int AutotileVariants = 16;

        /// <summary>Damage states drawn for the player's wall (whole, damaged, nearly destroyed).</summary>
        public const int WallDamageStates = 3;

        /// <summary>The Gun turret's head (v0.13): drawn over its base (<c>GunTurret</c>) in 8 facings, 2 frames (rest, recoil).</summary>
        public const string TurretHead = "GunTurretHead";

        /// <summary>Facings drawn for a unit's turret (22.5° apart: quads don't rotate, and a barrel shows a coarser step).</summary>
        public const int TurretDirections = 16;

        /// <summary>
        /// A turreted unit's two layers, drawn over each other in a match: <c>Tank + HullSuffix</c> (move frames, 8 facings) and
        /// <c>Tank + TurretSuffix</c> (<see cref="Render.RenderGen.TurretLayer"/>'s frames, <see cref="TurretDirections"/> facings).
        /// The plain <c>Tank</c> stays whole for what shows the unit standing still: thumbnails and wrecks.
        /// </summary>
        public const string HullSuffix = "Hull", TurretSuffix = "Turret";

        /// <summary>The floor and rock feature stamps (<see cref="Render.RenderGen.FloorFeature"/>, <see cref="Render.RenderGen.RockFeature"/>).</summary>
        public const string FloorFeature = "FloorFeature", RockFeature = "RockFeature";

        private static ArtEntry[] Icons() => new[]
        {
            // Icons: command card (16×16) and cursors.
            new ArtEntry("IconMove", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconAttackMove", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconStop", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconHold", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconMiner", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconSpawner", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconWall", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconBuilder", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconDigger", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconTank", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconScout", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconHeavy", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconArtillery", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconSniper", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconFlamethrower", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconBomber", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconDetonate", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconBarracks", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconHeavyFactory", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconSiegeWorkshop", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconMine", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconRepair", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconRadar", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconGunTurret", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconJammer", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconDig", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconCancel", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("MarkElimination", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("MarkFight", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorDefault", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorMove", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorAttack", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorInvalid", ArtKind.Icon, 1, 1, 1, 1, false),

            // UI: 9-slice sources (one tile each; the UI scales them by slicing, not stretching).
            new ArtEntry("PanelFrame", ArtKind.Ui, 1, 1, 1, 1, false),
            new ArtEntry("ButtonFrame", ArtKind.Ui, 1, 1, 3, 1, false), // default, hover, disabled
        };

        private static readonly ArtEntry[] Entries = Join(Units(), Buildings(), Tiles(), Render.EffectGen.Manifest(), Icons());

        private static ArtEntry[] Join(params ArtEntry[][] parts)
        {
            var all = new List<ArtEntry>();
            foreach (ArtEntry[] part in parts) all.AddRange(part);
            return all.ToArray();
        }

        private static Dictionary<string, ArtEntry> byId;

        /// <summary>Every entry, in manifest order.</summary>
        public static IReadOnlyList<ArtEntry> All => Entries;

        /// <summary>The entry with this id (case-sensitive).</summary>
        public static bool TryGet(string id, out ArtEntry entry)
        {
            if (byId == null)
            {
                var map = new Dictionary<string, ArtEntry>(StringComparer.Ordinal);
                foreach (ArtEntry e in Entries) map[e.Id] = e;
                byId = map;
            }
            if (id != null && byId.TryGetValue(id, out entry)) return true;
            entry = default;
            return false;
        }
    }
}
