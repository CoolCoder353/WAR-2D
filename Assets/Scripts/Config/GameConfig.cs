using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>What an attack hits, for the damage table.</summary>
public enum TargetClass : byte { Unit, Building, Wall }

namespace Config
{
    /// <summary>
    /// All gameplay balance values, loaded from Resources/GameConfig.xml by ConfigLoader.
    /// </summary>
    public class GameConfigData
    {
        /// <summary>Passive income, starting balance, mining and decay.</summary>
        public ResourceConfig Resources = new ResourceConfig();
        /// <summary>Match scene, countdown and map.</summary>
        public MatchConfig Match = new MatchConfig();
        /// <summary>Unit stats by type.</summary>
        public Dictionary<UnitType, UnitConfig> Units = new Dictionary<UnitType, UnitConfig>();
        /// <summary>Building stats by type.</summary>
        public Dictionary<BuildingType, BuildingConfig> Buildings = new Dictionary<BuildingType, BuildingConfig>();
        /// <summary>Server simulation tick and its job tuning.</summary>
        public SimulationConfig Simulation = new SimulationConfig();
        /// <summary>Unit replication encoder tuning.</summary>
        public ReplicationConfig Replication = new ReplicationConfig();
        /// <summary>Unit orders (Shift-queued waypoints).</summary>
        public OrdersConfig Orders = new OrdersConfig();
        /// <summary>Player-controlled diplomacy.</summary>
        public DiplomacyConfig Diplomacy = new DiplomacyConfig();
        /// <summary>Resource gifting between players.</summary>
        public GiftingConfig Gifting = new GiftingConfig();
        /// <summary>The alert feed.</summary>
        public AlertsConfig Alerts = new AlertsConfig();
        /// <summary>Construction, walls, digging, bombs and Radars (v0.7).</summary>
        public SiegeConfig Siege = new SiegeConfig();
        /// <summary>Music switching.</summary>
        public AudioConfig Audio = new AudioConfig();
        public ReplaysConfig Replays = new ReplaysConfig();
        /// <summary>What the host may pick in the lobby.</summary>
        public LobbyConfig Lobby = new LobbyConfig();
        /// <summary>The battle behind the main menu.</summary>
        public MenuBattleConfig MenuBattle = new MenuBattleConfig();
        /// <summary>Damage multipliers per attacker type and target class.</summary>
        public DamageTableConfig Damage = new DamageTableConfig();

        /// <summary>Returns the config for a unit type. Throws if it is missing (validation guarantees presence).</summary>
        public UnitConfig GetUnit(UnitType type)
        {
            if (Units.TryGetValue(type, out UnitConfig config)) return config;
            throw new KeyNotFoundException($"GameConfig.xml has no <Unit type=\"{type}\">.");
        }

        /// <summary>Returns the config for a building type. Throws if it is missing (validation guarantees presence).</summary>
        public BuildingConfig GetBuilding(BuildingType type)
        {
            if (Buildings.TryGetValue(type, out BuildingConfig config)) return config;
            throw new KeyNotFoundException($"GameConfig.xml has no <Building type=\"{type}\">.");
        }
    }

    /// <summary>Economy rates.</summary>
    public class ResourceConfig
    {
        /// <summary>Resources per second every player earns without mining.</summary>
        public float PassiveGenerationRate;
        /// <summary>Balance at the start of a match (the lobby may override it).</summary>
        public float StartingResources;
        /// <summary>Resources per second a working miner earns.</summary>
        public float MiningRate;
        /// <summary>Percent of max health lost per second while upkeep is unpaid.</summary>
        public float DecayPercentPerSecond;
    }

    /// <summary>Match setup.</summary>
    public class MatchConfig
    {
        /// <summary>The match scene to load.</summary>
        public string Scene = "Map_2";
        /// <summary>Seconds between the last HQ placed and the start of play.</summary>
        public float CountdownSeconds;
        /// <summary>The match map: generated from a seed, or the scene's tilemaps.</summary>
        public MapConfig Map = new MapConfig();
    }

