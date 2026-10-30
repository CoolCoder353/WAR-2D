using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// How strong a force is, from what the AI has seen (plan Task 7): each unit counts its cost scaled by the health it
    /// has left, a Gun turret its cost times <c>TurretStrength</c>; Builders count nothing. Only units and buildings in
    /// its view or on its blackboard (last sightings, ghosts) are ever counted, never hidden facts.
    /// </summary>
    public static class StrengthEstimate
    {
        /// <summary>One unit type's full-health worth.</summary>
        public static float Value(GameConfigData game, UnitType type)
        {
            if (type == UnitType.Builder || type == UnitType.None) return 0f;
            return game.Units.TryGetValue(type, out UnitConfig c) ? c.UpfrontCost : 0f;
        }

        /// <summary>A unit as seen: its worth times its health share.</summary>
        public static float Of(GameConfigData game, in AiUnit unit)
        {
            float share = unit.MaxHealth > 0f ? math.saturate(unit.Health / unit.MaxHealth) : 1f;
            return Value(game, unit.Type) * share;
        }

        /// <summary>A last-seen army (counts by type, at full health: the sighting kept no health).</summary>
        public static float Of(GameConfigData game, in ArmySighting sighting)
        {
            if (sighting.ByType == null) return 0f;
            float sum = 0f;
            for (int t = 0; t < sighting.ByType.Length; t++)
                if (sighting.ByType[t] > 0) sum += sighting.ByType[t] * Value(game, (UnitType)t);
            return sum;
        }

        /// <summary>A Gun turret's worth (finished ones; a blueprint doesn't fire).</summary>
        public static float Of(GameConfigData game, in AiBuilding building, float turretStrength)
        {
            if (building.Type != BuildingType.GunTurret || building.Progress < 100) return 0f;
            return game.Buildings.TryGetValue(building.Type, out BuildingConfig c) ? c.UpfrontCost * turretStrength : 0f;
        }

        /// <summary>Its own fighting units within <paramref name="radius"/> of a point (any radius ≤ 0: all of them).</summary>
        public static float Own(GameConfigData game, TeamView view, float2 at, float radius)
        {
            float r2 = radius * radius, sum = 0f;
            foreach (AiUnit u in view.OwnUnits)
                if (radius <= 0f || math.distancesq(u.Position, at) <= r2) sum += Of(game, u);
            return sum;
        }

        /// <summary>The enemy units it sees now within <paramref name="radius"/> of a point, plus the enemy turrets it knows there.</summary>
        public static float EnemyNear(GameConfigData game, TeamView view, float2 at, float radius, float turretStrength)
        {
            float r2 = radius * radius, sum = 0f;
            foreach (AiUnit u in view.OtherUnits)
                if (view.IsEnemy(u.OwnerId) && math.distancesq(u.Position, at) <= r2) sum += Of(game, u);
            foreach (AiBuilding b in view.Buildings)
                if (b.Type == BuildingType.GunTurret && b.OwnerId != view.Owner && view.IsEnemy(b.OwnerId) && math.distancesq((float2)b.Anchor + 0.5f, at) <= r2)
                    sum += Of(game, b, turretStrength);
            return sum;
        }

        /// <summary>
        /// An enemy's force as the AI last saw it: its last-seen army (within the army memory) plus the turrets it knows
        /// of that enemy's within <paramref name="radius"/> of <paramref name="at"/>.
        /// </summary>
        public static float Enemy(GameConfigData game, Blackboard board, int enemy, float2 at, float radius, float turretStrength)
        {
            float sum = 0f;
            if (board.Armies.TryGetValue(enemy, out ArmySighting army)) sum += Of(game, army);
            float r2 = radius * radius;
            foreach (AiBuilding b in board.EnemyBuildings.Values)
                if (b.OwnerId == enemy && b.Type == BuildingType.GunTurret && math.distancesq((float2)b.Anchor + 0.5f, at) <= r2)
                    sum += Of(game, b, turretStrength);
            return sum;
        }
    }
}
