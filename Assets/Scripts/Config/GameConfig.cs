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
        public ResourceConfig Resources = new ResourceConfig();
        public MatchConfig Match = new MatchConfig();
        public Dictionary<UnitType, UnitConfig> Units = new Dictionary<UnitType, UnitConfig>();
        public Dictionary<BuildingType, BuildingConfig> Buildings = new Dictionary<BuildingType, BuildingConfig>();
        /// <summary>Server simulation tick and its job tuning.</summary>
        public SimulationConfig Simulation = new SimulationConfig();
        /// <summary>Unit replication encoder tuning.</summary>
        public ReplicationConfig Replication = new ReplicationConfig();
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

    public class ResourceConfig
    {
        public float PassiveGenerationRate;
        public float StartingResources;
        public float MiningRate;
        /// <summary>Percent of max health lost per second while upkeep is unpaid.</summary>
        public float DecayPercentPerSecond;
    }

    public class MatchConfig
    {
        public string Scene = "Map_2";
        public float CountdownSeconds;
        /// <summary>The match map: generated from a seed, or the scene's tilemaps.</summary>
        public MapConfig Map = new MapConfig();
    }

    public class MapConfig
    {
        /// <summary>Tiles per side of the generated map; 0 uses the scene's tilemaps.</summary>
        public int Size;
        /// <summary>Generator seed; 0 picks a new random seed every match.</summary>
        public uint Seed;
        /// <summary>Share of floor-facing rock that becomes gem.</summary>
        public float GemChance;
    }

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

    public class UnitConfig
    {
        public int Health;
        public int Damage;
        public float Range;
        /// <summary>Seconds between attacks.</summary>
        public float AttackInterval;
        public float MoveSpeed;
        public float Acceleration;
        public float UpfrontCost;
        public float RunningCost;
        /// <summary>Collision radius in tiles.</summary>
        public float Radius;
        /// <summary>Pathing size class: 0 small, 1 large.</summary>
        public int SizeClass;
        /// <summary>Sight radius in tiles.</summary>
        public float Sight;
    }

    public class BuildingConfig
    {
        public int Health;
        public int Width;
        public int Height;
        public float UpfrontCost;
        public float RunningCost;
        /// <summary>Units per second a spawner may produce (0 for non-spawners).</summary>
        public float SpawnRate;
        /// <summary>Sight radius in tiles.</summary>
        public float Sight;

        public int2 Size => new int2(Width, Height);
    }
}