    /// <summary>Map generation.</summary>
    public class MapConfig
    {
        /// <summary>Tiles per side of the generated map; 0 uses the scene's tilemaps.</summary>
        public int Size;
        /// <summary>Generator seed; 0 picks a new random seed every match.</summary>
        public uint Seed;
        /// <summary>Share of floor-facing rock that becomes gem.</summary>
        public float GemChance;
        /// <summary>Sizes the layout tables below are given for, smallest first; other sizes use the next smaller row.</summary>
        public int[] LayoutSizes = System.Array.Empty<int>();
        /// <summary>Start sites per entry of <see cref="LayoutSizes"/>.</summary>
        public int[] StartSites = System.Array.Empty<int>();
        /// <summary>Rich expansions per entry of <see cref="LayoutSizes"/>.</summary>
        public int[] Expansions = System.Array.Empty<int>();
        /// <summary>Large central areas per entry of <see cref="LayoutSizes"/>.</summary>
        public int[] CentralAreas = System.Array.Empty<int>();
        /// <summary>Region radii, in percent of the base sizes, per entry of <see cref="LayoutSizes"/>: larger maps get larger, roomier regions.</summary>
        public int[] RegionScale = System.Array.Empty<int>();
        /// <summary>Lane widths (not chokepoints), in percent of <see cref="LaneWidthMin"/>..<see cref="LaneWidthMax"/>, per entry of <see cref="LayoutSizes"/>.</summary>
        public int[] LaneScale = System.Array.Empty<int>();
        /// <summary>Depth in tiles of the cave band grown out from every region and lane edge, per entry of <see cref="LayoutSizes"/> (0 = smooth edges).</summary>
        public int[] CaveDepth = System.Array.Empty<int>();
        /// <summary>Share of a central area's or expansion's floor given to rock outcrops (cover and flanks inside the open ground).</summary>
        public float OutcropShare;
        /// <summary>Share of lanes that get a chokepoint.</summary>
        public float ChokepointShare;
        public int LaneWidthMin, LaneWidthMax, ChokeWidthMin, ChokeWidthMax;
        /// <summary>Path distance band between a start site and its nearest other site, as a share of the map size.</summary>
        public float MinStartDistance, MaxStartDistance;
        /// <summary>Gem tiles in each expansion's rich field.</summary>
        public int ExpansionGemTiles;
    }

    /// <summary>Server simulation tuning.</summary>
    public class SimulationConfig
    {
        /// <summary>Server simulation ticks per second.</summary>
        public int TickRate;
        /// <summary>Ticks over which idle units' target searches are spread.</summary>
        public int TargetSearchSliceTicks;
        /// <summary>Separation runs every this many ticks.</summary>
        public int SeparationIntervalTicks;
        /// <summary>Strength of the push between overlapping units.</summary>
        public float SeparationStrength;
        /// <summary>Spatial hash cell size in tiles.</summary>
        public int HashCellSize;
        /// <summary>Flow-field sector rebuilds scheduled per tick.</summary>
        public int MaxFieldRebuildsPerTick;
        /// <summary>Most units one player may own.</summary>
        public int MaxUnitsPerPlayer;
        /// <summary>Capacity of the network id index (units and buildings).</summary>
        public int MaxEntities;
        /// <summary>Fog of war grid cell size in tiles.</summary>
        public int FogCellSize;
        /// <summary>Fog of war is recomputed every this many ticks.</summary>
        public int VisionIntervalTicks;

        /// <summary>Seconds per simulation tick.</summary>
        public float TickSeconds => 1f / TickRate;
    }

    /// <summary>Unit order tuning.</summary>
    public class OrdersConfig
    {
        /// <summary>Shift-queued waypoints kept per unit; extras are dropped.</summary>
        public int MaxQueued;
    }

    /// <summary>Diplomacy tuning.</summary>
    public class DiplomacyConfig
    {
        /// <summary>Seconds a player waits between changes, per command.</summary>
        public float ChangeCooldownSeconds;
    }

    /// <summary>Main menu battle.</summary>
    public class MenuBattleConfig
    {
        /// <summary>Units per army in the battle behind the main menu.</summary>
        public int UnitsPerArmy;
        /// <summary>Tiles per side of its generated map.</summary>
        public int MapSize;
        /// <summary>Its map seed.</summary>
        public uint Seed;
    }

    /// <summary>Lobby choices.</summary>
    public class LobbyConfig
    {
        /// <summary>Map sizes the host may pick (tiles per side), smallest first.</summary>
        public int[] MapSizes = System.Array.Empty<int>();
        /// <summary>Starting resources the host may pick, smallest first.</summary>
        public float[] StartingResources = System.Array.Empty<float>();
        /// <summary>Smallest and largest number of start options a player may be dealt.</summary>
        public int StartOptionsMin, StartOptionsMax, StartOptionsDefault;
        /// <summary>Timed pick durations the host may choose, in seconds (0 = random, and Unlimited is always allowed).</summary>
        public int[] StartPickSeconds = System.Array.Empty<int>();
        /// <summary>The default pick time (a value of <see cref="StartPickSeconds"/>).</summary>
        public int StartPickDefault;
        /// <summary>Sites kept undealt, so an enemy's position is never pinned down by elimination.</summary>
        public int SpareStartSites;
    }

    /// <summary>Gifting tuning.</summary>
    public class GiftingConfig
    {
        /// <summary>Seconds a player waits between gifts.</summary>
        public float CooldownSeconds;
    }

    /// <summary>Match recordings (v0.10).</summary>
    public class ReplaysConfig
    {
        public bool Enabled;
        public int MaxMegabytes;
        public int SendKilobytesPerSecond;
        public int KeepCount;
    }

    /// <summary>Music tuning.</summary>
    public class AudioConfig
    {
        /// <summary>Deaths the player sees within one second that switch the music to the battle track.</summary>
        public int BattleDeathsPerSecond;
        /// <summary>Seconds the battle track holds after the last such second before going back to calm.</summary>
        public float BattleHoldSeconds;
    }

