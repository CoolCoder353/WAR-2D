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

        /// <summary>Facings drawn for a building whose rotation shows (90° apart, anticlockwise from east).</summary>
        public const int BuildingDirections = 4;

        /// <summary>Wall-style tiles: one variant per 4-neighbour mask (N, E, S, W).</summary>
        public const int AutotileVariants = 16;

        /// <summary>Damage states drawn for the player's wall (whole, damaged, nearly destroyed).</summary>
        public const int WallDamageStates = 3;

        /// <summary>The Gun turret's head (v0.13): drawn over its base (<c>GunTurret</c>) in 8 facings, 2 frames (rest, recoil).</summary>
        public const string TurretHead = "GunTurretHead";

        private static readonly ArtEntry[] Entries =
        {
            // Units: 1×1 tile, 8 facings; frames are move (2) then the unit's action (2).
            new ArtEntry(nameof(UnitType.Tank), ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry("Builder", ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry("Digger", ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry(nameof(UnitType.Scout), ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry(nameof(UnitType.Flamethrower), ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry(nameof(UnitType.Sniper), ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            new ArtEntry(nameof(UnitType.SuicideBomber), ArtKind.Unit, 1, 1, 4, UnitDirections, true),
            // Large units draw on 2×2-tile canvases.
            new ArtEntry(nameof(UnitType.Heavy), ArtKind.Unit, 2, 2, 4, UnitDirections, true),
            new ArtEntry(nameof(UnitType.Artillery), ArtKind.Unit, 2, 2, 4, UnitDirections, true),

            // Buildings: footprint from GameConfig.xml; drawn upright. Only the Miner's rotation shows (4 rendered facings).
            new ArtEntry(nameof(BuildingType.Base), ArtKind.Building, 5, 5, 1, 1, true),
            new ArtEntry(nameof(BuildingType.Barracks), ArtKind.Building, 2, 2, 2, 1, true),
            new ArtEntry(nameof(BuildingType.Miner), ArtKind.Building, 1, 1, 4, BuildingDirections, true),
            new ArtEntry("Bomb", ArtKind.Building, 1, 1, 2, 1, true),
            new ArtEntry("Radar", ArtKind.Building, 2, 2, 2, 1, true),
            new ArtEntry(nameof(BuildingType.HeavyFactory), ArtKind.Building, 3, 3, 2, 1, true),
            new ArtEntry(nameof(BuildingType.SiegeWorkshop), ArtKind.Building, 2, 2, 2, 1, true),
            // v0.13: the Gun turret's base (frame 1 is the damaged plinth, chosen by health, not looped) and its head
            // (drawn over the base in 8 facings; frame 1 is the recoil on a shot), and the Jammer.
            new ArtEntry(nameof(BuildingType.GunTurret), ArtKind.Building, 1, 1, 2, 1, true),
            new ArtEntry(TurretHead, ArtKind.Building, 1, 1, 2, UnitDirections, true),
            new ArtEntry(nameof(BuildingType.Jammer), ArtKind.Building, 2, 2, 2, 1, true),

            // Tiles: frames are variants (random for floor and gems, autotile masks for walls).
            new ArtEntry(nameof(TileType.Ground), ArtKind.Tile, 1, 1, 4, 1, false),
            new ArtEntry(nameof(TileType.Wall), ArtKind.Tile, 1, 1, AutotileVariants, 1, false),
            new ArtEntry(nameof(TileType.Gem), ArtKind.Tile, 1, 1, 4 * AutotileVariants, 1, false), // 4 variants × 16 masks, variant-major
            new ArtEntry(nameof(TileType.Border), ArtKind.Tile, 1, 1, 1, 1, false),
            new ArtEntry("PlayerWall", ArtKind.Tile, 1, 1, AutotileVariants * WallDamageStates, 1, true),
            new ArtEntry("WallBlueprint", ArtKind.Tile, 1, 1, AutotileVariants, 1, true),

            // Effects and overlays.
            new ArtEntry("Explosion", ArtKind.Effect, 2, 2, 6, 1, false),
            new ArtEntry("BombBlast", ArtKind.Effect, 3, 3, 8, 1, false),
            new ArtEntry("MuzzleFlash", ArtKind.Effect, 1, 1, 2, 1, false),
            new ArtEntry("Tracer", ArtKind.Effect, 1, 1, 1, 1, false),
            new ArtEntry("SniperTracer", ArtKind.Effect, 1, 1, 1, 1, false),
            new ArtEntry("ArtilleryShell", ArtKind.Effect, 1, 1, 2, 1, false),
            new ArtEntry("FlameStream", ArtKind.Effect, 3, 1, 4, 1, false),
            new ArtEntry("ShellImpact", ArtKind.Effect, 4, 4, 6, 1, false),
            new ArtEntry("BomberBlast", ArtKind.Effect, 4, 4, 8, 1, false),
            new ArtEntry("DigDebris", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("RepairSparks", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("ConstructionOverlay", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("SelectionRingSmall", ArtKind.Effect, 1, 1, 1, 1, false),
            new ArtEntry("SelectionRingLarge", ArtKind.Effect, 2, 2, 1, 1, false),
            new ArtEntry("WaypointFlag", ArtKind.Effect, 1, 1, 2, 1, false),
            new ArtEntry("RallyFlag", ArtKind.Effect, 1, 1, 2, 1, true),
            new ArtEntry("HealthBar", ArtKind.Effect, 1, 1, 2, 1, false),
            new ArtEntry("TurretMuzzle", ArtKind.Effect, 1, 1, 3, 1, false),
            new ArtEntry("JamPulse", ArtKind.Effect, 3, 3, 6, 1, false),
            // Polish (2026-10-30): ground marks that fade through their frames (scorch where a unit died, rubble sized to a
            // destroyed building's footprint, tread marks in 4 directions), smoke and fire on damaged buildings, a Miner's gem glint.
            new ArtEntry("Scorch", ArtKind.Effect, 1, 1, Render.EffectGen.DecalFrames, 1, false),
            new ArtEntry("ScorchLarge", ArtKind.Effect, 2, 2, Render.EffectGen.DecalFrames, 1, false),
            new ArtEntry("Rubble1", ArtKind.Effect, 1, 1, Render.EffectGen.DecalFrames, 1, false),
            new ArtEntry("Rubble2", ArtKind.Effect, 2, 2, Render.EffectGen.DecalFrames, 1, false),
            new ArtEntry("Rubble3", ArtKind.Effect, 3, 3, Render.EffectGen.DecalFrames, 1, false),
            new ArtEntry("Rubble5", ArtKind.Effect, 5, 5, Render.EffectGen.DecalFrames, 1, false),
            new ArtEntry("Tracks", ArtKind.Effect, 1, 1, Render.EffectGen.DecalFrames, Render.EffectGen.TrackDirections, false),
            new ArtEntry("Smoke", ArtKind.Effect, 1, 1, 6, 1, false),
            new ArtEntry("Fire", ArtKind.Effect, 1, 1, 4, 1, false),
            new ArtEntry("GemSparkle", ArtKind.Effect, 1, 1, 4, 1, false),

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
            new ArtEntry("IconSuicideBomber", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconDetonate", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconBarracks", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconHeavyFactory", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconSiegeWorkshop", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconBomb", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconRepair", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconRadar", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconGunTurret", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconJammer", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconDig", ArtKind.Icon, 1, 1, 1, 1, false),
            new ArtEntry("IconCancel", ArtKind.Icon, 1, 1, 1, 1, false),
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
