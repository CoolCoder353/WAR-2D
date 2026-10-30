using Config;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>Rough priorities managers propose at (the budget spends the highest first).</summary>
    public static class AiPriority
    {
        /// <summary>The match is at stake: the HQ, a defence under attack, upkeep about to go unpaid.</summary>
        public const float Critical = 100f;
        public const float High = 75f;
        public const float Normal = 50f;
        public const float Low = 25f;
    }

    /// <summary>What a manager sees and how it proposes, for one think (one per brain, reused).</summary>
    public sealed class AiContext
    {
        internal AiContext(int owner, AiDifficultyConfig difficulty, AiStyleConfig style, AiConfig config, GameConfigData game, Blackboard board, ActionBudget budget)
        {
            Owner = owner;
            Difficulty = difficulty;
            Style = style;
            Config = config;
            Game = game;
            Board = board;
            Budget = budget;
            State = new AiState(this);
        }

        /// <summary>The AI's owner id.</summary>
        public int Owner { get; }

        public AiDifficultyConfig Difficulty { get; }

        /// <summary>The style it plays (Random already resolved).</summary>
        public AiStyleConfig Style { get; }

        /// <summary>Every AI number (<c>GameConfig.xml</c> &lt;Ai&gt;).</summary>
        public AiConfig Config { get; }

        /// <summary>The game's public numbers (unit and building costs, sizes, upkeep): what every client's config holds.</summary>
        public GameConfigData Game { get; }

        /// <summary>Its memory.</summary>
        public Blackboard Board { get; }

        /// <summary>What the managers share within a think: counts, economy, requests, placements (plan Task 7).</summary>
        public AiState State { get; }

        public ActionBudget Budget { get; }

        /// <summary>This think's view: everything it may know.</summary>
        public TeamView View { get; internal set; }

        /// <summary>The AI clock now.</summary>
        public double Now => View != null ? View.Time : 0;

        /// <summary>Proposes an action (see <see cref="ActionBudget.Propose"/>; a non-zero key replaces the same key's waiting proposal).</summary>
        public void Propose(float priority, in AiAction action, int key = 0) => Budget.Propose(priority, action, key, Now);
    }

    /// <summary>
    /// One part of the AI's play (spec §5.3): each think it reads the view and the blackboard and proposes actions with a
    /// priority. It never acts itself; the brain spends the action budget on the proposals.
    /// </summary>
    public interface IAiManager
    {
        /// <summary>Proposes this think's actions through <see cref="AiContext.Propose"/>.</summary>
        void Think(AiContext ai);
    }
}
