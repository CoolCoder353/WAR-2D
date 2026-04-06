using System.Collections.Generic;
using UnityEngine;

namespace Config
{
    [System.Serializable]
    public class GameConfigData
    {
        public ResourceConfig Resources = new ResourceConfig();
        public Dictionary<string, UnitConfig> Units = new Dictionary<string, UnitConfig>();
        public Dictionary<string, BuildingConfig> Buildings = new Dictionary<string, BuildingConfig>();
    }

    [System.Serializable]
    public class ResourceConfig
    {
        public float PassiveGenerationRate;
        public float StartingResources;
        public float MiningRate;
    }

    [System.Serializable]
    public class UnitConfig
    {
        public int Health;
        public int Damage;
        public float MoveSpeed;
        public float UpfrontCost;
        public float RunningCost;
    }

    [System.Serializable]
    public class BuildingConfig
    {
        public int Health;
        public float UpfrontCost;
        public float RunningCost;
        public float MiningRate; // Optional, for miners
        public float SpawnRate;  // Optional, for spawners
    }
}
