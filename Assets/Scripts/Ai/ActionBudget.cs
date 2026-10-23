using System;
using System.Collections.Generic;

namespace WAR2D.Ai
{
    /// <summary>
    /// The AI's action budget (v0.14, spec §5.1): tokens refilled at its difficulty's actions per minute, saved up to
    /// <c>BudgetBurstSeconds</c> of them. Managers <see cref="Propose"/> (priority, action); each think the brain
    /// <see cref="Spend"/>s the tokens on the highest priority first (older first on a tie), one token per action whether
    /// or not it is accepted (a refused click is still a click). Proposals it can't afford wait for a later think, up to
    /// <c>ProposalWaitSeconds</c>; a manager re-proposing under the same key replaces its waiting proposal.
    /// </summary>
    public sealed class ActionBudget
    {
        private struct Proposal
        {
            public float Priority;
            public AiAction Action;
            public int Key;
            public double Since;
            public long Order;
        }

        private readonly List<Proposal> pending = new List<Proposal>(32);
        private readonly float perSecond, capacity, waitSeconds;
        private double lastRefill = double.NaN;
        private long sequence;
        private static readonly Comparison<Proposal> HighestFirst = (a, b) =>
            a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Order.CompareTo(b.Order);

        /// <param name="actionsPerMinute">The difficulty's APM.</param>
        /// <param name="burstSeconds">How many seconds of tokens it may save up (it starts full).</param>
        /// <param name="waitSeconds">How long an unaffordable proposal waits.</param>
        public ActionBudget(int actionsPerMinute, float burstSeconds, float waitSeconds)
        {
            ActionsPerMinute = Math.Max(0, actionsPerMinute);
            perSecond = ActionsPerMinute / 60f;
            capacity = Math.Max(1f, perSecond * Math.Max(0f, burstSeconds));
            this.waitSeconds = Math.Max(0f, waitSeconds);
            Tokens = capacity;
        }

        public int ActionsPerMinute { get; }

        /// <summary>Actions it may take now.</summary>
        public float Tokens { get; private set; }

        /// <summary>Most tokens it saves up.</summary>
        public float Capacity => capacity;

        /// <summary>Proposals waiting.</summary>
        public int Pending => pending.Count;

        /// <summary>Actions taken so far, and how many were accepted.</summary>
        public long Spent { get; private set; }
        public long Accepted { get; private set; }

        /// <summary>Adds the tokens earned since the last refill (the first refill only starts the clock).</summary>
        public void Refill(double now)
        {
            if (!double.IsNaN(lastRefill) && now > lastRefill) Tokens = Math.Min(capacity, Tokens + (float)((now - lastRefill) * perSecond));
            lastRefill = double.IsNaN(lastRefill) ? now : Math.Max(lastRefill, now);
        }

        /// <summary>
        /// Proposes an action. A non-zero <paramref name="key"/> names what it is about (a producer, a squad, a plan):
        /// a waiting proposal with the same key is replaced, so a manager can re-propose every think without piling up.
        /// </summary>
        public void Propose(float priority, in AiAction action, int key, double now)
        {
            var proposal = new Proposal { Priority = priority, Action = action, Key = key, Since = now, Order = sequence++ };
            if (key != 0)
                for (int i = 0; i < pending.Count; i++)
                    if (pending[i].Key == key)
                    {
                        proposal.Order = pending[i].Order; // keeps its place in the queue
                        proposal.Since = pending[i].Since;
                        pending[i] = proposal;
                        return;
                    }
            pending.Add(proposal);
        }

        /// <summary>
        /// Takes as many proposals as the tokens allow, highest priority first, through <paramref name="execute"/>; the
        /// rest wait (stale ones are dropped). Returns how many it took.
        /// </summary>
        public int Spend(double now, Func<AiAction, bool> execute)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
                if (now - pending[i].Since > waitSeconds) pending.RemoveAt(i);
            if (pending.Count == 0) return 0;
            pending.Sort(HighestFirst);
            int taken = 0;
            while (taken < pending.Count && Tokens >= 1f)
            {
                Tokens -= 1f;
                Spent++;
                if (execute(pending[taken].Action)) Accepted++;
                taken++;
            }
            pending.RemoveRange(0, taken);
            return taken;
        }

        /// <summary>Drops every waiting proposal.</summary>
        public void Clear() => pending.Clear();
    }
}
