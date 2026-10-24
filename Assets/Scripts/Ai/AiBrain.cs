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
    public sealed class AiBrain : IAiBrain, IAiBrainStatus
    {
        private readonly IAiManager[] managers;

        /// <param name="managers">The managers in order (null: the standard set).</param>
        public AiBrain(int owner, AiDifficulty difficulty, AiStyle style, AiConfig config, IAiManager[] managers = null, GameConfigData game = null)
        {
            game ??= ConfigLoader.LoadConfig();
            Owner = owner;
            Difficulty = config.GetDifficulty(difficulty);
            Style = config.GetStyle(style == AiStyle.Random ? AiStyle.Balanced : style);
            Budget = new ActionBudget(Difficulty.ActionsPerMinute, config.Brain.BudgetBurstSeconds, config.Brain.ProposalWaitSeconds);
            Board = new Blackboard(owner, Difficulty.ReactionSeconds, config.Brain);
            Context = new AiContext(owner, Difficulty, Style, config, game, Board, Budget);
            this.managers = managers ?? StandardManagers();
            Execute = action => action.Execute(Owner);
            take = action =>
            {
                bool accepted = Execute(action);
                Context.State.OnExecuted(action, accepted);
                return accepted;
            };
        }

        private readonly Func<AiAction, bool> take;

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

        /// <summary>The manager of a type in this brain, or null.</summary>
        public T Manager<T>() where T : class, IAiManager
        {
            foreach (IAiManager m in managers) if (m is T t) return t;
            return null;
        }

        /// <summary>What it is doing, for the control API's <c>aistate</c> (dev only).</summary>
        public System.Collections.Generic.Dictionary<string, object> Status()
        {
            AiState s = Context.State;
            ArmyManager army = Manager<ArmyManager>();
            var units = new System.Collections.Generic.Dictionary<string, int>();
            for (int t = 1; t < s.UnitCount.Length; t++) if (s.UnitCount[t] > 0) units[((UnitType)t).ToString()] = s.UnitCount[t];
            var buildings = new System.Collections.Generic.Dictionary<string, int>();
            for (int t = 1; t < s.BuildingCount.Length; t++) if (s.BuildingCount[t] > 0) buildings[((BuildingType)t).ToString()] = s.BuildingCount[t];
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["thinks"] = Thinks, ["actions"] = Budget.Spent, ["accepted"] = Budget.Accepted, ["waiting"] = Budget.Pending,
                ["openingDone"] = s.OpeningDone, ["playing"] = s.PlayingSeconds, ["incomeEst"] = s.Income, ["upkeepEst"] = s.Upkeep,
                ["units"] = units, ["buildings"] = buildings,
                ["army"] = army != null ? army.Mode.ToString() : "", ["attacks"] = army?.Attacks ?? 0, ["group"] = army?.Group.Count ?? 0,
                ["target"] = army != null ? $"{army.Target.x},{army.Target.y} (owner {army.TargetOwner}, building {army.TargetId})" : "",
                ["gather"] = $"{s.GatherPoint.x},{s.GatherPoint.y}", ["knownEnemyBuildings"] = Board.EnemyBuildings.Count,
                ["knownEnemyBases"] = Board.EnemyBases.Count, ["retreats"] = army?.Retreats ?? 0,
                ["conceded"] = Manager<ConcedeManager>()?.Conceded ?? false,
                ["scout"] = Manager<ScoutingManager>()?.Stops ?? "",
                ["armyLog"] = army != null ? army.Log.ToArray() : new string[0],
                ["blueprints"] = s.Blueprints.Count, ["idleBuilders"] = s.IdleBuilders.Count, ["requests"] = s.BuildRequests.Count,
                ["requestList"] = RequestList(s), ["free"] = s.Free,
                ["expansions"] = $"{Manager<EconomyManager>()?.Held ?? 0}/{Manager<EconomyManager>()?.Claimed.Count ?? 0}",
            };
        }

        private static string RequestList(AiState s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (BuildRequest r in s.BuildRequests) { if (sb.Length > 0) sb.Append(','); sb.Append(r.Type); if (s.IsSaturated(r.Key)) sb.Append("(full)"); }
            foreach (UnitRequest r in s.UnitRequests) { if (sb.Length > 0) sb.Append(','); sb.Append('u').Append(r.Type); }
            return sb.ToString();
        }

        public void Think(TeamView view)
        {
            if (view == null) return;
            Thinks++;
            Context.View = view;
            Budget.Refill(view.Time);
            Board.Observe(view);
            Context.State.Update(view);
            foreach (IAiManager manager in managers) manager.Think(Context);
            Budget.Spend(view.Time, take);
        }
    }
}