    /// <summary>Construction, walls, digging, bombs and Radars (v0.7).</summary>
    public class SiegeConfig
    {
        /// <summary>Health per second one Builder adds to a blueprint, a rising wall or (for a price) a damaged building.</summary>
        public float BuildPower;
        /// <summary>Share of its cost (0..1) a completed building refunds when its owner demolishes it; blueprints refund in full.</summary>
        public float DemolishRefund;
        /// <summary>Resources per point of health a repair restores.</summary>
        public float RepairCostPerHealth;
        /// <summary>How far (tiles, from the body's edge) a Builder or Digger reaches its target.</summary>
        public float WorkReach;
        /// <summary>Seconds a Digger takes to turn one rock or gem tile into floor.</summary>
        public float DigTime;
        /// <summary>Resources a dug gem tile pays the Digger's owner.</summary>
        public float GemPayout;
        /// <summary>Resources per wall segment, taken when it is placed.</summary>
        public float WallCost;
        /// <summary>A finished wall segment's health.</summary>
        public int WallHealth;
        /// <summary>Most segments one wall drag may lay.</summary>
        public int MaxWallLine;
        /// <summary>Radius in tiles a bomb blast reaches.</summary>
        public float BlastRadius;
        /// <summary>Damage a bomb blast deals to every unit, building and wall it reaches.</summary>
        public float BlastDamage;
        /// <summary>Seconds a Radar's radius must stay clear of enemies before it may ping again.</summary>
        public float RadarClearSeconds;
        /// <summary>Least seconds between two pings from one Radar.</summary>
        public float RadarAlertCooldownSeconds;
    }

    /// <summary>Alert tuning.</summary>
    public class AlertsConfig
    {
        /// <summary>Seconds an Under-attack alert for one area stays quiet after it fires.</summary>
        public float ThrottleSeconds;
        /// <summary>Side of the square area (in tiles) that shares one Under-attack throttle.</summary>
        public int AreaTiles;
        /// <summary>Seconds an alert stays in the feed unless dismissed.</summary>
        public float ShowSeconds;
    }

    /// <summary>Unit replication tuning.</summary>
    public class ReplicationConfig
    {
        /// <summary>Ticks between correction passes for units in view.</summary>
        public int CorrectionIntervalTicks;
        /// <summary>Prediction error (tiles) that triggers a correction in view.</summary>
        public float CorrectionThreshold;
        /// <summary>Delta-correction quantisation steps per tile (0 disables deltas).</summary>
        public int DeltaScale;
        /// <summary>Prediction error (tiles) that triggers a correction outside the view.</summary>
        public float OffscreenThreshold;
        /// <summary>Ticks between correction passes for units outside the view.</summary>
        public int OffscreenIntervalTicks;
        /// <summary>Rate at which a joining client's snapshot is released.</summary>
        public int SnapshotBytesPerSecond;
        /// <summary>Tiles a sent route may cut across the flow cells it simplifies (0 keeps every turn).</summary>
        public float RouteTolerance;
    }

    /// <summary>Damage multipliers keyed by attacker unit type and target class.</summary>
    public class DamageTableConfig
    {
        /// <summary>Multipliers read from the &lt;DamageTable&gt; entries.</summary>
        public Dictionary<(UnitType, TargetClass), float> Entries = new Dictionary<(UnitType, TargetClass), float>();

        /// <summary>The multiplier for an attacker hitting a target class; 1 when no entry exists.</summary>
        public float Multiplier(UnitType attacker, TargetClass target)
            => Entries.TryGetValue((attacker, target), out float m) ? m : 1f;
    }

    /// <summary>One unit type's stats.</summary>
    public class UnitConfig
    {
        /// <summary>Hit points.</summary>
        public int Health;
        /// <summary>Damage per attack before the damage table.</summary>
        public int Damage;
        /// <summary>Attack range in tiles.</summary>
        public float Range;
        /// <summary>Seconds between attacks.</summary>
        public float AttackInterval;
        /// <summary>Tiles per second.</summary>
        public float MoveSpeed;
        /// <summary>Parsed but not used yet: units reach full speed at once.</summary>
        public float Acceleration;
        /// <summary>Resources taken once, when built or spawned.</summary>
        public float UpfrontCost;
        /// <summary>Upkeep in resources per second.</summary>
        public float RunningCost;
        /// <summary>Collision radius in tiles.</summary>
        public float Radius;
        /// <summary>Pathing size class: 0 small, 1 large.</summary>
        public int SizeClass;
        /// <summary>Sight radius in tiles.</summary>
        public float Sight;
    }

    /// <summary>One building type's stats.</summary>
    public class BuildingConfig
    {
        /// <summary>Hit points.</summary>
        public int Health;
        /// <summary>Footprint width in tiles.</summary>
        public int Width;
        /// <summary>Footprint height in tiles.</summary>
        public int Height;
        /// <summary>Resources taken once, when built or spawned.</summary>
        public float UpfrontCost;
        /// <summary>Upkeep in resources per second.</summary>
        public float RunningCost;
        /// <summary>Units per second a spawner may produce (0 for non-spawners).</summary>
        public float SpawnRate;
        /// <summary>Sight radius in tiles.</summary>
        public float Sight;

        /// <summary>The footprint in tiles.</summary>
        public int2 Size => new int2(Width, Height);
    }
}
