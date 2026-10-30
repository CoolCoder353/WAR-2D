using System.Collections.Generic;
using System.Diagnostics;
using WAR2D.AiView;
using WAR2D.Dev.Logging;

namespace WAR2D.Ai
{
    /// <summary>
    /// Schedules the AI players' thinks (v0.14, spec §5.2; server only): in PlacingHQ and Playing, never while paused,
    /// each step at most one due seat thinks, round robin, so thinks are staggered one per tick. A think builds the seat's
    /// view (on the settled world) and runs its brain; the step's main-thread time is <see cref="LastStepMs"/>
    /// (<c>perf.part.ai</c>).
    /// </summary>
    public sealed class AiDirector : IAiDirector
    {
        private readonly Dictionary<AiSeat, double> nextThink = new Dictionary<AiSeat, double>();
        private readonly List<AiSeat> stale = new List<AiSeat>();
        private readonly Stopwatch watch = new Stopwatch();
        private int cursor;

        public double LastStepMs { get; private set; }

        /// <summary>The seat that thought in the last step, or null.</summary>
        public AiSeat LastThinker { get; private set; }

        /// <summary>Thinks so far.</summary>
        public long Thinks { get; private set; }

        public void Step(IAiHost host)
        {
            LastStepMs = 0;
            LastThinker = null;
            GameState state = host.State;
            if (state != GameState.PlacingHQ && state != GameState.Playing) return;
            if (host.Paused) return; // also Task 9's one-human menu pause, through the host
            IReadOnlyList<AiSeat> seats = host.Seats;
            if (seats.Count == 0) return;
            double now = host.Now;
            for (int k = 0; k < seats.Count; k++)
            {
                int i = (cursor + k) % seats.Count;
                AiSeat seat = seats[i];
                IAiBrain brain = seat.Brain;
                if (brain == null) continue;
                if (nextThink.TryGetValue(seat, out double due) && now < due) continue;
                watch.Restart();
                try
                {
                    TeamView view = host.BuildView(seat);
                    if (view != null) brain.Think(view);
                }
                catch (System.Exception e)
                {
                    // One brain's bug must never stop the match's tick.
                    WarLog.Error(LogCategory.Match, $"AI {seat.Callsign} ({seat.OwnerId}) think failed: {e}");
                }
                watch.Stop();
                nextThink[seat] = now + brain.ThinkSeconds;
                cursor = i + 1;
                LastStepMs = watch.Elapsed.TotalMilliseconds;
                LastThinker = seat;
                Thinks++;
                if (nextThink.Count > seats.Count * 2) Prune(seats);
                return;
            }
        }

        /// <summary>Forgets the schedule of seats no longer listed (eliminated, or a new match).</summary>
        private void Prune(IReadOnlyList<AiSeat> seats)
        {
            stale.Clear();
            foreach (AiSeat seat in nextThink.Keys)
            {
                bool listed = false;
                for (int i = 0; i < seats.Count && !listed; i++) listed = seats[i] == seat;
                if (!listed) stale.Add(seat);
            }
            foreach (AiSeat seat in stale) nextThink.Remove(seat);
        }
    }
}
