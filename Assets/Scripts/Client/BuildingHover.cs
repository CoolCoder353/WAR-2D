using Unity.Mathematics;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>What the building hover card shows (Figma v0.15 QoL redesign, item 12): only what the client already holds.</summary>
    public readonly struct BuildingHoverInfo
    {
        public readonly int Id, OwnerId;
        public readonly BuildingType Type;
        /// <summary>One of the local player's own buildings.</summary>
        public readonly bool Own;
        /// <summary>Out of sight: what is shown is how it was last seen.</summary>
        public readonly bool Ghost;
        /// <summary>Health as of now (as last seen for a ghost).</summary>
        public readonly float Health, MaxHealth;
        /// <summary>Construction progress in percent; 100 when finished.</summary>
        public readonly byte Progress;
        /// <summary>Own buildings only: the last upkeep charge failed.</summary>
        public readonly bool Unpaid;
        /// <summary>Own buildings only: upkeep per second.</summary>
        public readonly float Upkeep;
        /// <summary>Ghosts only: seconds since it went out of sight.</summary>
        public readonly float LastSeenSecondsAgo;
        /// <summary>Own finished Miners only (v0.17, Figma H1): what it mines, "Mining rich gem: 25/s" or "Mining: 10/s"; else null.</summary>
        public readonly string Mining;

        public BuildingHoverInfo(int id, int ownerId, BuildingType type, bool own, bool ghost, float health, float maxHealth, byte progress, bool unpaid, float upkeep, float lastSeenSecondsAgo, string mining = null)
        {
            Mining = mining;
            Id = id;
            OwnerId = ownerId;
            Type = type;
            Own = own;
            Ghost = ghost;
            Health = health;
            MaxHealth = maxHealth;
            Progress = progress;
            Unpaid = unpaid;
            Upkeep = upkeep;
            LastSeenSecondsAgo = lastSeenSecondsAgo;
        }

        public float Health01 => MaxHealth > 0f ? Health / MaxHealth : 0f;
    }

    /// <summary>
    /// Looks up the building under a tile for the hover card. It reads the client's own building set, so it can never show
    /// more than the client knows: no production for an enemy building, and a ghost keeps its last-seen health.
    /// </summary>
    public static class BuildingHover
    {
        /// <summary>Describes the building covering <paramref name="tile"/>; false when none.</summary>
        public static bool TryDescribe(BuildingPicker picker, ClientBuildings buildings, Config.GameConfigData config, int2 tile, int localOwner, float now, out BuildingHoverInfo info)
        {
            info = default;
            if (picker == null || buildings == null || !picker.TryPick(tile, out int id) || !buildings.Entries.TryGetValue(id, out ClientBuildings.Entry e)) return false;
            bool own = e.Data.ownerId == localOwner;
            float upkeep = own && config != null ? config.GetBuilding(e.Data.buildingType)?.RunningCost ?? 0f : 0f;
            float ago = e.Ghost ? System.Math.Max(0f, now - e.LastSeenAt) : 0f;
            string mining = null;
            if (own && !e.Ghost && e.Progress >= 100 && e.Data.buildingType == BuildingType.Miner && config != null)
                mining = MiningLine(FacedTile(e), config.Resources.MiningRate, config.Resources.RichGemMultiplier);
            info = new BuildingHoverInfo(id, e.Data.ownerId, e.Data.buildingType, own, e.Ghost, e.Health.currentHealth, e.Health.maxHealth, e.Progress, own && e.Unpaid, upkeep, ago, mining);
            return true;
        }

        /// <summary>
        /// An own Miner's footer line (v0.17, Figma H1): "Mining rich gem: 25/s" facing a rich gem, "Mining: 10/s" facing a
        /// gem, null facing anything else.
        /// </summary>
        public static string MiningLine(TileType faced, float miningRate, float richMultiplier)
        {
            float rate = miningRate * MinerRules.RateOf(faced, richMultiplier);
            return faced == TileType.RichGem ? $"Mining rich gem: {rate:0.#}/s" : faced == TileType.Gem ? $"Mining: {rate:0.#}/s" : null;
        }

        /// <summary>The tile a Miner faces, from the client's terrain (Border when unknown).</summary>
        private static TileType FacedTile(in ClientBuildings.Entry e)
        {
            int2 tile = (int2)math.round(e.Data.position) + MinerRules.FacingOffset(e.Data.rotation);
            ClientTerrain terrain = ClientTerrain.Current;
            if (terrain.Width <= 0 || tile.x < 0 || tile.y < 0 || tile.x >= terrain.Width) return TileType.Border;
            int index = tile.y * terrain.Width + tile.x;
            return index < terrain.Tiles.Length ? (TileType)terrain.Tiles[index] : TileType.Border;
        }

        /// <summary>Seconds as "m:ss" for the ghost card's "Last seen 2:14 ago".</summary>
        public static string Ago(float seconds)
        {
            int s = System.Math.Max(0, (int)seconds);
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
