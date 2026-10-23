using System;
using System.Collections.Generic;
using WAR2D.Sim;

namespace WAR2D.AiView
{
    /// <summary>What the AI director may see of the match and ask of it (v0.14). <see cref="AiRunner"/> in a match; tests fake it.</summary>
    public interface IAiHost
    {
        /// <summary>The match state; the director thinks only in PlacingHQ and Playing.</summary>
        GameState State { get; }

        /// <summary>True while the match is paused (dev pause now, the one-human menu pause later): the director does nothing.</summary>
        bool Paused { get; }

        /// <summary>The AI clock: seconds of unpaused PlacingHQ and Playing steps.</summary>
        double Now { get; }

        /// <summary>The seats that think: a brain, still in the match. Ordered by owner id.</summary>
        IReadOnlyList<AiSeat> Seats { get; }

        /// <summary>Builds the seat's team view on the settled world now (call only for the seat that thinks this step).</summary>
        TeamView BuildView(AiSeat seat);
    }

    /// <summary>Schedules the AI players' thinks (implemented in <c>WAR2D.Ai</c>, registered through <see cref="AiBrains.Director"/>).</summary>
    public interface IAiDirector
    {
        /// <summary>One step (a simulation tick, or a tick's worth of time while HQs are placed): at most one due seat thinks.</summary>
        void Step(IAiHost host);

        /// <summary>Main-thread milliseconds the last step spent (its view build and think; 0 when nobody thought).</summary>
        double LastStepMs { get; }
    }

    /// <summary>
    /// Drives the AI director in a match (server only): one step per settled tick in Playing (from
    /// <see cref="SimContext.Settled"/>, so views are built on the settled world), and one per tick's worth of time in
    /// PlacingHQ, when the tick doesn't run (no jobs are in flight, so reading the simulation is safe). Records the AI's
    /// main-thread time per step in <see cref="SimTiming.LastAiMs"/> (<c>perf.part.ai</c>).
    /// </summary>
    public sealed class AiRunner : IAiHost, IDisposable
    {
        /// <summary>
        /// Extra pause sources besides the dev pause (<see cref="SimPause.Paused"/>): plan Task 9's one-human menu pause
        /// (<c>MatchPause</c>) sets this. Null: none.
        /// </summary>
        public static Func<bool> MatchPaused;

        private readonly SimContext sim;
        private readonly TeamViewBuilder views;
        private readonly IAiDirector director;
        private readonly List<AiSeat> seats = new List<AiSeat>(8);
        private static readonly Comparison<AiSeat> ByOwner = (a, b) => a.OwnerId.CompareTo(b.OwnerId);
        private readonly float tickSeconds;
        private float placingAccumulator;
        private SimData stepData;
        private int stepTick, stepCount;
        private bool disposed;

        public AiRunner(SimContext sim, TeamViewBuilder views, IAiDirector director)
        {
            this.sim = sim;
            this.views = views;
            this.director = director;
            tickSeconds = sim.Config.Simulation.TickSeconds;
            sim.Settled += OnSettled;
        }

        public GameState State => GameCore.Instance != null ? GameCore.Instance.CurrentState : GameState.Lobby;

        public bool Paused => sim.Pause.Paused || (MatchPaused != null && MatchPaused());

        public double Now { get; private set; }

        public IReadOnlyList<AiSeat> Seats => seats;

        public TeamView BuildView(AiSeat seat) => seat == null ? null : views.Build(seat.OwnerId, stepData, stepTick, stepCount);

        private void OnSettled(SimData data, int tick, int count)
        {
            if (State != GameState.Playing) return;
            Step(data, tick, count);
        }

        /// <summary>From the match's Update (server): steps the director at the tick rate while HQs are placed.</summary>
        public void Update(float deltaTime)
        {
            if (disposed || State != GameState.PlacingHQ) { placingAccumulator = 0f; return; }
            placingAccumulator = Math.Min(placingAccumulator + deltaTime, tickSeconds * 2f); // a stall never runs a burst of thinks
            if (placingAccumulator < tickSeconds) return;
            placingAccumulator -= tickSeconds;
            Step(sim.Data, sim.Clock.Tick, sim.Clock.UnitCount);
        }

        private void Step(SimData data, int tick, int count)
        {
            if (disposed) return;
            SimTiming.LastAiMs = 0;
            if (director == null) return;
            GameState state = State;
            if (!Paused && (state == GameState.PlacingHQ || state == GameState.Playing)) Now += tickSeconds;
            RefreshSeats();
            stepData = data;
            stepTick = tick;
            stepCount = count;
            director.Step(this);
            SimTiming.LastAiMs = director.LastStepMs;
        }

        /// <summary>The seats with a brain that are still playing, tracked by the view builder (an eliminated one is dropped).</summary>
        private void RefreshSeats()
        {
            seats.Clear();
            GameCore core = GameCore.Instance;
            if (core == null) return;
            foreach (KeyValuePair<int, AiSeat> entry in core.AiPlayers)
            {
                AiSeat seat = entry.Value;
                if (seat.Brain == null) continue;
                if (seat.Player.state != PlayerState.Playing) { if (views.IsTracked(seat.OwnerId)) views.Untrack(seat.OwnerId); continue; }
                if (!views.IsTracked(seat.OwnerId)) views.Track(seat.OwnerId);
                seats.Add(seat);
            }
            seats.Sort(ByOwner);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            sim.Settled -= OnSettled;
        }
    }
}
