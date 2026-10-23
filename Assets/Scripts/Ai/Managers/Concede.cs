using System.Collections.Generic;
using Config;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Concede (spec §8): after <c>MinMinutes</c> of play it surrenders, through the same Surrender action a player
    /// uses, when for <c>Seconds</c> both hold: it can't rebuild an army (no producer of fighting units it can afford a
    /// unit from, and no Builder that could put one up it can afford, counting its resources plus <c>IncomeSeconds</c> of
    /// income), and its strength (army and buildings, by cost) is below <c>Ratio</c> of the strongest enemy force it saw
    /// in the last <c>SeenWindowSeconds</c>. Never while a teammate is still in the match: it fights to the end instead.
    /// </summary>
    public sealed class ConcedeManager : IAiManager
    {
        private struct Sighting
        {
            public double Time;
            public float Strength;
        }

        private readonly List<Sighting> seen = new List<Sighting>(64);
        private readonly Dictionary<int, float> byEnemy = new Dictionary<int, float>();
        private double hopelessSince = double.NaN;

        /// <summary>True once it has proposed its surrender.</summary>
        public bool Conceded { get; private set; }

        /// <summary>This think's readings (tests and the control API).</summary>
        public float OwnStrength { get; private set; }
        public float StrongestSeen { get; private set; }
        public bool CanRebuild { get; private set; }
        public bool TeammateHolds { get; private set; }

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            if (view == null || view.State != GameState.Playing) return;
            double now = ai.Now;
            AiConcedeConfig c = ai.Config.Concede;
            RecordStrongest(ai, view, now, c.SeenWindowSeconds);
            OwnStrength = Strength(ai, view);
            CanRebuild = MayRebuild(ai, view, c.IncomeSeconds);
            TeammateHolds = TeammateAlive(ai, view);

            bool hopeless = !TeammateHolds && !CanRebuild && StrongestSeen > 0f && OwnStrength < c.Ratio * StrongestSeen;
            if (!hopeless || ai.State.PlayingSeconds < c.MinMinutes * 60f) { hopelessSince = double.NaN; return; }
            if (double.IsNaN(hopelessSince)) hopelessSince = now;
            if (now - hopelessSince < c.Seconds) return;
            Conceded = true;
            ai.Propose(AiPriority.Critical, AiAction.Surrender(), AiKeys.Of(AiKeys.Surrender));
        }

        /// <summary>The strongest enemy force (per enemy, the units of theirs it sees now) over the window.</summary>
        private void RecordStrongest(AiContext ai, TeamView view, double now, float window)
        {
            byEnemy.Clear();
            foreach (AiUnit u in view.OtherUnits)
            {
                if (!view.IsEnemy(u.OwnerId)) continue;
                byEnemy.TryGetValue(u.OwnerId, out float s);
                byEnemy[u.OwnerId] = s + StrengthEstimate.Of(ai.Game, u);
            }
            float strongest = 0f;
            foreach (float s in byEnemy.Values) if (s > strongest) strongest = s;
            if (strongest > 0f) seen.Add(new Sighting { Time = now, Strength = strongest });
            int old = 0;
            while (old < seen.Count && now - seen[old].Time > window) old++;
            if (old > 0) seen.RemoveRange(0, old);
            StrongestSeen = 0f;
            foreach (Sighting s in seen) if (s.Strength > StrongestSeen) StrongestSeen = s.Strength;
        }

        /// <summary>Its army and buildings, by cost (units by health left, like every strength estimate).</summary>
        private static float Strength(AiContext ai, TeamView view)
        {
            float sum = StrengthEstimate.Own(ai.Game, view, default, 0f);
            foreach (AiBuilding b in view.Buildings)
                if (b.OwnerId == ai.Owner && !b.Ghost && b.Progress >= 100) sum += ai.State.CostOf(b.Type);
            return sum;
        }

        /// <summary>
        /// True when it can still make a fighting unit: a finished producer of one and money for the cheapest it makes,
        /// or a Builder (or an HQ to make one) and money for the cheapest such producer (and the Builder).
        /// </summary>
        private static bool MayRebuild(AiContext ai, TeamView view, float incomeSeconds)
        {
            AiState s = ai.State;
            float money = view.Resources + s.Income * incomeSeconds;
            float cheapestUnit = float.MaxValue;
            foreach (int i in s.Producers)
                foreach (UnitType t in SpawnerRules.Produces(view.Buildings[i].Type))
                    if (t != UnitType.Builder && s.UnitOf(t) != null) cheapestUnit = System.Math.Min(cheapestUnit, s.CostOf(t));
            if (money >= cheapestUnit) return true;

            float cheapestProducer = float.MaxValue;
            foreach (BuildingType b in FightingProducers)
                if (s.BuildingOf(b) != null) cheapestProducer = System.Math.Min(cheapestProducer, s.CostOf(b) + CheapestUnitOf(s, b));
            if (s.UnitCount[(int)UnitType.Builder] > 0) return money >= cheapestProducer;
            return s.HasHq && money >= cheapestProducer + s.CostOf(UnitType.Builder);
        }

        private static readonly BuildingType[] FightingProducers = { BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.SiegeWorkshop };

        private static float CheapestUnitOf(AiState s, BuildingType producer)
        {
            float cheapest = float.MaxValue;
            foreach (UnitType t in SpawnerRules.Produces(producer)) if (s.UnitOf(t) != null) cheapest = System.Math.Min(cheapest, s.CostOf(t));
            return cheapest == float.MaxValue ? 0f : cheapest;
        }

        /// <summary>True while a teammate is still in the match (it never abandons them).</summary>
        private static bool TeammateAlive(AiContext ai, TeamView view)
        {
            foreach (AiPlayer p in view.Players)
                if (p.OwnerId != ai.Owner && !p.Eliminated && p.Team == view.Team) return true;
            return false;
        }
    }
}
