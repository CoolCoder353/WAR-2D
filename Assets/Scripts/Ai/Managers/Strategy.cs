using System.Collections.Generic;
using Config;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Strategy (AI overhaul §4.3, §4.4): reacts to what its enemy models show and changes style when its plan isn't
    /// working. A rush expected (an enemy guessed Rusher while its own army is under its attack size): an extra turret in
    /// front of its HQ, its first attack held until the rush is seen off (or <c>FirstAttackSeconds</c> + 120 s), and its
    /// mix towards Flamethrowers and Tanks. A greedy enemy (guessed Expander): its first attack comes sooner
    /// (<c>PunishFactor</c>). A Turtle enemy: its mix towards Artillery and Diggers, and it raids more. At most once per
    /// <c>StyleSwitchSeconds</c>: a maxed-out or late Turtle with the advantage becomes Balanced, a Rusher after two bad
    /// trades in a row an Expander, an Expander expecting a rush before its first attack Balanced.
    /// </summary>
    public sealed class StrategyManager : IAiManager
    {
        private double lastSwitch = double.NegativeInfinity;
        private bool rushSeenOff;

        /// <summary>Its style switches, oldest first (dev reads).</summary>
        public readonly List<string> Switches = new List<string>(4);

        /// <summary>What it reacts to now (dev reads).</summary>
        public string Reacting { get; private set; } = "";

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPolicyConfig policy = ai.Policy;

            bool rusher = false, greedy = false, turtle = false;
            foreach (EnemyModel m in ai.Board.Models.Values)
            {
                if (!view.IsEnemy(m.Owner)) continue;
                rusher |= m.Guess == EnemyStyleGuess.Rusher;
                greedy |= m.Guess == EnemyStyleGuess.Expander;
                turtle |= m.Guess == EnemyStyleGuess.Turtle;
            }
            int army = 0;
            foreach (int i in state.Combat) if (!state.Scouts.Contains(view.OwnUnits[i].Id)) army++;

            // A rush is seen off once a threat at its base has come and gone.
            if (rusher && !rushSeenOff && ai.Board.LastBaseThreat > double.NegativeInfinity && ai.Now - ai.Board.LastBaseThreat > policy.RushOverSeconds) rushSeenOff = true;
            state.RushExpected = rusher && army < ai.Style.AttackArmySize && !rushSeenOff;
            state.ExtraTurrets = state.RushExpected ? 1 : 0;
            state.HoldFirstAttack = rusher && !rushSeenOff && !state.AttackedOnce && state.PlayingSeconds < ai.Style.FirstAttackSeconds + 120.0;
            state.FirstAttackFactor = greedy ? policy.PunishFactor : 1f;
            state.RaidCooldownFactor = turtle ? policy.TurtleRaidFactor : 1f;

            for (int t = 0; t < state.MixBias.Length; t++) state.MixBias[t] = 1f;
            if (state.RushExpected) { Bias(state, UnitType.Flamethrower, policy.ReactionBias); Bias(state, UnitType.Tank, policy.ReactionBias); }
            if (turtle) { Bias(state, UnitType.Artillery, policy.ReactionBias); Bias(state, UnitType.Digger, policy.ReactionBias * policy.ReactionBias); }
            Reacting = (state.RushExpected ? "rush " : "") + (greedy ? "greedy " : "") + (turtle ? "turtle" : "");

            SwitchStyle(ai, state, army);
        }

        private static void Bias(AiState state, UnitType type, float factor) => state.MixBias[(int)type] *= factor;

        private void SwitchStyle(AiContext ai, AiState state, int army)
        {
            AiPolicyConfig policy = ai.Policy;
            if (ai.Now - lastSwitch < policy.StyleSwitchSeconds || ai.SwitchStyle == null) return;
            AiStyle to = ai.StyleName;
            string why = null;
            switch (ai.StyleName)
            {
                case AiStyle.Turtle:
                    // Maxed out only counts once its own first attack is due (its starting money banks early).
                    bool maxed = state.PlayingSeconds > ai.Style.FirstAttackSeconds && state.Banked && !state.MayAddUpkeep(state.UpkeepOf(UnitType.Tank));
                    if (maxed) { to = AiStyle.Balanced; why = "maxed out"; }
                    else if (state.PlayingSeconds > policy.TurtleBreakoutSeconds && Ahead(ai)) { to = AiStyle.Balanced; why = "ahead late"; }
                    break;
                case AiStyle.Rusher:
                    if (state.BadTradeStreak >= 2) { to = AiStyle.Expander; why = "two bad trades"; }
                    break;
                case AiStyle.Expander:
                    if (state.RushExpected && !state.AttackedOnce) { to = AiStyle.Balanced; why = "a rush coming"; }
                    break;
            }
            if (to == ai.StyleName) return;
            AiStyle from = ai.StyleName;
            ai.SwitchStyle(to);
            lastSwitch = ai.Now;
            if (Switches.Count >= 4) Switches.RemoveAt(0);
            Switches.Add($"{state.PlayingSeconds / 60.0:F1}m {from} to {to}: {why}");
        }

        /// <summary>True when its whole force beats every enemy army it remembers.</summary>
        private static bool Ahead(AiContext ai)
        {
            Force ours = AiForces.Own(ai);
            bool any = false;
            foreach (EnemyModel m in ai.Board.Models.Values)
            {
                if (!ai.View.IsEnemy(m.Owner)) continue;
                any = true;
                if (AiForces.Advantage(ai, ours, AiForces.EnemyArmy(ai, m.Owner)) < 1f) return false;
            }
            return any;
        }
    }
}
