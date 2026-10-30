using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// The forces the fight model compares (AI overhaul §3.1), built only from what the AI may know: its own units, enemy
    /// units in its view now, the enemy turrets it knows (seen or ghosts), and its enemy models' remembered armies.
    /// </summary>
    public static class AiForces
    {
        /// <summary>
        /// Its own fighting units (not Builders): those in <paramref name="group"/> when it is given, within
        /// <paramref name="radius"/> of <paramref name="at"/> when the radius is above 0.
        /// </summary>
        public static Force Own(AiContext ai, ICollection<int> group = null, float2 at = default, float radius = 0f)
        {
            var f = new Force();
            float r2 = radius * radius;
            foreach (AiUnit u in ai.View.OwnUnits)
            {
                if (u.Type == UnitType.Builder) continue;
                if (group != null && !group.Contains(u.Id)) continue;
                if (radius > 0f && math.distancesq(u.Position, at) > r2) continue;
                FightModel.AddUnit(ref f, ai.Game, u.Type, Share(u.Health, u.MaxHealth), ai.Policy);
            }
            return f;
        }

        /// <summary>The enemy units it sees now within <paramref name="radius"/> of a point, plus the enemy turrets it knows there.</summary>
        public static Force EnemyNear(AiContext ai, float2 at, float radius)
        {
            var f = new Force();
            float r2 = radius * radius;
            TeamView view = ai.View;
            foreach (AiUnit u in view.OtherUnits)
                if (view.IsEnemy(u.OwnerId) && math.distancesq(u.Position, at) <= r2)
                    FightModel.AddUnit(ref f, ai.Game, u.Type, Share(u.Health, u.MaxHealth), ai.Policy);
            f.Add(KnownTurrets(ai, at, radius));
            return f;
        }

        /// <summary>The finished enemy turrets it knows (seen now or as ghosts) within <paramref name="radius"/> of a point (of one owner when <paramref name="owner"/> ≥ 0).</summary>
        public static Force KnownTurrets(AiContext ai, float2 at, float radius, int owner = -1)
        {
            var f = new Force();
            float r2 = radius * radius;
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
            {
                if (owner >= 0 && b.OwnerId != owner) continue;
                if (b.Progress < 100 || !IsAttacker(ai.Game, b.Type)) continue;
                if (math.distancesq((float2)b.Anchor + 0.5f, at) > r2) continue;
                FightModel.AddTurret(ref f, ai.Game, b.Type, Share(b.Health, b.MaxHealth));
            }
            return f;
        }

        /// <summary>
        /// An enemy's army as the AI remembers it (AI overhaul §4.2): its model's peak counts at full health, else its last
        /// sighting, else nothing.
        /// </summary>
        public static Force EnemyArmy(AiContext ai, int owner)
        {
            var f = new Force();
            if (ai.Board.Models.TryGetValue(owner, out EnemyModel m))
            {
                for (int t = 0; t < m.Peak.Length; t++)
                {
                    int n = (int)math.round(m.Peak[t]);
                    for (int k = 0; k < n; k++) FightModel.AddUnit(ref f, ai.Game, (UnitType)t, 1f, ai.Policy);
                }
                if (!f.Empty) return f;
            }
            if (ai.Board.Armies.TryGetValue(owner, out ArmySighting s) && s.ByType != null)
                for (int t = 0; t < s.ByType.Length; t++)
                    for (int k = 0; k < s.ByType[t]; k++) FightModel.AddUnit(ref f, ai.Game, (UnitType)t, 1f, ai.Policy);
            return f;
        }

        /// <summary>
        /// Our advantage over <paramref name="theirs"/> as this AI judges it: the fight model with its range weight, times
        /// its estimate noise (exact at full insight).
        /// </summary>
        public static float Advantage(AiContext ai, in Force ours, in Force theirs) =>
            FightModel.Advantage(ours, theirs, ai.Policy.RangeWeight) * ai.Noise();

        private static bool IsAttacker(GameConfigData game, BuildingType t) => game.Buildings.TryGetValue(t, out BuildingConfig c) && c.IsAttacker;

        private static float Share(float health, float max) => max > 0f ? health / max : 1f;
    }
}
