using System.Collections.Generic;
using Unity.Mathematics;

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

        public int2 Size => new int2(Width, Height);
    }
}
