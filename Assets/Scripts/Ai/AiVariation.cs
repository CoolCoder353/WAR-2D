using System.Collections.Generic;
using Config;
using Unity.Mathematics;

namespace WAR2D.Ai
{
    /// <summary>
    /// Per-match variation (AI overhaul §6.1, §6.2): a brain's own copy of its styles, with an opening picked from each
    /// style's and its timing, size and army-mix numbers each multiplied by a factor in 1 ± <c>Variation</c>. Every style
    /// gets the same factors, so a style switch (§4.4) keeps the AI's character.
    /// </summary>
    public sealed class AiVariation
    {
        /// <summary>The factors drawn for this brain (1 each when it doesn't vary).</summary>
        public readonly float FirstAttack, ArmySize, Interval, Expand;
        /// <summary>Factors for each unit type's army-mix weight.</summary>
        public readonly float[] Mix;
        /// <summary>Radians its gathering point turns off the line to the enemy.</summary>
        public readonly float GatherAngle;
        /// <summary>A number in [0, 1) that picks each style's opening.</summary>
        private readonly float openingPick;

        public AiVariation(AiDice dice, AiPolicyConfig policy)
        {
            int types = System.Enum.GetValues(typeof(UnitType)).Length + 1;
            Mix = new float[types];
            if (!dice.Varies)
            {
                FirstAttack = ArmySize = Interval = Expand = 1f;
                for (int i = 0; i < types; i++) Mix[i] = 1f;
                return;
            }
            float v = policy.Variation;
            FirstAttack = dice.Range(1f - v, 1f + v);
            ArmySize = dice.Range(1f - v, 1f + v);
            Interval = dice.Range(1f - v, 1f + v);
            Expand = dice.Range(1f - v, 1f + v);
            for (int i = 0; i < types; i++) Mix[i] = dice.Range(1f - v, 1f + v);
            GatherAngle = math.radians(dice.Range(-policy.GatherAngle, policy.GatherAngle));
            openingPick = dice.Next();
            Varies = true;
        }

        /// <summary>True when the factors aren't all 1.</summary>
        public bool Varies { get; }

        /// <summary>A copy of <paramref name="style"/> with its opening picked and its numbers varied.</summary>
        public AiStyleConfig Apply(AiStyleConfig style)
        {
            AiStyleConfig s = style.Clone();
            if (s.BuildOrders.Length > 0)
                s.BuildOrder = s.BuildOrders[Varies ? math.min(s.BuildOrders.Length - 1, (int)(openingPick * s.BuildOrders.Length)) : 0];
            if (!Varies) return s;
            s.FirstAttackSeconds *= FirstAttack;
            s.AttackArmySize = math.max(1, (int)math.round(s.AttackArmySize * ArmySize));
            s.AttackIntervalSeconds *= Interval;
            s.ExpandTiming *= Expand;
            var mix = new Dictionary<UnitType, float>(s.ArmyMix.Count);
            foreach (KeyValuePair<UnitType, float> w in s.ArmyMix) mix[w.Key] = w.Value * Mix[(int)w.Key];
            s.ArmyMix = mix;
            return s;
        }
    }
}
