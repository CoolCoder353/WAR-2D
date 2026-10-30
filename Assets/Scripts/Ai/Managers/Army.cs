using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>What the army is doing.</summary>
    public enum ArmyMode : byte
    {
        /// <summary>At its gathering point, building up.</summary>
        Gather,
        /// <summary>Home units fight a threat near its base.</summary>
        Defend,
        /// <summary>The attack group goes for a known enemy building or last-seen army.</summary>
        Attack,
        /// <summary>The attack group pulls back to the gathering point.</summary>
        Retreat,
    }

    /// <summary>
    /// Army (spec §5.3, plan Task 7, AI overhaul §3 and §4.1). Its fighting units gather at a point in front of its HQ
    /// towards the enemy it knows, turned by its variation's angle (Turtle: at its chokepoint, holding it). Threats near
    /// its base (enemies seen by its buildings, or Under-attack alerts, both after the reaction delay) are met by its home
    /// units, and the attack group is called back when home can't hold. Every fight is judged by the fight model
    /// (<see cref="FightModel"/>, from the damage table, with its insight's noise). It attacks when the play time, wave
    /// interval and army size allow and its advantage over the target's enemy (that enemy's remembered army plus the
    /// turrets it knows at the target) reaches its threshold (the policy's, scaled by the style and its caution). The
    /// target is scored (<see cref="ChooseTarget"/>): worth over distance, among the targets whose known defence it beats,
    /// never anything it hasn't seen; a turret-guarded target with turrets it out-ranges is sieged. After a threat at its
    /// base an attack leaves a home guard. On its attentive thinks: it retreats from a losing fight (or regroups a strung
    /// out vanguard), its units in range focus fire, Artillery and Snipers stay behind the front, Diggers dig through enemy
    /// walls, and Suicide bombers detonate on clumps and walls. Each attack's trade (from the kill ledger) moves its
    /// caution and marks a target that beat it. Scouts and raiders are left alone.
    /// </summary>
    public sealed class ArmyManager : IAiManager
    {
        private readonly HashSet<int> group = new HashSet<int>();
        private readonly HashSet<int> alive = new HashSet<int>();
        private readonly HashSet<int> holding = new HashSet<int>();
        private readonly List<int> ids = new List<int>(256), back = new List<int>(32), stale = new List<int>(32);
        private readonly List<AiAlert> alerts = new List<AiAlert>(8);
        private readonly Dictionary<int, double> orderedAt = new Dictionary<int, double>();
        private double lastAttackEnd = double.NegativeInfinity, retreatStarted, nextGather = double.NegativeInfinity, stagedAt;
        private bool attackedOnce, engaged, hasThreat;
        private int2 stage;
        private double marchStarted, lastMarchNote, targetSince;
        private float marchLimit;
        private float homeAdvantage;
        private float2 threatAt;

        /// <summary>What the army did (dev reads: the bench and <c>aistate</c>), newest last, at most 40 lines.</summary>
        public readonly List<string> Log = new List<string>(40);

        private void Note(AiContext ai, string what)
        {
            if (Log.Count >= 40) Log.RemoveAt(0);
            Log.Add($"{ai.State.PlayingSeconds / 60.0:F1}m {what}");
        }

        /// <summary>True once the attack group has gathered at its staging point and goes for the target.</summary>
        public bool Engaged => engaged;

        public ArmyMode Mode { get; private set; }

        /// <summary>What it attacks: a known enemy building's id (0 for a last-seen army), its owner and tile.</summary>
        public int TargetId { get; private set; }
        public int TargetOwner { get; private set; }
        public int2 Target { get; private set; }

        /// <summary>Its attack group's unit ids.</summary>
        public IReadOnlyCollection<int> Group => group;

        /// <summary>Attacks it has started, and retreats it has made.</summary>
        public int Attacks { get; private set; }
        public int Retreats { get; private set; }

        // ---- AI overhaul §4.1: learning from its attacks ----
        /// <summary>Factor on the advantage it wants before attacking: up after bad trades, down after good ones.</summary>
        public float Caution { get; private set; } = 1f;
        /// <summary>Bad trades in a row (the Strategy manager's style switch reads it).</summary>
        public int BadTradeStreak { get; private set; }
        /// <summary>The last attack's trade (enemy worth lost over its own), or NaN before one ended.</summary>
        public float LastTrade { get; private set; } = float.NaN;
        /// <summary>Each finished attack's trade, newest last (dev reads), at most 20.</summary>
        public readonly List<string> Trades = new List<string>(20);
        /// <summary>True while the group sieges turrets it out-ranges (short-range units held back).</summary>
        public bool Sieging { get; private set; }
        private bool recording;
        /// <summary>The target its group last regrouped for: one regroup per target, then it fights (a strung-out march would regroup for ever).</summary>
        private int2 regroupedFor = new int2(int.MinValue, int.MinValue);
        private float recordEnemyLost, recordOwnLost, recordValue;
        private bool recordTurrets;
        private int2 recordTarget;

        private static bool Busy(AiState state, int id) => state.Scouts.Contains(id) || state.Raiders.Contains(id);

        public void Think(AiContext ai)
        {
            TeamView view = ai.View;
            AiState state = ai.State;
            if (view.State != GameState.Playing || !state.HasHq) return;
            AiPlayConfig play = ai.Config.Play;
            if (ai.Now >= nextGather || !state.HasGatherPoint) { PlaceGatherPoint(ai, view, state); nextGather = ai.Now + play.ReplanSeconds; }

            alive.Clear();
            foreach (int i in state.Combat) alive.Add(view.OwnUnits[i].Id);
            stale.Clear();
            foreach (int id in group) if (!alive.Contains(id) || Busy(state, id)) stale.Add(id);
            foreach (int id in stale) group.Remove(id);
            stale.Clear();
            foreach (int id in holding) if (!alive.Contains(id) || group.Contains(id)) stale.Add(id);
            foreach (int id in stale) holding.Remove(id);

            Defend(ai, view, state);
            // Defending against a threat it far outweighs: the rest of the army may counter-attack (the units at the threat stay).
            if (Mode == ArmyMode.Defend && hasThreat && homeAdvantage >= ai.Policy.CounterAttackAdvantage)
                TryStartAttack(ai, view, state, CountArmy(view, state), true);
            switch (Mode)
            {
                case ArmyMode.Attack: Fight(ai, view, state); break;
                case ArmyMode.Retreat: Retreat(ai, view, state); break;
                case ArmyMode.Gather: Gather(ai, view, state); break;
            }
            state.Attacking = Mode == ArmyMode.Attack;
            state.AttackedOnce = attackedOnce;
            state.BadTradeStreak = BadTradeStreak;
            if (orderedAt.Count > 64) orderedAt.Clear();
        }

        // ---- gathering ----

        private void PlaceGatherPoint(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            float2 enemy = BuilderManager.EnemyDirectionPoint(ai, view, state);
            float2 dir = math.normalizesafe(enemy - state.Home, new float2(1f, 0f));
            // Its variation turns the gathering point off the straight line to the enemy (AI overhaul §6.1).
            float angle = ai.Variation.GatherAngle;
            if (angle != 0f) dir = new float2(dir.x * math.cos(angle) - dir.y * math.sin(angle), dir.x * math.sin(angle) + dir.y * math.cos(angle));
            float2 point = ai.Style.HoldsChokes && state.DefenceSpots.Count > 0
                ? state.DefenceSpots[0] - state.DefenceDirs[0] * play.DefenceSpread * 0.5f
                : state.Home + dir * play.GatherDistance;
            state.GatherPoint = state.GroundNear(point, (int)math.max(play.DefenceSpread, play.GatherDistance));
            state.HasGatherPoint = true;
        }

        private void Gather(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            int army = CountArmy(view, state);
            if (TryStartAttack(ai, view, state, army, false)) return;
            if (Sweep(ai, view, state, army)) return;

            // Units away from the gathering point with nothing to do go there; Turtle holds it.
            float2 g = (float2)state.GatherPoint + 0.5f;
            float r2 = play.ArmyRadius * play.ArmyRadius;
            ids.Clear();
            back.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (Busy(state, u.Id) || u.HasOrder || u.Stance == 3) continue;
                if (math.distancesq(u.Position, g) > r2) ids.Add(u.Id);
                else if (ai.Style.HoldsChokes && !holding.Contains(u.Id)) back.Add(u.Id);
            }
            if (ids.Count > 0 && Due(ai, AiKeys.Army)) ai.Propose(AiPriority.Low, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Army));
            if (back.Count > 0)
            {
                ai.Propose(AiPriority.Low, AiAction.OrderUnits(back, OrderKind.Hold, state.GatherPoint), AiKeys.Of(AiKeys.Hold));
                foreach (int id in back) holding.Add(id);
            }
        }

        private readonly HashSet<long> swept = new HashSet<long>();
        private int2 sweepSite;
        private bool sweeping;

        /// <summary>
        /// Ready to attack but with nothing it knows to attack (no enemy building, remembered HQ or army): the army itself
        /// goes to look, moving (not attacking) to the nearest start site of the map (public map data) it doesn't see now
        /// and hasn't swept, so a base its scouts missed is found. Once anything is known, the normal attack takes over.
        /// </summary>
        private bool Sweep(AiContext ai, TeamView view, AiState state, int army)
        {
            if (state.PlayingSeconds < FirstAttackAt(ai, state) || army < ai.Style.AttackArmySize) { sweeping = false; return false; }
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values) if (b.Type != BuildingType.Bomb) { sweeping = false; return false; }
            if (ai.Board.KnownHqs.Count > 0 || ai.Board.Armies.Count > 0) { sweeping = false; return false; }
            float2 centre = GroupCentreOf(view, state);
            if (sweeping && (view.FogAt((float2)sweepSite + 0.5f) == 2 || math.distancesq(centre, (float2)sweepSite) < ai.Config.Play.ArmyRadius * ai.Config.Play.ArmyRadius))
            {
                swept.Add(((long)sweepSite.x << 32) | (uint)sweepSite.y);
                sweeping = false;
            }
            if (!sweeping)
            {
                float best = float.MaxValue;
                foreach (int2 site in view.HqSites)
                {
                    if (math.distance(site, state.Home) < view.HqClearRadius * 2f || swept.Contains(((long)site.x << 32) | (uint)site.y)) continue;
                    if (view.FogAt((float2)site + 0.5f) == 2) continue;
                    float d = math.distancesq((float2)site, centre);
                    if (d < best) { best = d; sweepSite = site; sweeping = true; }
                }
                if (!sweeping) { swept.Clear(); return false; } // all swept: start over next time
                Note(ai, $"sweep to {sweepSite.x},{sweepSite.y} with {army}");
            }
            ids.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (Busy(state, u.Id) || (u.HasOrder && math.distancesq(u.OrderGoal, sweepSite) < 64f)) continue;
                ids.Add(u.Id);
            }
            if (ids.Count > 0 && Due(ai, AiKeys.Army)) ai.Propose(AiPriority.Low, AiAction.OrderUnits(ids, OrderKind.Move, state.GroundNear((float2)sweepSite + 0.5f, view.HqClearRadius)), AiKeys.Of(AiKeys.Army));
            return true;
        }

        private static float2 GroupCentreOf(TeamView view, AiState state)
        {
            float2 sum = 0;
            int n = 0;
            foreach (int i in state.Combat) { sum += view.OwnUnits[i].Position; n++; }
            return n > 0 ? sum / n : state.Home;
        }

        private int CountArmy(TeamView view, AiState state)
        {
            int n = 0;
            foreach (int i in state.Combat) if (!Busy(state, view.OwnUnits[i].Id)) n++;
            return n;
        }

        // ---- attacking ----

        /// <summary>Play seconds of its first attack: the style's, sooner against a greedy enemy (AI overhaul §4.3).</summary>
        private static float FirstAttackAt(AiContext ai, AiState state) => ai.Style.FirstAttackSeconds * state.FirstAttackFactor;

        /// <summary>The advantage it wants before attacking: the policy's, scaled by the style and its caution.</summary>
        private float Need(AiContext ai) => ai.Difficulty.AttackAdvantage * ai.Style.AttackRatioScale * Caution;

        /// <summary>Maxed out (no upkeep room for another unit, money piling up): a player attacks with what they have.</summary>
        private static bool Maxed(AiContext ai, AiState state) =>
            ai.Difficulty.KeepsUpkeepPaid && state.Free >= ai.Config.Play.MaxedAttackResources && !state.MayAddUpkeep(state.UpkeepOf(UnitType.Tank));

        /// <param name="counter">A counter-attack from Defend: the units at the threat stay to hold it.</param>
        private bool TryStartAttack(AiContext ai, TeamView view, AiState state, int army, bool counter)
        {
            AiPlayConfig play = ai.Config.Play;
            if (state.PlayingSeconds < FirstAttackAt(ai, state) || army < ai.Style.AttackArmySize) return false;
            if (attackedOnce && ai.Now - lastAttackEnd < ai.Style.AttackIntervalSeconds) return false;
            bool maxed = Maxed(ai, state);
            // A rush expected and not yet seen off: its first attack waits (AI overhaul §4.3).
            if (!attackedOnce && state.HoldFirstAttack && !maxed && !counter) return false;
            // An attack-timing slip (AI overhaul §2.3): it goes without the advantage it wants.
            bool slip = !maxed && ai.Slip(AiSlip.AttackTiming);
            float need = maxed || slip ? 0f : Need(ai);
            if (!ChooseTarget(ai, state.Home, -1, need, out int id, out int owner, out int2 target)) return false;
            Force ours = AiForces.Own(ai);
            Force theirs = AiForces.EnemyArmy(ai, owner);
            theirs.Add(AiForces.KnownTurrets(ai, (float2)target + 0.5f, play.ArmyRadius, owner));
            float advantage = AiForces.Advantage(ai, ours, theirs);
            if (advantage < need) return false;
            group.Clear();
            holding.Clear();
            float keep2 = play.ArmyRadius * play.ArmyRadius;
            guard.Clear();
            float total = 0f;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (Busy(state, u.Id)) continue;
                if (counter && math.distancesq(u.Position, threatAt) <= keep2) continue;
                group.Add(u.Id);
                float v = StrengthEstimate.Of(ai.Game, u);
                total += v;
                guard.Add((u.Id, math.distancesq(u.Position, state.GatherPoint), v));
            }
            KeepHomeGuard(ai, total);
            if (group.Count == 0) return false;
            Note(ai, $"from {GroupCentre(view, false).x:F0},{GroupCentre(view, false).y:F0} home {state.Home.x:F0},{state.Home.y:F0}");
            Note(ai, $"attack {(counter ? "(counter) " : maxed ? "(maxed) " : slip ? "(slip) " : "")}group {group.Count} advantage {Shown(advantage)} need {need:F2} target {target.x},{target.y} building {id != 0}");
            Mode = ArmyMode.Attack;
            TargetId = id;
            TargetOwner = owner;
            Target = target;
            targetSince = ai.Now;
            attackedOnce = true;
            Attacks++;
            StartRecord(ai, target, ours.Value);
            BeginStage(ai, view, state);
            return true;
        }

        private readonly List<(int Id, float Distance, float Value)> guard = new List<(int, float, float)>(64);
        private static readonly System.Comparison<(int Id, float Distance, float Value)> NearestFirst = (a, b) => a.Distance.CompareTo(b.Distance);

        /// <summary>
        /// After a threat at its base (within <c>HomeGuardSeconds</c>), the units nearest its gathering point worth
        /// <c>HomeGuardShare</c> of the group stay home (AI overhaul §3.5). Uses <see cref="guard"/>.
        /// </summary>
        private void KeepHomeGuard(AiContext ai, float total)
        {
            AiPolicyConfig policy = ai.Policy;
            if (policy.HomeGuardShare <= 0f || ai.Now - ai.Board.LastBaseThreat > policy.HomeGuardSeconds || guard.Count < 2) return;
            guard.Sort(NearestFirst);
            float kept = 0f, wanted = total * policy.HomeGuardShare;
            for (int k = 0; k < guard.Count - 1 && kept < wanted; k++)
            {
                group.Remove(guard[k].Id);
                kept += guard[k].Value;
            }
        }

        private static string Shown(float advantage) => float.IsPositiveInfinity(advantage) ? "inf" : advantage.ToString("F2");

        /// <summary>
        /// Sends the group to a staging point <c>StageDistance</c> short of the target, on the side it comes from, so it
        /// arrives together instead of strung out along the route; a target that close engages at once.
        /// </summary>
        private void BeginStage(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            Sieging = false;
            float2 from = GroupCentre(view, false);
            float2 to = (float2)Target + 0.5f;
            float d = math.distance(from, to);
            if (play.StageDistance <= 0f || d <= play.StageDistance * 1.5f)
            {
                engaged = true;
                OrderGroup(ai, view, state, true);
                return;
            }
            stage = state.GroundNear(to + (from - to) / d * play.StageDistance, (int)play.StageDistance);
            if (view.TileAt(stage) != TileType.Ground)
            {
                // No open ground there (rock all round): no staging, it goes straight in.
                engaged = true;
                OrderGroup(ai, view, state, true);
                return;
            }
            engaged = false;
            stagedAt = ai.Now;
            marchStarted = ai.Now;
            // The staging wait starts at the latest once its slowest unit could have got there (with slack).
            float slowest = float.MaxValue;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (!group.Contains(u.Id)) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c != null && c.MoveSpeed > 0f) slowest = math.min(slowest, c.MoveSpeed);
            }
            marchLimit = math.max(play.MarchSeconds, slowest < float.MaxValue ? math.distance(from, (float2)stage + 0.5f) / slowest * play.MarchSlack : 0f);
            GroupIds(view, state, false);
            if (ids.Count > 0) ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.AttackMove, stage), AiKeys.Of(AiKeys.Army));
            orderedAt[AiKeys.Army] = ai.Now;
        }

        /// <summary>
        /// The best target it knows (AI overhaul §3.4; never anything it hasn't seen): every known enemy building (ghosts
        /// count), every remembered HQ its view no longer shows (id -1) and every last-seen army (id 0), scored by worth
        /// over distance (<see cref="ScoreTarget"/>), kept only when its whole force's advantage over what it knows defends
        /// the target (turrets there, an army seen there) reaches <paramref name="need"/>. <paramref name="preferOwner"/>'s
        /// targets score double (an attack moving on through a base). A target slip takes the second best.
        /// </summary>
        internal static bool ChooseTarget(AiContext ai, float2 from, int preferOwner, float need, out int id, out int owner, out int2 target)
        {
            id = 0;
            owner = 0;
            target = default;
            Force ours = AiForces.Own(ai);
            float noise = ai.Noise();
            float best = 0f, second = 0f;
            int bestId = 0, bestOwner = 0, secondId = 0, secondOwner = 0;
            int2 bestAt = default, secondAt = default;
            void Consider(int candidateId, int candidateOwner, int2 at, float worth)
            {
                float score = ScoreTarget(ai, ours, from, candidateOwner, at, worth, need, noise);
                if (candidateOwner == preferOwner) score *= 2f;
                if (score <= 0f) return;
                if (score > best)
                {
                    second = best; secondId = bestId; secondOwner = bestOwner; secondAt = bestAt;
                    best = score; bestId = candidateId; bestOwner = candidateOwner; bestAt = at;
                }
                else if (score > second) { second = score; secondId = candidateId; secondOwner = candidateOwner; secondAt = at; }
            }
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
            {
                if (b.Type == BuildingType.Bomb) continue;
                Consider(b.Id, b.OwnerId, b.Anchor, WorthOf(ai, b.Type));
            }
            // An HQ it saw that its view doesn't show any more (a Jammer hides it): its remembered spot (id -1).
            foreach (KeyValuePair<int, int2> hq in ai.Board.KnownHqs)
            {
                bool shown = false;
                foreach (AiBuilding b in ai.Board.EnemyBuildings.Values) if (b.OwnerId == hq.Key && b.Type == BuildingType.Base) { shown = true; break; }
                if (!shown) Consider(-1, hq.Key, hq.Value, ai.Policy.HqValue);
            }
            foreach (ArmySighting army in ai.Board.Armies.Values)
                Consider(0, army.OwnerId, (int2)math.floor(army.Centre), StrengthEstimate.Of(ai.Game, army));
            if (best <= 0f) return false;
            bool slip = second > 0f && ai.Slip(AiSlip.Target);
            id = slip ? secondId : bestId;
            owner = slip ? secondOwner : bestOwner;
            target = slip ? secondAt : bestAt;
            return true;
        }

        /// <summary>A target's worth (AI overhaul §3.4): an HQ wins the game, a Miner adds the income it denies, the rest their cost.</summary>
        internal static float WorthOf(AiContext ai, BuildingType type)
        {
            if (type == BuildingType.Base) return ai.Policy.HqValue;
            float cost = ai.Game.Buildings.TryGetValue(type, out BuildingConfig c) ? c.UpfrontCost : 0f;
            return type == BuildingType.Miner ? cost + ai.Policy.MinerIncomeValue : cost;
        }

        /// <summary>
        /// A target's score: its worth over (1 + distance / <c>TargetDistanceScale</c>), times <c>AvoidFactor</c> where an
        /// attack lost badly; 0 when <paramref name="ours"/> can't beat what it knows defends the target (the turrets it
        /// knows within <c>ArmyRadius</c>, and its owner's remembered army when that was last seen within two radii).
        /// </summary>
        internal static float ScoreTarget(AiContext ai, in Force ours, float2 from, int owner, int2 at, float worth, float need, float noise)
        {
            if (worth <= 0f) return 0f;
            // A finishing blow (AI overhaul): far stronger than the army it remembers of that enemy, its HQ is worth that much more.
            if (worth >= ai.Policy.HqValue && FightModel.Advantage(ours, AiForces.EnemyArmy(ai, owner), ai.Policy.RangeWeight) * noise >= ai.Policy.FinishAdvantage)
                worth *= ai.Policy.FinishAdvantage;
            float radius = ai.Config.Play.ArmyRadius;
            float2 centre = (float2)at + 0.5f;
            Force defence = AiForces.KnownTurrets(ai, centre, radius, owner);
            if (ai.Board.Armies.TryGetValue(owner, out ArmySighting seen) && math.distancesq(seen.Centre, centre) <= 4f * radius * radius)
                defence.Add(AiForces.EnemyArmy(ai, owner));
            if (need > 0f && FightModel.Advantage(ours, defence, ai.Policy.RangeWeight) * noise < need) return 0f;
            float score = worth / (1f + math.distance(from, centre) / ai.Policy.TargetDistanceScale);
            if (ai.Board.IsAvoided(at, radius)) score *= ai.Policy.AvoidFactor;
            return score;
        }

        private void Fight(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            if (group.Count == 0) { Note(ai, "attack over: group gone"); EndRecord(ai, state); EndAttack(ai, state, false); return; }
            float2 centre = GroupCentre(view, false);

            // The target is gone (seen gone, or the army moved on): the next one, nearest the army.
            bool known = TargetId > 0 ? ai.Board.EnemyBuildings.ContainsKey(TargetId)
                : TargetId < 0 ? ai.Board.KnownHqs.TryGetValue(TargetOwner, out int2 hqAt) && hqAt.Equals(Target)
                : ai.Board.Armies.ContainsKey(TargetOwner);
            bool reachedEmpty = TargetId == 0 && math.distancesq(centre, Target) < play.ArmyRadius * play.ArmyRadius && EnemiesNear(view, centre, play.ArmyRadius) == 0;
            // A last-seen army it reached and doesn't find there any more: it forgets that sighting (else it picks it again).
            if (reachedEmpty) ai.Board.Armies.Remove(TargetOwner);
            // Chasing a last-seen army that long without reaching it: it drops that sighting and picks again.
            if (TargetId == 0 && ai.Now - targetSince > play.MarchSeconds) { ai.Board.Armies.Remove(TargetOwner); known = false; }
            // A building it now sees beats a remembered HQ spot.
            if (TargetId < 0 && ai.Board.EnemyBuildings.Count > 0) known = false;
            // A building target it hasn't reached in MarchSeconds (walled off, across rock, a ghost it never sees again):
            // it gives up on it for AvoidSeconds and picks again (else the whole army, reinforcements and all, waits for ever).
            bool unreached = TargetId != 0 && ai.Now - targetSince > play.MarchSeconds
                && math.distancesq(FrontCentre(view, state, play.ArmyRadius), (float2)Target + 0.5f) > play.ArmyRadius * play.ArmyRadius;
            if (unreached)
            {
                Note(ai, $"gave up on {Target.x},{Target.y}: not reached in {play.MarchSeconds:F0} s");
                ai.Board.Avoid(Target, ai.Now + ai.Policy.AvoidSeconds);
                int2 given = Target;
                if (!ChooseTarget(ai, centre, TargetOwner, Maxed(ai, state) ? 0f : 1f, out int nid, out int nowner, out int2 ntarget) || ntarget.Equals(given))
                {
                    EndRecord(ai, state);
                    EndAttack(ai, state, true);
                    return;
                }
                TargetId = nid;
                TargetOwner = nowner;
                Target = ntarget;
                targetSince = ai.Now;
                if (engaged) OrderGroup(ai, view, state, true);
                else BeginStage(ai, view, state);
            }
            if (!known || reachedEmpty)
            {
                // Moving on: any target its group beats (maxed: any at all).
                if (!ChooseTarget(ai, centre, TargetOwner, Maxed(ai, state) ? 0f : 1f, out int id, out int owner, out int2 target)) { Note(ai, $"attack over: no target left, group {group.Count}"); EndRecord(ai, state); EndAttack(ai, state, true); return; }
                Note(ai, $"next target {target.x},{target.y} building {id != 0}");
                TargetId = id;
                TargetOwner = owner;
                Target = target;
                targetSince = ai.Now;
                if (engaged) OrderGroup(ai, view, state, true);
                else BeginStage(ai, view, state);
            }

            // The fight is at the front (the group strings out between home and the target): the units nearest the target.
            float2 front = FrontCentre(view, state, play.ArmyRadius);
            // Retreat and regroup checks are micro: on its attentive thinks only (AI overhaul §2.3).
            if (ai.Difficulty.Retreats && ai.Attentive)
            {
                centre = front;
                // Engaged: the group within two fight radii (what can join the fight). Marching to its staging point: the whole
                // group (a strung-out march meeting a defence is not a lost fight; it gathers at the stage first).
                Force local = engaged ? AiForces.Own(ai, group, centre, 2f * play.ArmyRadius) : AiForces.Own(ai, group);
                Force all = AiForces.Own(ai, group);
                Force theirs = AiForces.EnemyNear(ai, centre, play.ArmyRadius);
                float noise = ai.Noise();
                float localAdvantage = FightModel.Advantage(local, theirs, ai.Policy.RangeWeight) * noise;
                float allAdvantage = FightModel.Advantage(all, theirs, ai.Policy.RangeWeight) * noise;
                float retreatAt = ai.Difficulty.RetreatAdvantage;
                // Maxed out (no upkeep room, money piling up), a trade costs it nothing it can't rebuild: it fights on.
                bool maxed = Maxed(ai, state);
                if (!maxed && engaged && !theirs.Empty && localAdvantage < retreatAt && local.Value < all.Value * play.RegroupShare && allAdvantage >= retreatAt
                    && !regroupedFor.Equals(Target))
                {
                    regroupedFor = Target;
                    // Only its vanguard is in the fight (the group strung out): the front falls back to the main body and
                    // the group stages there again, rather than the whole army going home.
                    float2 body = GroupCentre(view, false);
                    Note(ai, $"regroup at {body.x:F0},{body.y:F0}: front {Shown(localAdvantage)} of whole {Shown(allAdvantage)}");
                    stage = state.GroundNear(body, (int)play.StageDistance);
                    engaged = false;
                    Sieging = false;
                    stagedAt = ai.Now;
                    marchStarted = ai.Now;
                    marchLimit = play.StageSeconds;
                    GroupIds(view, state, false);
                    if (ids.Count > 0) ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.Move, stage), AiKeys.Of(AiKeys.Army));
                    orderedAt[AiKeys.Army] = ai.Now;
                    return;
                }
                // A retreat slip (AI overhaul §2.3): it misses this check and fights on.
                // Already regrouped for this target and the whole group wins: it fights on (the rest are on their way).
                bool wholeWins = regroupedFor.Equals(Target) && allAdvantage >= retreatAt;
                if (!maxed && !wholeWins && !theirs.Empty && localAdvantage < retreatAt && !ai.Slip(AiSlip.Retreat))
                {
                    Note(ai, $"retreat group {group.Count} advantage {Shown(localAdvantage)}");
                    EndRecord(ai, state);
                    Mode = ArmyMode.Retreat;
                    Sieging = false;
                    Retreats++;
                    retreatStarted = ai.Now;
                    lastAttackEnd = ai.Now;
                    GroupIds(view, state, false);
                    ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Army));
                    return;
                }
            }

            if (!engaged)
            {
                // Staging: engage once most of the group is there (or it has waited long enough); stragglers come on.
                float2 s = (float2)stage + 0.5f;
                float sr2 = play.ArmyRadius * play.ArmyRadius;
                int there = 0;
                ids.Clear();
                foreach (int i in state.Combat)
                {
                    AiUnit u = view.OwnUnits[i];
                    if (!group.Contains(u.Id)) continue;
                    if (math.distancesq(u.Position, s) <= sr2) there++;
                    else if (!u.HasOrder) ids.Add(u.Id);
                }
                if (ai.Now - lastMarchNote >= 60.0)
                {
                    lastMarchNote = ai.Now;
                    float2 sum = 0;
                    int n = 0, ordered = 0, working = 0, held = 0;
                    foreach (int i in state.Combat)
                    {
                        AiUnit u = view.OwnUnits[i];
                        if (!group.Contains(u.Id)) continue;
                        sum += u.Position; n++;
                        if (u.HasOrder) ordered++;
                        if (u.Work != 0) working++;
                        if (u.Stance == 3) held++;
                    }
                    float2 c = n > 0 ? sum / n : 0;
                    Note(ai, $"march: {n} at {c.x:F0},{c.y:F0} to {stage.x},{stage.y}, {there} there, {ordered} ordered, {working} working, {held} holding");
                }
                // The wait starts when the first unit gets there (a long march is not a wait), at most MarchSeconds in all.
                if (there == 0 && ai.Now - marchStarted < marchLimit) stagedAt = ai.Now;
                if (there >= group.Count * play.StageShare || ai.Now - stagedAt > play.StageSeconds)
                {
                    int moving = 0;
                    float2 sum = 0;
                    float far = 0f;
                    foreach (int i in state.Combat)
                    {
                        AiUnit u = view.OwnUnits[i];
                        if (!group.Contains(u.Id)) continue;
                        if (u.HasOrder) moving++;
                        sum += u.Position;
                        far = math.max(far, math.distance(u.Position, s));
                    }
                    float2 c = group.Count > 0 ? sum / group.Count : 0;
                    Note(ai, $"engage: {there} of {group.Count} staged at {stage.x},{stage.y} (tile {ai.View.TileAt(stage)}), centre {c.x:F0},{c.y:F0}, farthest {far:F0}, {moving} ordered, target {Target.x},{Target.y}");
                    engaged = true;
                    OrderGroup(ai, view, state, true);
                }
                else
                {
                    if (ids.Count > 0 && Due(ai, AiKeys.Army)) ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, stage), AiKeys.Of(AiKeys.Army));
                    Reinforce(ai, view, state);
                    if (ai.Attentive) Detonate(ai, view, state);
                    return;
                }
            }

            Reinforce(ai, view, state);
            UpdateSiege(ai, view, state);
            if (!Sieging)
            {
                // Units that arrived (or lost their order) go on to the target.
                ids.Clear();
                foreach (int i in state.Combat)
                {
                    AiUnit u = view.OwnUnits[i];
                    if (group.Contains(u.Id) && !u.HasOrder && !(ai.Difficulty.KeepsBacklineBack && IsBackline(u.Type))) ids.Add(u.Id);
                }
                if (ids.Count > 0 && Due(ai, AiKeys.Army)) ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, Target), AiKeys.Of(AiKeys.Army));
            }
            // Micro (AI overhaul §2.3): on its attentive thinks only.
            if (!ai.Attentive) return;
            if (ai.Difficulty.FocusFire) FocusFire(ai, view, state, front);
            if (ai.Difficulty.KeepsBacklineBack && !Sieging) Backline(ai, view, state, centre);
            if (ai.Difficulty.DigsPastWalls) DigWalls(ai, view, state, centre);
            Detonate(ai, view, state);
        }

        // ---- sieges and focus fire (AI overhaul §3.4, §3.6) ----

        /// <summary>
        /// Starts or ends a siege: turrets it knows at the target and units of its group that out-range them (by more than
        /// a tile) start one; the turrets there all gone (or none of its units out-ranging them left) end it, and the
        /// whole group engages.
        /// </summary>
        private void UpdateSiege(AiContext ai, TeamView view, AiState state)
        {
            float radius = ai.Config.Play.ArmyRadius;
            float turretRange = 0f;
            float best = float.MaxValue;
            int2 nearest = Target;
            float2 centre = GroupCentre(view, false);
            foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
            {
                if (b.Progress < 100 || !ai.Game.Buildings.TryGetValue(b.Type, out BuildingConfig c) || !c.IsAttacker) continue;
                if (math.distancesq((float2)b.Anchor, (float2)Target) > radius * radius) continue;
                turretRange = math.max(turretRange, c.Range);
                float d = math.distancesq((float2)b.Anchor, centre);
                if (d < best) { best = d; nearest = b.Anchor; }
            }
            int outranging = 0;
            if (turretRange > 0f)
                foreach (int i in state.Combat)
                {
                    AiUnit u = view.OwnUnits[i];
                    UnitConfig c = state.UnitOf(u.Type);
                    if (group.Contains(u.Id) && c != null && c.Range > turretRange + 1f) outranging++;
                }
            bool siege = turretRange > 0f && outranging > 0;
            if (!siege)
            {
                if (Sieging)
                {
                    Sieging = false;
                    Note(ai, "siege over: the whole group engages");
                    OrderGroup(ai, view, state, true);
                }
                return;
            }
            if (!Sieging) Note(ai, $"siege: {outranging} out-range the turrets at {Target.x},{Target.y}");
            Sieging = true;
            if (!Due(ai, AiKeys.Siege)) return;
            // Out-ranging units shell the nearest turret; the rest hold at the staging point, out of its reach.
            ids.Clear();
            back.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (!group.Contains(u.Id)) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c != null && c.Range > turretRange + 1f) ids.Add(u.Id);
                else if (math.distancesq(u.Position, (float2)stage) > radius * radius * 0.25f) back.Add(u.Id);
            }
            if (ids.Count > 0) ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.AttackMove, nearest), AiKeys.Of(AiKeys.Army));
            if (back.Count > 0) ai.Propose(AiPriority.High, AiAction.OrderUnits(back, OrderKind.Move, stage), AiKeys.Of(AiKeys.Siege));
        }

        /// <summary>
        /// Focus fire, as close as attack-moves get (AI overhaul §3.6): its group's units already in range of the weakest
        /// enemy it sees near the front (within their range plus a tile) attack-move onto that enemy's tile; at least two of
        /// them, so a lone unit doesn't leave the group. The rest keep the group order.
        /// </summary>
        private void FocusFire(AiContext ai, TeamView view, AiState state, float2 front)
        {
            if (!FocusTarget(view, front, ai.Config.Play.ArmyRadius, out float2 weakest)) return;
            ids.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (!group.Contains(u.Id)) continue;
                UnitConfig c = state.UnitOf(u.Type);
                if (c == null || c.Range <= 0f) continue;
                float reach = c.Range + 1f;
                if (math.distancesq(u.Position, weakest) <= reach * reach) ids.Add(u.Id);
            }
            if (ids.Count >= 2 && Due(ai, AiKeys.Focus)) ai.Propose(AiPriority.Normal + 5f, AiAction.OrderUnits(ids, OrderKind.AttackMove, (int2)math.floor(weakest)), AiKeys.Of(AiKeys.Focus));
        }

        // ---- trades (AI overhaul §4.1) ----

        /// <summary>Starts the attack's ledger: the kills and losses so far, its worth, and whether turrets defend the target more than an army.</summary>
        private void StartRecord(AiContext ai, int2 target, float value)
        {
            recording = true;
            recordEnemyLost = ai.Board.EnemyLost;
            recordOwnLost = ai.Board.OwnLost;
            recordValue = value;
            recordTarget = target;
            float turrets = AiForces.KnownTurrets(ai, (float2)target + 0.5f, ai.Config.Play.ArmyRadius, TargetOwner).Value;
            recordTurrets = turrets > 0f && turrets >= AiForces.EnemyArmy(ai, TargetOwner).Value;
        }

        /// <summary>
        /// Ends the attack's ledger: its trade (enemy worth lost over its own) moves its caution, and a bad one marks the
        /// target avoided (and, when turrets defended it, weighs its counters against turrets more). An attack that never
        /// really fought (both losses under a tenth of its worth) teaches nothing.
        /// </summary>
        private void EndRecord(AiContext ai, AiState state)
        {
            if (!recording) return;
            recording = false;
            AiPolicyConfig policy = ai.Policy;
            float enemy = ai.Board.EnemyLost - recordEnemyLost, own = ai.Board.OwnLost - recordOwnLost;
            if (enemy + own < 0.1f * recordValue) return;
            float trade = enemy / math.max(1f, own);
            LastTrade = trade;
            string verdict = "even";
            if (trade < policy.BadTrade)
            {
                Caution = math.min(policy.MaxCaution, Caution * policy.CautionUp);
                BadTradeStreak++;
                ai.Board.Avoid(recordTarget, ai.Now + policy.AvoidSeconds);
                if (recordTurrets) state.TurretCounterBoost = policy.TurretCounterBoost;
                verdict = "bad";
            }
            else if (trade > policy.GoodTrade)
            {
                Caution = math.max(policy.MinCaution, Caution * policy.CautionDown);
                BadTradeStreak = 0;
                verdict = "good";
            }
            string line = $"{state.PlayingSeconds / 60.0:F1}m {verdict} trade {trade:F2} (killed {enemy:F0}, lost {own:F0}) at {recordTarget.x},{recordTarget.y}, caution {Caution:F2}";
            if (Trades.Count >= 20) Trades.RemoveAt(0);
            Trades.Add(line);
            Note(ai, line);
        }

        private void EndAttack(AiContext ai, AiState state, bool sendHome)
        {
            Mode = ArmyMode.Gather;
            Sieging = false;
            lastAttackEnd = ai.Now;
            if (sendHome && group.Count > 0)
            {
                ids.Clear();
                foreach (int id in group) ids.Add(id);
                ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Army));
            }
            group.Clear();
            TargetId = 0;
        }

        /// <summary>Orders the whole group at the target (with <c>KeepsBacklineBack</c>: its Artillery and Snipers to a point behind the front).</summary>
        private void OrderGroup(AiContext ai, TeamView view, AiState state, bool force)
        {
            GroupIds(view, state, ai.Difficulty.KeepsBacklineBack);
            if (ids.Count > 0) ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.AttackMove, Target), AiKeys.Of(AiKeys.Army));
            orderedAt[AiKeys.Army] = ai.Now;
            if (ai.Difficulty.KeepsBacklineBack) Backline(ai, view, state, GroupCentre(view, true), force);
        }

        /// <summary><c>KeepsBacklineBack</c>: Artillery and Snipers attack-move to a point <c>BacklineDistance</c> behind the front, towards home.</summary>
        private void Backline(AiContext ai, TeamView view, AiState state, float2 front, bool force = false)
        {
            if (!force && !Due(ai, AiKeys.Backline)) return;
            back.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) && IsBackline(u.Type)) back.Add(u.Id);
            }
            if (back.Count == 0) return;
            float2 toTarget = math.normalizesafe((float2)Target - front);
            float2 spot = front - toTarget * ai.Config.Play.BacklineDistance;
            if (math.distancesq(front, state.Home) < 1f) spot = math.lerp(state.Home, Target, 0.5f);
            int2 tile = math.clamp((int2)math.floor(spot), 0, new int2(view.Width - 1, view.Height - 1));
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(back, OrderKind.AttackMove, tile), AiKeys.Of(AiKeys.Backline));
            orderedAt[AiKeys.Backline] = ai.Now;
        }

        private static bool IsBackline(UnitType t) => t == UnitType.Artillery || t == UnitType.Sniper;

        /// <summary>Units gathered at home join the attack once there are <c>ReinforceSize</c> of them.</summary>
        private void Reinforce(AiContext ai, TeamView view, AiState state)
        {
            if (Mode != ArmyMode.Attack) return;
            ids.Clear();
            float2 g = (float2)state.GatherPoint + 0.5f;
            float r2 = ai.Config.Play.ArmyRadius * ai.Config.Play.ArmyRadius;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) || Busy(state, u.Id) || math.distancesq(u.Position, g) > r2) continue;
                ids.Add(u.Id);
            }
            if (ids.Count < ai.Config.Play.ReinforceSize) return;
            // A home guard after a threat at its base stays home (AI overhaul §3.5).
            if (ai.Policy.HomeGuardShare > 0f && ai.Now - ai.Board.LastBaseThreat <= ai.Policy.HomeGuardSeconds) return;
            foreach (int id in ids) group.Add(id);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, engaged ? Target : stage), AiKeys.Of(AiKeys.Army, 1));
        }

        /// <summary>Focus fire: the most damaged enemy unit it sees near its army.</summary>
        private static bool FocusTarget(TeamView view, float2 centre, float radius, out float2 at)
        {
            at = default;
            float r2 = radius * radius, lowest = float.MaxValue;
            foreach (AiUnit u in view.OtherUnits)
            {
                if (!view.IsEnemy(u.OwnerId) || math.distancesq(u.Position, centre) > r2) continue;
                if (u.Health < lowest) { lowest = u.Health; at = u.Position; }
            }
            return lowest < float.MaxValue;
        }

        private static int EnemiesNear(TeamView view, float2 at, float radius)
        {
            int n = 0;
            float r2 = radius * radius;
            foreach (AiUnit u in view.OtherUnits) if (view.IsEnemy(u.OwnerId) && math.distancesq(u.Position, at) <= r2) n++;
            return n;
        }

        /// <summary><c>DigsPastWalls</c>: Diggers in the group dig out the enemy walls it knows near the army.</summary>
        private void DigWalls(AiContext ai, TeamView view, AiState state, float2 centre)
        {
            if (!Due(ai, AiKeys.Dig)) return;
            back.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) && u.Type == UnitType.Digger && u.Work == 0) back.Add(u.Id);
            }
            if (back.Count == 0) return;
            float r = ai.Config.Play.ArmyRadius;
            float best = r * r;
            int wall = -1;
            foreach (KeyValuePair<int, AiWall> w in view.Walls)
            {
                if (w.Value.OwnerId == ai.Owner || !view.IsEnemy(w.Value.OwnerId)) continue;
                float2 at = new float2(w.Key % view.Width, w.Key / view.Width) + 0.5f;
                float d = math.distancesq(at, centre);
                if (d < best) { best = d; wall = w.Key; }
            }
            if (wall < 0) return;
            int2 t = new int2(wall % view.Width, wall / view.Width);
            int half = (int)math.max(1f, ai.Config.Play.DefenceSpread * 0.5f);
            ai.Propose(AiPriority.Normal, AiAction.DigArea(back, t - half, t + half), AiKeys.Of(AiKeys.Dig));
            orderedAt[AiKeys.Dig] = ai.Now;
        }

        /// <summary>Suicide bombers blow up beside a clump of enemies it sees, or beside an enemy wall.</summary>
        private void Detonate(AiContext ai, TeamView view, AiState state)
        {
            back.Clear();
            int checks = 0;
            UnitConfig bomber = state.UnitOf(UnitType.SuicideBomber);
            if (bomber == null) return;
            float r2 = bomber.BlastRadius * bomber.BlastRadius;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (u.Type != UnitType.SuicideBomber || !group.Contains(u.Id) || checks++ >= ai.Config.Play.BomberChecks) continue;
                int near = 0;
                foreach (AiUnit e in view.OtherUnits)
                    if (view.IsEnemy(e.OwnerId) && math.distancesq(e.Position, u.Position) <= r2 && ++near >= ai.Config.Play.BomberClump) break;
                bool wall = false;
                int2 at = (int2)math.floor(u.Position);
                for (int y = -1; y <= 1 && !wall; y++)
                    for (int x = -1; x <= 1 && !wall; x++)
                    {
                        int2 t = at + new int2(x, y);
                        if (t.x < 0 || t.y < 0 || t.x >= view.Width || t.y >= view.Height) continue;
                        wall = view.Walls.TryGetValue(t.y * view.Width + t.x, out AiWall w) && w.OwnerId != ai.Owner && view.IsEnemy(w.OwnerId);
                    }
                if (near >= ai.Config.Play.BomberClump || wall) back.Add(u.Id);
            }
            if (back.Count > 0) ai.Propose(AiPriority.High, AiAction.Detonate(back), AiKeys.Of(AiKeys.Detonate));
        }

        // ---- retreating and defending ----

        private void Retreat(AiContext ai, TeamView view, AiState state)
        {
            float2 g = (float2)state.GatherPoint + 0.5f;
            float r2 = ai.Config.Play.ArmyRadius * ai.Config.Play.ArmyRadius;
            int home = 0;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) && math.distancesq(u.Position, g) <= r2) home++;
            }
            if (ai.Now - retreatStarted > ai.Config.Play.RetreatSeconds || home * 2 >= group.Count)
            {
                Mode = ArmyMode.Gather;
                group.Clear();
                TargetId = 0;
            }
        }

        /// <summary>
        /// A threat near its base it may act on (a threat its buildings saw, past the reaction delay, or an Under-attack
        /// alert past it): home units attack-move there; the attack group comes back when home can't hold it.
        /// </summary>
        private void Defend(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            hasThreat = false;
            if (!FindThreat(ai, state, out float2 threat))
            {
                if (Mode == ArmyMode.Defend)
                {
                    Mode = ArmyMode.Gather;
                    nextGather = ai.Now; // and back to the gathering point
                }
                return;
            }
            // A threat at an expansion is met too: home units near their base or near the threat come (a threat it
            // ignored would hold the army in Defend for good).
            Force theirs = AiForces.EnemyNear(ai, threat, play.ArmyRadius);
            ids.Clear();
            home.Clear();
            float d2 = play.DefendRadius * play.DefendRadius;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) || Busy(state, u.Id)) continue;
                if (math.distancesq(u.Position, state.Home) > d2 && math.distancesq(u.Position, threat) > d2) continue;
                home.Add(u.Id);
                // Only those not already on their way there (an order is a click: it isn't repeated for nothing).
                if (!u.HasOrder || math.distancesq(u.OrderGoal, threat) > play.ArmyRadius * play.ArmyRadius) ids.Add(u.Id);
            }
            Force homeForce = AiForces.Own(ai, home);
            hasThreat = true;
            threatAt = threat;
            homeAdvantage = AiForces.Advantage(ai, homeForce, theirs);
            bool atBase = math.distance(threat, state.Home) <= play.DefendRadius;
            if (atBase) ai.Board.LastBaseThreat = ai.Now;
            // Home can't hold a real attack on its base (not a scout or a raid on an outpost): the attack comes back,
            // unless it is already at its target (a base trade it may win).
            float groupValue = Mode == ArmyMode.Attack ? AiForces.Own(ai, group).Value : 0f;
            bool serious = theirs.Value >= groupValue * play.RecallShare && atBase;
            bool atTarget = engaged && math.distancesq(FrontCentre(view, state, play.ArmyRadius), Target) <= 4f * play.ArmyRadius * play.ArmyRadius;
            if (Mode == ArmyMode.Attack && homeAdvantage < 1f && serious && !atTarget)
            {
                Note(ai, $"recalled: home advantage {Shown(homeAdvantage)}, threat {theirs.Value:F0}");
                EndRecord(ai, state);
                // Home can't hold: the attack comes back.
                foreach (int id in group) ids.Add(id);
                group.Clear();
                Sieging = false;
                lastAttackEnd = ai.Now;
                Mode = ArmyMode.Defend;
            }
            else if (Mode != ArmyMode.Attack && Mode != ArmyMode.Retreat) Mode = ArmyMode.Defend;
            foreach (int id in ids) holding.Remove(id);
            if (ids.Count > 0 && Due(ai, AiKeys.Defend))
                ai.Propose(AiPriority.High + 5f, AiAction.OrderUnits(ids, OrderKind.AttackMove, (int2)math.floor(threat)), AiKeys.Of(AiKeys.Defend));
        }

        private readonly HashSet<int> home = new HashSet<int>();

        private bool FindThreat(AiContext ai, AiState state, out float2 at)
        {
            at = default;
            double newest = double.NegativeInfinity;
            Blackboard board = ai.Board;
            float recent = ai.Config.Play.OrderSeconds + board.ReactionSeconds;
            // The current threat nearest its HQ first (an HQ falls in seconds; an outpost can wait).
            float nearest = float.MaxValue;
            foreach (Threat t in board.Threats.Values)
            {
                if (!board.IsActionable(t) || board.Now - t.LastSeen > recent) continue;
                float d = math.distancesq(t.Position, state.Home);
                if (d < nearest) { nearest = d; newest = t.LastSeen; at = t.Position; }
            }
            if (newest > double.NegativeInfinity) return true;
            board.Alerts.Ready(board.Now, alerts);
            foreach (AiAlert a in alerts)
            {
                if (a.Kind != AlertKind.UnderAttack || board.Now - a.Time > board.ReactionSeconds + recent) continue;
                float d = math.distancesq((float2)a.Tile + 0.5f, state.Home);
                if (d < nearest) { nearest = d; newest = a.Time; at = (float2)a.Tile + 0.5f; }
            }
            return newest > double.NegativeInfinity;
        }

        // ---- helpers ----

        /// <summary>The centre of the group's units within <paramref name="radius"/> of its unit nearest the target.</summary>
        private float2 FrontCentre(TeamView view, AiState state, float radius)
        {
            float best = float.MaxValue;
            float2 lead = Target;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (!group.Contains(u.Id)) continue;
                float d = math.distancesq(u.Position, Target);
                if (d < best) { best = d; lead = u.Position; }
            }
            float2 sum = 0;
            int n = 0;
            float r2 = radius * radius;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (!group.Contains(u.Id) || math.distancesq(u.Position, lead) > r2) continue;
                sum += u.Position;
                n++;
            }
            return n > 0 ? sum / n : lead;
        }

        private float2 GroupCentre(TeamView view, bool frontOnly)
        {
            float2 sum = 0;
            int n = 0;
            foreach (AiUnit u in view.OwnUnits)
            {
                if (!group.Contains(u.Id) || (frontOnly && IsBackline(u.Type))) continue;
                sum += u.Position;
                n++;
            }
            return n > 0 ? sum / n : (float2)Target;
        }

        /// <summary>Fills <see cref="ids"/> with the group (without its Artillery and Snipers when <paramref name="frontOnly"/>).</summary>
        private void GroupIds(TeamView view, AiState state, bool frontOnly)
        {
            ids.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) && !(frontOnly && IsBackline(u.Type))) ids.Add(u.Id);
            }
        }

        /// <summary>True when the group keyed <paramref name="key"/> may be re-ordered now (at most every <c>OrderSeconds</c>).</summary>
        private bool Due(AiContext ai, int key)
        {
            if (orderedAt.TryGetValue(key, out double at) && ai.Now - at < ai.Config.Play.OrderSeconds) return false;
            orderedAt[key] = ai.Now;
            return true;
        }
    }
}
