using Config;
using Mirror;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>
    /// What the client shows about its own defences (v0.13) from state it already has, with nothing new sent:
    /// the range a building's ring draws at (the Radar's sight, the Gun turret's range, the Jammer's jam radius,
    /// all from the config), whether one of the local player's buildings looks unpaid, and whether a turret is
    /// firing (it shot within the last moments).
    /// </summary>
    public static class DefenceView
    {
        /// <summary>Seconds after its last shot (beyond its attack interval) a turret still counts as firing.</summary>
        public const float FiringGraceSeconds = 1f;

        /// <summary>The radius in tiles a selected (or placed) building's ring draws at; 0 for a building with none.</summary>
        public static float RingRadius(BuildingType type)
        {
            GameConfigData config = ConfigLoader.LoadConfig();
            return type switch
            {
                BuildingType.Radar => config.GetBuilding(BuildingType.Radar).Sight,
                BuildingType.GunTurret => config.GetBuilding(BuildingType.GunTurret).Range,
                BuildingType.Jammer => config.Siege.JamRadius,
                _ => 0f,
            };
        }

        /// <summary>The local player, or null (not in a match).</summary>
        public static ClientPlayer Local => NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;

        /// <summary>
        /// True when a finished building of the local player's looks unpaid: the player's resources (sent to it
        /// after each second's charges) can't cover its running cost, so its last charge failed or the next will.
        /// The server's per-building flag never reaches clients; this is the client's reading of its own budget.
        /// </summary>
        public static bool LooksUnpaid(in ClientBuildings.Entry e)
        {
            ClientPlayer local = Local;
            if (local == null || e.Ghost || e.Progress < 100 || e.Data.ownerId != BuildingData.UIntToInt(local.netId)) return false;
            float cost = ConfigLoader.LoadConfig().GetBuilding(e.Data.buildingType).RunningCost;
            return cost > 0f && local.currentResources < cost;
        }

        /// <summary>True when turret <paramref name="id"/> shot within its attack interval plus <see cref="FiringGraceSeconds"/> of <paramref name="time"/> (<c>Time.time</c>).</summary>
        public static bool Firing(ClientBuildings known, int id, float time)
        {
            float interval = ConfigLoader.LoadConfig().GetBuilding(BuildingType.GunTurret).AttackInterval;
            return time - known.HeadOf(id).FiredAt <= interval + FiringGraceSeconds;
        }
    }
}
