using System;
using System.Collections.Generic;

namespace WAR2D.Art
{
    /// <summary>What an art entry is for (and which atlas it is packed into).</summary>
    public enum ArtKind { Unit, Building, Tile, Effect, Icon, Ui }

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

        public ArtEntry(string id, ArtKind kind, int widthTiles, int heightTiles, int frames, int directions, bool teamMask)
        {
            Id = id;
            Kind = kind;
            WidthTiles = widthTiles;
            HeightTiles = heightTiles;
            Frames = frames;
            Directions = directions;
            TeamMask = teamMask;
        }

        public override string ToString() => $"{Id} ({Kind}, {WidthTiles}×{HeightTiles}, {Frames}×{Directions})";
    }

    /// <summary>
    /// Every sprite the game draws (<c>docs/art/inventory.md</c> is the readable version). Tests fail when a
    /// unit, building or tile type has no entry, so a new type can't ship without art.
    /// </summary>
    public static class ArtManifest
    {
        /// <summary>Facings drawn for each unit (45° apart).</summary>
        public const int UnitDirections = 8;

        /// <summary>Wall-style tiles: one variant per 4-neighbour mask (N, E, S, W).</summary>
        public const int AutotileVariants = 16;

        /// <summary>Damage states drawn for the player's wall (whole, damaged, nearly destroyed).</summary>
        public const int WallDamageStates = 3;

        private static readonly ArtEntry[] Entries =
        {
            // Units: 1×1 tile, 8 facings; frames are move (2) then the unit's action (2).
            new ArtEntry(nameof(UnitType.Tank), ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry("Builder", ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry("Digger", ArtKind.Unit, 1, 1, 4, UnitDirections, true),

            // Buildings: footprint from GameConfig.xml; rotated in 90° steps as whole sprites.
            new ArtEntry(nameof(BuildingType.Base), ArtKind.Building, 3, 3, 1, 1, true),
            new ArtEntry(nameof(BuildingType.SmallUnitSpawner), ArtKind.Building, 2, 2, 2, 1, true),
            new ArtEntry(nameof(BuildingType.Miner), ArtKind.Building, 1, 1, 4, 1, true),
            new ArtEntry("Bomb", ArtKind.Building, 1, 1, 2, 1, true),

            // Tiles: frames are variants (random for floor and gems, autotile masks for walls).
            new ArtEntry(nameof(TileType.Ground), ArtKind.Tile, 1, 1, 4, 1, false),
            new ArtEntry(nameof(TileType.Wall), ArtKind.Tile, 1, 1, AutotileVariants, 1, false),
            new ArtEntry(nameof(TileType.Gem), ArtKind.Tile, 1, 1, 4, 1, false),
            new ArtEntry(nameof(TileType.Border), ArtKind.Tile, 1, 1, 1, 1, false),
            new ArtEntry("PlayerWall", ArtKind.Tile, 1, 1, AutotileVariants * WallDamageStates, 1, true),
            new ArtEntry("WallBlueprint", ArtKind.Tile, 1, 1, AutotileVariants, 1, true),

            // Effects and overlays.
            new ArtEntry("Explosion", ArtKind.Effect, 2, 2, 6, 1, false),
            new ArtEntry("BombBlast", ArtKind.Effect, 3, 3, 8, 1, false),
            new ArtEntry("MuzzleFlash", ArtKind.Effect, 1, 1, 2, 1, false),
            new ArtEntry("Tracer", ArtKind.Effect, 1, 1, 1, 1, false),
            new ArtEntry("DigDebris", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("RepairSparks", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("ConstructionOverlay", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("SelectionRingSmall", ArtKind.Effect, 1, 1, 1, 1, false),
            new ArtEntry("SelectionRingLarge", ArtKind.Effect, 2, 2, 1, 1, false),
            new ArtEntry("WaypointFlag", ArtKind.Effect, 1, 1, 2, 1, false),
            new ArtEntry("RallyFlag", ArtKind.Effect, 1, 1, 2, 1, true),
            new ArtEntry("FogEdge", ArtKind.Effect, 1, 1, AutotileVariants, 1, false),
            new ArtEntry("HealthBar", ArtKind.Effect, 1, 1, 2, 1, false),

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
            new ArtEntry("IconBomb", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconRepair", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorDefault", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorMove", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorAttack", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("CursorInvalid", ArtKind.Icon, 1, 1, 1, 1, false),

            // UI: 9-slice sources (one tile each; the UI scales them by slicing, not stretching).
            new ArtEntry("PanelFrame", ArtKind.Ui, 1, 1, 1, 1, false),
            new ArtEntry("ButtonFrame", ArtKind.Ui, 1, 1, 3, 1, false), // default, hover, disabled
        };

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
