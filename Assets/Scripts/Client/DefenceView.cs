using Config;
using Mirror;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>
    /// What the client shows about its own defences (v0.13):
    /// the range a building's ring draws at (the Radar's sight, the Gun turret's range, the Jammer's jam radius,
    /// all from the config), whether one of the local player's buildings is unpaid (the server's flag, which only
    /// the owner's records carry), and whether a turret is firing (it shot within the last moments).
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
        /// True when one of the local player's finished buildings is unpaid now: the server's flag, sent in the owner's
        /// own building records only (<see cref="BuildingRecord.Unpaid"/>), so it is never set on anyone else's.
        /// </summary>
        public static bool Unpaid(in ClientBuildings.Entry e) => e.Unpaid && !e.Ghost && e.Progress >= 100;

        /// <summary>True when turret <paramref name="id"/> shot within its attack interval plus <see cref="FiringGraceSeconds"/> of <paramref name="time"/> (<c>Time.time</c>).</summary>
        public static bool Firing(ClientBuildings known, int id, float time)
        {
            float interval = ConfigLoader.LoadConfig().GetBuilding(BuildingType.GunTurret).AttackInterval;
            return time - known.HeadOf(id).FiredAt <= interval + FiringGraceSeconds;
        }
    }
}
