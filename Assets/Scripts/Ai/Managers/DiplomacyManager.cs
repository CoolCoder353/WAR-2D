using System.Collections.Generic;
using Config;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Diplomacy (spec §7, O2, O5, O6), only while the match has Diplomacy on and is playing. It knows only what a player
    /// knows: its own row (<see cref="TeamView.Attacking"/>, <see cref="TeamView.SharingWith"/>), who shares vision with it
    /// (<see cref="TeamView.SharedWithMe"/>), the attacks its side is told about and its alerts. Never another player's row.
    /// <list type="bullet">
    /// <item>Teammates: never attacked, never unshared (it puts either back if it finds it changed).</item>
    /// <item>Peace: a non-teammate who shares vision with it and hasn't hit anything of its for <c>PeaceSeconds</c> is
    /// offered peace (it stops attacking them and shares vision back), accepted by difficulty: Easy always; Normal unless
    /// clearly winning against them; Hard only when losing against them or when both fight a third player.</item>
    /// <item>Betrayal: a hit from someone it doesn't attack makes it attack them again and stop sharing at once, and it
    /// refuses peace from them for <c>GrudgeSeconds</c>.</item>
    /// <item>Gifts (O5, Normal and Hard): above <c>GiftSurplus</c> for <c>GiftSurplusSeconds</c>, it gifts
    /// <c>GiftShare</c> of its balance to the teammate it saw fighting most recently.</item>
    /// <item>O6: a gift from a non-teammate (its alert) halves the peace wait for that player, once.</item>
    /// </list>
    /// </summary>
    public sealed class DiplomacyManager : IAiManager
    {
        /// <summary>What it remembers of one other player.</summary>
        private sealed class Relation
        {
            /// <summary>When they started sharing vision with it (NaN while they don't).</summary>
            public double SharedSince = double.NaN;
            /// <summary>When they last hit anything of its.</summary>
            public double LastHit = double.NegativeInfinity;
            /// <summary>It refuses their peace until then (a betrayal).</summary>
            public double GrudgeUntil = double.NegativeInfinity;
            /// <summary>When it last saw them fight (attack or be attacked), anyone.</summary>
            public double LastFought = double.NegativeInfinity;
            /// <summary>The share of <c>PeaceSeconds</c> they must stay quiet (O6 lowers it once).</summary>
            public float PeaceFactor = 1f;
            public bool GiftCounted;
        }

        private readonly Dictionary<int, Relation> relations = new Dictionary<int, Relation>();
        /// <summary>Fights seen between two other players (key: the pair, smaller id first), with when.</summary>
        private readonly Dictionary<long, double> fights = new Dictionary<long, double>();
        private readonly Dictionary<int, int> ownerOf = new Dictionary<int, int>();
        private readonly HashSet<int> own = new HashSet<int>();
        private readonly List<long> expired = new List<long>();
        private double surplusSince = double.NaN;

        /// <summary>Betrayals so far (a hit from someone it was at peace with).</summary>
        public int Betrayals { get; private set; }

        /// <summary>True while it refuses peace from <paramref name="other"/> after a betrayal.</summary>
        public bool HoldsGrudge(int other, double now) => relations.TryGetValue(other, out Relation r) && now < r.GrudgeUntil;

        /// <summary>Seconds <paramref name="other"/> must stay quiet (and share vision) before it is offered peace.</summary>
        public float PeaceWait(AiContext ai, int other) => ai.Config.Diplomacy.PeaceSeconds * Of(other).PeaceFactor;

        private Relation Of(int other)
        {
            if (!relations.TryGetValue(other, out Relation r)) relations[other] = r = new Relation();
            return r;
        }

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            if (view == null || !view.DiplomacyOn || view.State != GameState.Playing) return;
            double now = ai.Now;
            ObserveAttacks(ai, view);
            ObserveAlerts(ai, view);

            foreach (AiPlayer p in view.Players)
            {
                if (p.OwnerId == ai.Owner || p.Eliminated) continue;
                if (p.Team == view.Team) KeepTeammate(ai, view, p.OwnerId);
                else ConsiderPeace(ai, view, p.OwnerId, now);
            }
            Gift(ai, view, now);
        }

        // ---- what it learns ----

        /// <summary>The owner of a unit or building its side knows of by id (0: unknown).</summary>
        private int OwnerOf(int id) => id != 0 && ownerOf.TryGetValue(id, out int owner) ? owner : 0;

        private void ObserveAttacks(AiContext ai, TeamView view)
        {
            if (view.Attacks.Count == 0) return;
            ownerOf.Clear();
            own.Clear();
            foreach (AiUnit u in view.OwnUnits) { own.Add(u.Id); ownerOf[u.Id] = u.OwnerId; }
            foreach (AiUnit u in view.OtherUnits) ownerOf[u.Id] = u.OwnerId;
            foreach (AiBuilding b in view.Buildings)
            {
                ownerOf[b.Id] = b.OwnerId;
                if (b.OwnerId == ai.Owner && !b.Ghost) own.Add(b.Id);
            }
            foreach (AiAttack attack in view.Attacks)
            {
                int attacker = OwnerOf(attack.AttackerId);
                if (attacker == 0 || attacker == ai.Owner) continue;
                Of(attacker).LastFought = attack.Time;
                if (attack.TargetId == 0) continue;
                if (own.Contains(attack.TargetId)) { Hit(ai, view, attacker, attack.Time); continue; }
                int target = OwnerOf(attack.TargetId);
                if (target == 0 || target == attacker) continue;
                Of(target).LastFought = attack.Time;
                fights[PairKey(attacker, target)] = attack.Time;
            }
            // Forget old fights (the blackboard's army memory).
            expired.Clear();
            foreach (KeyValuePair<long, double> f in fights) if (ai.Now - f.Value > ai.Config.Brain.ArmyMemorySeconds) expired.Add(f.Key);
            foreach (long key in expired) fights.Remove(key);
        }

        private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>O6: a gift from a non-teammate (its GiftReceived alert) halves that player's peace wait, once.</summary>
        private void ObserveAlerts(AiContext ai, TeamView view)
        {
            foreach (AiAlert alert in view.Alerts)
            {
                if (alert.Kind != AlertKind.GiftReceived || alert.OtherOwnerId == 0 || alert.OtherOwnerId == ai.Owner) continue;
                if (view.TeamOf(alert.OtherOwnerId) == view.Team) continue;
                Relation r = Of(alert.OtherOwnerId);
                if (r.GiftCounted) continue;
                r.GiftCounted = true;
                r.PeaceFactor *= ai.Config.Diplomacy.GiftPeaceFactor;
            }
        }

        /// <summary>One of its things was hit by <paramref name="attacker"/>: a betrayal when it was at peace with them.</summary>
        private void Hit(AiContext ai, TeamView view, int attacker, double at)
        {
            Relation r = Of(attacker);
            r.LastHit = at;
            if (view.TeamOf(attacker) == view.Team) return; // friendly fire never breaks a team
            if (view.Attacking.Contains(attacker)) return;   // already fighting them
            if (at >= r.GrudgeUntil) Betrayals++; // more hits before its change applies are the same betrayal
            r.GrudgeUntil = at + ai.Config.Diplomacy.GrudgeSeconds;
            ai.Propose(AiPriority.Critical, AiAction.SetAttack(attacker, true), AiKeys.Of(AiKeys.Attack, attacker));
            if (view.SharingWith.Contains(attacker))
                ai.Propose(AiPriority.Critical, AiAction.SetShareVision(attacker, false), AiKeys.Of(AiKeys.Share, attacker));
        }

        // ---- teammates ----

        private static void KeepTeammate(AiContext ai, TeamView view, int mate)
        {
            if (view.Attacking.Contains(mate)) ai.Propose(AiPriority.High, AiAction.SetAttack(mate, false), AiKeys.Of(AiKeys.Attack, mate));
            if (!view.SharingWith.Contains(mate)) ai.Propose(AiPriority.High, AiAction.SetShareVision(mate, true), AiKeys.Of(AiKeys.Share, mate));
        }

        // ---- peace ----

        private void ConsiderPeace(AiContext ai, TeamView view, int other, double now)
        {
            Relation r = Of(other);
            bool sharesWithMe = view.SharedWithMe.Contains(other);
            if (!sharesWithMe) r.SharedSince = double.NaN;
            else if (double.IsNaN(r.SharedSince)) r.SharedSince = now;

            if (!view.Attacking.Contains(other)) return; // at peace already (it stays until they hit it)
            if (!sharesWithMe || now < r.GrudgeUntil) return;
            double quietSince = System.Math.Max(r.SharedSince, r.LastHit);
            if (now - quietSince < ai.Config.Diplomacy.PeaceSeconds * r.PeaceFactor) return;
            if (!Accepts(ai, view, other, now)) return;
            ai.Propose(AiPriority.Normal, AiAction.SetAttack(other, false), AiKeys.Of(AiKeys.Attack, other));
            if (!view.SharingWith.Contains(other))
                ai.Propose(AiPriority.Normal, AiAction.SetShareVision(other, true), AiKeys.Of(AiKeys.Share, other));
        }

        /// <summary>Whether it takes peace with <paramref name="other"/> now, by its difficulty (spec §7, O2).</summary>
        public bool Accepts(AiContext ai, TeamView view, int other, double now)
        {
            AiDiplomacyConfig d = ai.Config.Diplomacy;
            float mine = StrengthEstimate.Own(ai.Game, view, default, 0f);
            float theirs = StrengthEstimate.Enemy(ai.Game, ai.Board, other, ai.State.Home, 1e6f, ai.Config.Play.TurretStrength);
            // Unknown strength (never seen their army) is neither clearly winning nor losing.
            bool known = theirs > 0f;
            float ratio = known ? mine / theirs : 1f;
            switch (ai.Difficulty.Peace)
            {
                case AiPeace.Always: return true;
                case AiPeace.UnlessWinning: return !(known && ratio > d.WinningRatio);
                case AiPeace.WhenLosingOrThirdParty: return (known && ratio < d.LosingRatio) || BothFightAThird(ai, view, other, now);
                default: return false;
            }
        }

        /// <summary>True when it attacks a third player it has recently seen fighting <paramref name="other"/> too.</summary>
        private bool BothFightAThird(AiContext ai, TeamView view, int other, double now)
        {
            foreach (AiPlayer p in view.Players)
            {
                int third = p.OwnerId;
                if (third == ai.Owner || third == other || p.Eliminated || !view.Attacking.Contains(third)) continue;
                if (fights.TryGetValue(PairKey(other, third), out double at) && now - at <= ai.Config.Brain.ArmyMemorySeconds) return true;
            }
            return false;
        }

        // ---- gifts (O5) ----

        private void Gift(AiContext ai, TeamView view, double now)
        {
            AiDiplomacyConfig d = ai.Config.Diplomacy;
            if (!ai.Difficulty.GiftsSurplus || view.Resources <= d.GiftSurplus) { surplusSince = double.NaN; return; }
            if (double.IsNaN(surplusSince)) surplusSince = now;
            if (now - surplusSince < d.GiftSurplusSeconds) return;
            int mate = GiftTarget(ai, view);
            if (mate == 0) return;
            float amount = view.Resources * d.GiftShare;
            if (amount <= 0f) return;
            ai.Propose(AiPriority.Low, AiAction.Gift(mate, amount), AiKeys.Of(AiKeys.Gift));
            surplusSince = now; // another full surplus spell before the next gift
        }

        /// <summary>The living teammate it saw fighting most recently (else the first living teammate), or 0.</summary>
        public int GiftTarget(AiContext ai, TeamView view)
        {
            int best = 0;
            double bestAt = double.NegativeInfinity;
            foreach (AiPlayer p in view.Players)
            {
                if (p.OwnerId == ai.Owner || p.Eliminated || p.Team != view.Team) continue;
                double fought = relations.TryGetValue(p.OwnerId, out Relation r) ? r.LastFought : double.NegativeInfinity;
                if (best == 0 || fought > bestAt || (fought == bestAt && p.OwnerId < best)) { best = p.OwnerId; bestAt = fought; }
            }
            return best;
        }
    }
}
