using System;
using Config;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// One AI player's brain (v0.14): each think it learns its <see cref="TeamView"/> into the <see cref="Blackboard"/>,
    /// asks every manager for proposals, and spends its <see cref="ActionBudget"/> on them, highest priority first.
    /// It acts only through <see cref="AiAction.Execute"/> (that is, <see cref="PlayerActions"/>).
    /// </summary>
    public sealed class AiBrain : IAiBrain
    {
        private readonly IAiManager[] managers;

        /// <param name="managers">The managers in order (null: the standard set).</param>
        public AiBrain(int owner, AiDifficulty difficulty, AiStyle style, AiConfig config, IAiManager[] managers = null)
        {
            Owner = owner;
            Difficulty = config.GetDifficulty(difficulty);
            Style = config.GetStyle(style == AiStyle.Random ? AiStyle.Balanced : style);
            Budget = new ActionBudget(Difficulty.ActionsPerMinute, config.Brain.BudgetBurstSeconds, config.Brain.ProposalWaitSeconds);
            Board = new Blackboard(owner, Difficulty.ReactionSeconds, config.Brain);
            Context = new AiContext(owner, Difficulty, Style, config, Board, Budget);
            this.managers = managers ?? StandardManagers();
            Execute = action => action.Execute(Owner);
        }

        /// <summary>A brain for a seat at match start (its style resolved), from the loaded config.</summary>
        public static AiBrain For(AiSeat seat) => new AiBrain(seat.OwnerId, seat.Difficulty, seat.ResolvedStyle, ConfigLoader.LoadConfig().Ai);

        /// <summary>The managers every brain runs, in the order they propose (spec §5.3).</summary>
        public static IAiManager[] StandardManagers() => new IAiManager[]
        {
            new OpeningManager(), new EconomyManager(), new BuilderManager(), new ProductionManager(),
            new ScoutingManager(), new ArmyManager(), new DiplomacyManager(), new ConcedeManager(),
        };

        public int Owner { get; }
        public AiDifficultyConfig Difficulty { get; }
        public AiStyleConfig Style { get; }
        public ActionBudget Budget { get; }
        public Blackboard Board { get; }
        public AiContext Context { get; }

        /// <summary>Takes an action (by default through <see cref="PlayerActions"/>; tests count them instead).</summary>
        public Func<AiAction, bool> Execute;

        /// <summary>Thinks so far.</summary>
        public int Thinks { get; private set; }

        public float ThinkSeconds => Difficulty.ThinkSeconds;

        public void Think(TeamView view)
        {
            if (view == null) return;
            Thinks++;
            Context.View = view;
            Budget.Refill(view.Time);
            Board.Observe(view);
            foreach (IAiManager manager in managers) manager.Think(Context);
            Budget.Spend(view.Time, Execute);
        }
    }
}
