using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>What a hit lands on, for the damage table: a Light or Heavy armoured unit, a building or a wall tile.</summary>
public enum TargetClass : byte { LightUnit, HeavyUnit, Building, Wall }

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
        /// <summary>v0.16: target lines for selected units (client presentation).</summary>
        public TargetLinesConfig TargetLines = new TargetLinesConfig();
        /// <summary>The Attack order's shared goal (v0.18).</summary>
        public AttackOrderConfig AttackOrder = new AttackOrderConfig();
        /// <summary>The held-key range rings (v0.18).</summary>
        public RangeRingsConfig RangeRings = new RangeRingsConfig();
        /// <summary>The end screen's graphs and timeline (v0.18).</summary>
        public SummaryConfig Summary = new SummaryConfig();
        /// <summary>Bombers' last gasp and leap (v0.18, v0.20).</summary>
        public BomberConfig Bomber = new BomberConfig();
        /// <summary>v0.20: the smallest direct hit, after flat armour.</summary>
        public CombatConfig Combat = new CombatConfig();
        /// <summary>v0.20: fire from Flamethrowers.</summary>
        public BurnConfig Burn = new BurnConfig();
        /// <summary>v0.20: Artillery shells in flight.</summary>
        public ArtilleryConfig Artillery = new ArtilleryConfig();
        /// <summary>v0.20: the Sniper's aim.</summary>
        public SniperConfig Sniper = new SniperConfig();
        /// <summary>v0.20: combat effects (client presentation).</summary>
        public EffectsConfig Effects = new EffectsConfig();
        /// <summary>v0.16: fights pulsing on the minimap (client presentation).</summary>
        public MinimapCombatConfig MinimapCombat = new MinimapCombatConfig();
        /// <summary>v0.16: the minimap while placing the HQ (client presentation).</summary>
        public PlacementMinimapConfig PlacementMinimap = new PlacementMinimapConfig();
        /// <summary>v0.16: formations for group moves.</summary>
        public FormationConfig Formation = new FormationConfig();
        /// <summary>v0.16: idle Builders building and repairing nearby.</summary>
        public AutoWorkConfig AutoWork = new AutoWorkConfig();
        public DefendConfig Defend = new DefendConfig();
        public PingsConfig Pings = new PingsConfig();
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
        /// <summary>The release's version and name (the main menu; the build's version).</summary>
        public ReleaseConfig Release = new ReleaseConfig();
        /// <summary>The battle behind the main menu.</summary>
        public MenuBattleConfig MenuBattle = new MenuBattleConfig();
        /// <summary>Damage multipliers per attacker type and target class.</summary>
        public DamageTableConfig Damage = new DamageTableConfig();
        /// <summary>AI players (v0.14).</summary>
        public AiConfig Ai = new AiConfig();

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
        /// <summary>v0.17: a Miner facing a rich gem earns <see cref="MiningRate"/> times this.</summary>
        public float RichGemMultiplier;
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
        /// <summary>
        /// Seconds a player who drops out of a running match has to come back (v0.15). Their units hold position and
        /// nothing is produced or charged meanwhile; at zero they are removed like a player who left.
        /// </summary>
        public float ReconnectSeconds;
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
        /// <summary>v0.17: rich gem tiles per central area, per entry of <see cref="LayoutSizes"/>.</summary>
        public int[] CentralRichGemTiles = System.Array.Empty<int>();
        /// <summary>v0.17: no rich gem within this lane distance of a start site, as a share of the map size.</summary>
        public float RichMinStartDistance;
        /// <summary>Playtest 2026-11-05: no rich gem within this many tiles (straight line) of a start site's centre.</summary>
        public int RichStartClearance;
        /// <summary>
        /// Playtest 2026-11-02: rock within this many tiles of the map's edge becomes undiggable border, so Diggers can't open
        /// a path around the map's rim (floor and gems there stay). 1 is the old one-tile border.
        /// </summary>
        public int EdgeTiles;
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

    /// <summary>The release (2026-11-02): one place for the version number and the update's name.</summary>
    public class ReleaseConfig
    {
        /// <summary>major.minor.patch; builds use it as their version (Steam joins compare it).</summary>
        public string Version = "";
        /// <summary>The update's name ("Clarity").</summary>
        public string Name = "";
        /// <summary>The main menu's line: "v0.16.0 Clarity".</summary>
        public string Display => string.IsNullOrEmpty(Name) ? $"v{Version}" : $"v{Version} {Name}";
    }

    /// <summary>Unit order tuning.</summary>
    public class OrdersConfig
    {
        /// <summary>Shift-queued waypoints kept per unit; extras are dropped.</summary>
        public int MaxQueued;
        /// <summary>The longest side of a Dig drag's area, in tiles.</summary>
        public int MaxDigArea;
    }

    /// <summary>The Attack order's shared goal (v0.18).</summary>
    public class AttackOrderConfig
    {
        /// <summary>Tiles a visible target moves before the shared goal follows it.</summary>
        public float RepathTiles;
        /// <summary>Least seconds between two moves of the shared goal.</summary>
        public float RepathSeconds;
    }

    /// <summary>The held-key range rings (v0.18).</summary>
    public class RangeRingsConfig
    {
        /// <summary>Most ring sets at once.</summary>
        public int Max;
        /// <summary>Share two circles of a type must overlap to share one ring.</summary>
        public float MergeOverlap;
    }

    /// <summary>The end screen's graphs and timeline (v0.18).</summary>
    public class SummaryConfig
    {
        /// <summary>Seconds between samples.</summary>
        public float SampleSeconds;
        /// <summary>Most samples per player.</summary>
        public int MaxPoints;
        /// <summary>Side of a fight cell, in tiles.</summary>
        public int FightCellTiles;
        /// <summary>Seconds a fight's worth is counted over, and the quiet that closes it.</summary>
        public float FightWindowSeconds;
        /// <summary>Worth killed within the window that opens a fight.</summary>
        public float FightMinWorth;
        /// <summary>Most fights kept per match, the largest.</summary>
        public int MaxFights;
    }

    /// <summary>Bombers' last gasp (v0.18) and leap (v0.20).</summary>
    public class BomberConfig
    {
        /// <summary>Share of its health at or below which a bomber explodes.</summary>
        public float LastGaspHealth;
        /// <summary>Most rounds of blasts setting off bombers in one tick.</summary>
        public int ChainPasses;
        /// <summary>Share of the blast that reaches own and allied units, buildings and walls.</summary>
        public float FriendlyDamage;
        /// <summary>v0.20: farthest a leap goes, in tiles.</summary>
        public float LeapReach;
        /// <summary>v0.20: seconds a leap takes, whatever its length.</summary>
        public float LeapSeconds;
        /// <summary>v0.20: enemy units within this many tiles of the nearest one are its army.</summary>
        public float ArmyTiles;
        /// <summary>v0.20: tiles beyond its body at which a Bomber touching its target goes off without leaping.</summary>
        public float ContactTiles;
    }

    /// <summary>Hits after armour (v0.20).</summary>
    public class CombatConfig
    {
        /// <summary>The least a direct hit does after flat armour.</summary>
        public float MinHit;
    }

    /// <summary>Fire (v0.20): a Flamethrower's hit sets its target burning.</summary>
    public class BurnConfig
    {
        /// <summary>Damage a second at the full rate.</summary>
        public float Dps;
        /// <summary>Seconds a fire burns after its last flame hit.</summary>
        public float Seconds;
        /// <summary>Rate for light units, heavy units and buildings (walls never burn).</summary>
        public float LightUnit, HeavyUnit, Building;

        /// <summary>The rate for a target class (0 for a wall).</summary>
        public float Rate(TargetClass c) => c switch
        {
            TargetClass.LightUnit => LightUnit,
            TargetClass.HeavyUnit => HeavyUnit,
            TargetClass.Building => Building,
            _ => 0f,
        };
    }

    /// <summary>Artillery shells (v0.20).</summary>
    public class ArtilleryConfig
    {
        /// <summary>Tiles a second a shell flies.</summary>
        public float ShellSpeed;
        /// <summary>Whether it aims where the target will be when the shell lands.</summary>
        public bool LeadAim;
        /// <summary>Tiles past its range a led shot may land.</summary>
        public float LeadCapTiles;
        /// <summary>Share of a shell's blast its own side and allies take.</summary>
        public float FriendlyDamage;
        /// <summary>Seconds of a shell's flight a client that sees only the landing point is shown.</summary>
        public float IncomingSeconds;
    }

    /// <summary>The Sniper's aim (v0.20).</summary>
    public class SniperConfig
    {
        /// <summary>Seconds a Sniper aims, standing still, before each shot.</summary>
        public float AimSeconds;
        /// <summary>Tiles a second under which it counts as standing still (a crowd's push aside).</summary>
        public float StillSpeed;
    }

    /// <summary>Combat effects (v0.20, client presentation).</summary>
    public class EffectsConfig
    {
        /// <summary>Most fires drawn at once, nearest the view's centre first.</summary>
        public int MaxFires;
        /// <summary>Seconds a burnt wreck smokes.</summary>
        public float SmoulderSeconds;
    }

    /// <summary>Target lines (v0.16).</summary>
    public class TargetLinesConfig
    {
        /// <summary>Seconds a line stays after the attacker's last shot.</summary>
        public float HoldSeconds;
        /// <summary>Most lines drawn at once.</summary>
        public int Max;
    }

    /// <summary>Fights on the minimap (v0.16).</summary>
    public class MinimapCombatConfig
    {
        /// <summary>Tiles per side of a pulse cell.</summary>
        public int CellTiles;
        /// <summary>Seconds a pulse grows and fades.</summary>
        public float PulseSeconds;
        /// <summary>Seconds a cell stays quiet after its pulse ends.</summary>
        public float QuietSeconds;
        /// <summary>Most pulses at once.</summary>
        public int Max;
    }

    /// <summary>The HQ-placement minimap (v0.16).</summary>
    public class PlacementMinimapConfig
    {
        /// <summary>Smallest on-screen size of an own start option's ring, in pixels.</summary>
        public int MarkerPixels;
    }

    /// <summary>Formations (v0.16).</summary>
    public class FormationConfig
    {
        /// <summary>Tiles between slots.</summary>
        public float Spacing;
        /// <summary>Within this many tiles of its slot a unit runs at its own speed.</summary>
        public float LooseTiles;
        /// <summary>Within this many tiles of the goal a unit steers to its slot.</summary>
        public float SlotTiles;
        /// <summary>A unit this much farther from the goal than its group's leader drops the group speed.</summary>
        public float LostTiles;
    }

    /// <summary>Map pings between players sharing vision (playtest 2026-11-04).</summary>
    public class PingsConfig
    {
        /// <summary>Least seconds between one player's pings.</summary>
        public float CooldownSeconds;
        /// <summary>Seconds a ping stays on the map and the minimap.</summary>
        public float ShowSeconds;
    }

    /// <summary>Idle units answering fire (playtest 2026-11-04).</summary>
    public class DefendConfig
    {
        /// <summary>Tiles from a shot unit or building within which idle units answer.</summary>
        public float RespondTiles;
        /// <summary>Tiles they go towards a shooter their side doesn't see.</summary>
        public float StepTiles;
        /// <summary>Side of the cells that make one alarm of many shooters.</summary>
        public float AlarmCellTiles;
        /// <summary>Ticks between answers.</summary>
        public int IntervalTicks;
    }

    /// <summary>Builders working on their own when idle (v0.16).</summary>
    public class AutoWorkConfig
    {
        /// <summary>Tiles an idle Builder looks for work.</summary>
        public float Radius;
        /// <summary>Health share below which an own building or wall is repaired.</summary>
        public float RepairBelow;
        /// <summary>Ticks between one idle Builder's looks.</summary>
        public int SliceTicks;
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
        /// <summary>The lobby's default visibility (v0.14): Public, Friends or Private.</summary>
        public LobbyVisibility Visibility = LobbyVisibility.Friends;
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
        public int KeepCount;
        /// <summary>Seconds between recorded fog changes (v0.18).</summary>
        public float FogSeconds;
        /// <summary>Seconds before its event a seek-bar marker seeks to (v0.18).</summary>
        public float MarkerLeadSeconds;
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
        /// <summary>Tiles from a hidden bomb's tile centre at which an enemy sets it off.</summary>
        public float MineTriggerRadius;
        /// <summary>Share of a hidden bomb's blast that reaches its own side.</summary>
        public float MineFriendlyDamage;
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
        /// <summary>v0.17: digging a rich gem pays this once (the tile is gone for good).</summary>
        public float RichGemPayout;
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
        /// <summary>Radius in tiles a complete, paid Jammer jams for owners its owner doesn't share vision with (v0.13).</summary>
        public float JamRadius;
        /// <summary>Tiles from which such an owner's sources still see a jammed fog cell (less than <see cref="JamRadius"/>).</summary>
        public float JamSight;
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

    /// <summary>Damage multipliers keyed by attacker (unit or building) type and target class.</summary>
    public class DamageTableConfig
    {
        /// <summary>Multipliers read from the &lt;DamageTable&gt; entries for unit attackers.</summary>
        public Dictionary<(UnitType, TargetClass), float> Entries = new Dictionary<(UnitType, TargetClass), float>();
        /// <summary>Multipliers read from the &lt;DamageTable&gt; entries for building attackers (v0.13: the Gun turret).</summary>
        public Dictionary<(BuildingType, TargetClass), float> BuildingEntries = new Dictionary<(BuildingType, TargetClass), float>();

        /// <summary>The multiplier for an attacker hitting a target class; 1 when no entry exists.</summary>
        public float Multiplier(UnitType attacker, TargetClass target)
            => Entries.TryGetValue((attacker, target), out float m) ? m : 1f;

        /// <summary>The multiplier for a building attacker hitting a target class; 1 when no entry exists.</summary>
        public float Multiplier(BuildingType attacker, TargetClass target)
            => BuildingEntries.TryGetValue((attacker, target), out float m) ? m : 1f;

        /// <summary>True when the table has at least one entry for the building attacker.</summary>
        public bool HasRow(BuildingType attacker)
        {
            foreach (var key in BuildingEntries.Keys) if (key.Item1 == attacker) return true;
            return false;
        }
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
        /// <summary>The armour class hits against this unit use: <see cref="TargetClass.LightUnit"/> or <see cref="TargetClass.HeavyUnit"/>.</summary>
        public TargetClass Armour;
        /// <summary>Seconds a producer takes to make one.</summary>
        public float BuildSeconds;
        /// <summary>Splash radius in tiles around the target (0 = none).</summary>
        public float Splash;
        /// <summary>Targets closer than this many tiles are ignored (0 = none).</summary>
        public float MinRange;
        /// <summary>
        /// Blast radius in tiles (0 for none): a Bomber's blast, or (v0.20) with no <see cref="BlastDamage"/> the radius of the
        /// shell's blast of a unit that fires shells (the Artillery), whose blast is its <see cref="Damage"/>.
        /// </summary>
        public float BlastRadius;
        /// <summary>Bomber blast damage before the damage table (0 for other units).</summary>
        public float BlastDamage;
        /// <summary>v0.20: share of any blast it shrugs off (0 to 0.99).</summary>
        public float BlastResist;
        /// <summary>v0.20: taken off every hit that is not a blast or fire, down to <c>Combat/MinHit</c>.</summary>
        public float FlatArmour;

        /// <summary>v0.20: true for a Bomber (it has a blast of its own and never fires).</summary>
        public bool IsBomber => BlastDamage > 0f;
        /// <summary>v0.20: true for a unit whose shots are shells that fly and land in a blast (the Artillery).</summary>
        public bool FiresShells => BlastRadius > 0f && BlastDamage <= 0f;
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
        /// <summary>Sight radius in tiles.</summary>
        public float Sight;
        /// <summary>Damage per attack before the damage table; 0 for a building that doesn't attack (v0.13).</summary>
        public int Damage;
        /// <summary>Attack range in tiles (attackers only).</summary>
        public float Range;
        /// <summary>Seconds between attacks (attackers only).</summary>
        public float AttackInterval;
        /// <summary>v0.20: seconds one Builder takes to build it, in place of health over <c>Siege/BuildPower</c> (0: the health rule).</summary>
        public float BuildSeconds;

        /// <summary>v0.20: health one Builder adds a second: health over <see cref="BuildSeconds"/> where given, else <paramref name="buildPower"/>.</summary>
        public float BuildRate(float buildPower) => BuildSeconds > 0f ? Health / BuildSeconds : buildPower;

        /// <summary>True for a building that attacks (<see cref="Damage"/> &gt; 0): the Gun turret.</summary>
        public bool IsAttacker => Damage > 0;

        /// <summary>The footprint in tiles.</summary>
        public int2 Size => new int2(Width, Height);
    }
}
