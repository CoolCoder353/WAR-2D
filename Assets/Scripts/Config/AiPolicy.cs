using System;

namespace Config
{
    /// <summary>
    /// Resolves a difficulty (AI overhaul §2.2): the one policy played through a difficulty's handicaps. Every resolved
    /// number moves only one way with its handicap, so a better handicap can never make an AI play worse by construction;
    /// full handicaps (Insight, Attention and Ambition 1, Mistakes 0) give the policy itself.
    /// </summary>
    public static class AiPolicy
    {
        /// <summary>Insight below which an AI scouts once; from <see cref="RadarInsight"/> it also keeps a Radar.</summary>
        public const float RegularScoutingInsight = 0.3f, RadarInsight = 0.9f;

        /// <summary>The settings the managers play by for one difficulty.</summary>
        public static AiDifficultyConfig Resolve(AiPolicyConfig p, AiHandicapConfig h)
        {
            float insight = Clamp01(h.Insight), attention = Clamp01(h.Attention), ambition = Clamp01(h.Ambition);
            return new AiDifficultyConfig
            {
                ThinkSeconds = h.ThinkSeconds,
                ActionsPerMinute = h.ActionsPerMinute,
                ReactionSeconds = h.ReactionSeconds,
                Insight = insight,
                Attention = attention,
                Ambition = ambition,
                Mistakes = Clamp01(h.Mistakes),

                Builders = Math.Max(Math.Min(2, p.Builders), (int)Math.Round(p.Builders * ambition)),
                BuildersPerExpansion = (int)Math.Round(p.BuildersPerExpansion * ambition),
                MaxExpansions = Math.Max(Math.Min(1, p.MaxExpansions), (int)Math.Round(p.MaxExpansions * ambition)),
                ExpandAfterSeconds = p.ExpandAfterSeconds / Math.Max(0.25f, ambition),
                SaturatesMiners = ambition >= p.SaturateAmbition,
                ExtraProducers = (int)Math.Round(p.ExtraProducers * ambition),
                IncomePerProducer = p.IncomePerProducer,
                KeepsUpkeepPaid = true,
                UpkeepReserveSeconds = p.UpkeepReserveSeconds,

                CounterWeight = insight,
                BuildingCounterWeight = Math.Max(0f, 2f * insight - 1f),
                ArmyMemoryScale = Math.Max(0.3f, insight),
                Scouts = Math.Max(1, (int)Math.Round(p.Scouts * insight)),
                RescoutSeconds = p.RescoutSeconds / Math.Max(0.2f, insight),
                Scouting = insight < RegularScoutingInsight ? AiScouting.Once : insight >= RadarInsight ? AiScouting.RegularWithRadar : AiScouting.Regular,

                SpecialUse = attention,
                Retreats = p.Retreats,
                FocusFire = p.FocusFire,
                KeepsBacklineBack = p.KeepsBacklineBack,
                DigsPastWalls = p.DigsPastWalls,
                AttackAdvantage = p.AttackAdvantage,
                RetreatAdvantage = p.RetreatAdvantage,
                DefendsChokepoints = p.DefendsChokepoints,
                EarlyTurrets = p.EarlyTurrets,

                Peace = h.Peace,
                GiftsSurplus = h.GiftsSurplus,
            };
        }

        /// <summary>Fills <see cref="AiConfig.Difficulties"/> from the policy and every difficulty's handicaps.</summary>
        public static void ResolveAll(AiConfig config)
        {
            config.Difficulties.Clear();
            foreach (var h in config.Handicaps) config.Difficulties[h.Key] = Resolve(config.Policy, h.Value);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
