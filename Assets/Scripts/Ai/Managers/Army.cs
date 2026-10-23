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
    /// Army (spec §5.3, plan Task 7). Its fighting units gather at a point in front of its HQ towards the enemy it knows
    /// (Turtle: at its chokepoint, holding it). Threats near its base (enemies seen by its buildings, or Under-attack
    /// alerts, both after the reaction delay) are met by its home units, and the attack group is called back when home
    /// can't hold. It attacks when the play time, wave interval and army size allow and its strength (what it has) beats
    /// the enemy's (what it has seen: the last-seen army plus known turrets there) by its ratio; the target is a known
    /// enemy building (its HQ first, ghosts included) or else a last-seen army, never anything it hasn't seen. In the
    /// fight: Normal and Hard retreat when the local fight turns against them; Hard focuses fire on the weakest enemy in
    /// sight, keeps Artillery and Snipers <c>BacklineDistance</c> behind the front, and digs through enemy walls with
    /// its Diggers; Suicide bombers detonate on clumps and walls. Units reach the army through the rally point; new ones
    /// gathered at home join an attack as reinforcements.
    /// </summary>
    public sealed class ArmyManager : IAiManager
    {
        private readonly HashSet<int> group = new HashSet<int>();
        private readonly HashSet<int> alive = new HashSet<int>();
        private readonly HashSet<int> holding = new HashSet<int>();
        private readonly List<int> ids = new List<int>(256), back = new List<int>(32), stale = new List<int>(32);
        private readonly List<AiAlert> alerts = new List<AiAlert>(8);
        private readonly Dictionary<int, double> orderedAt = new Dictionary<int, double>();
        private double lastAttackEnd = double.NegativeInfinity, retreatStarted, nextGather = double.NegativeInfinity;
        private bool attackedOnce;

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
            foreach (int id in group) if (!alive.Contains(id) || state.Scouts.Contains(id)) stale.Add(id);
            foreach (int id in stale) group.Remove(id);
            stale.Clear();
            foreach (int id in holding) if (!alive.Contains(id) || group.Contains(id)) stale.Add(id);
            foreach (int id in stale) holding.Remove(id);

            Defend(ai, view, state);
            switch (Mode)
            {
                case ArmyMode.Attack: Fight(ai, view, state); break;
                case ArmyMode.Retreat: Retreat(ai, view, state); break;
                case ArmyMode.Gather: Gather(ai, view, state); break;
            }
            if (orderedAt.Count > 64) orderedAt.Clear();
        }

        // ---- gathering ----

        private void PlaceGatherPoint(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            float2 enemy = BuilderManager.EnemyDirectionPoint(ai, view, state);
            float2 dir = math.normalizesafe(enemy - state.Home, new float2(1f, 0f));
            float2 point = ai.Style.HoldsChokes && state.DefenceSpots.Count > 0
                ? state.DefenceSpots[0] - state.DefenceDirs[0] * play.DefenceSpread * 0.5f
                : state.Home + dir * play.GatherDistance;
            state.GatherPoint = state.GroundNear(point, (int)play.DefenceSpread);
            state.HasGatherPoint = true;
        }

        private void Gather(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            int army = CountArmy(view, state);
            if (TryStartAttack(ai, view, state, army)) return;

            // Units away from the gathering point with nothing to do go there; Turtle holds it.
            float2 g = (float2)state.GatherPoint + 0.5f;
            float r2 = play.ArmyRadius * play.ArmyRadius;
            ids.Clear();
            back.Clear();
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (state.Scouts.Contains(u.Id) || u.HasOrder || u.Stance == 3) continue;
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

        private int CountArmy(TeamView view, AiState state)
        {
            int n = 0;
            foreach (int i in state.Combat) if (!state.Scouts.Contains(view.OwnUnits[i].Id)) n++;
            return n;
        }

        // ---- attacking ----

        private bool TryStartAttack(AiContext ai, TeamView view, AiState state, int army)
        {
            if (state.PlayingSeconds < ai.Style.FirstAttackSeconds || army < ai.Style.AttackArmySize) return false;
            if (attackedOnce && ai.Now - lastAttackEnd < ai.Style.AttackIntervalSeconds) return false;
            if (!ChooseTarget(ai, state.Home, -1, true, out int id, out int owner, out int2 target)) return false;
            float ours = StrengthEstimate.Own(ai.Game, view, default, 0f);
            float theirs = StrengthEstimate.Enemy(ai.Game, ai.Board, owner, target, ai.Config.Play.ArmyRadius * 2f, ai.Config.Play.TurretStrength);
            if (ours < theirs * ai.Difficulty.AttackRatio * ai.Style.AttackRatioScale) return false;
            group.Clear();
            holding.Clear();
            foreach (int i in state.Combat)
            {
                int uid = view.OwnUnits[i].Id;
                if (!state.Scouts.Contains(uid)) group.Add(uid);
            }
            Mode = ArmyMode.Attack;
            TargetId = id;
            TargetOwner = owner;
            Target = target;
            attackedOnce = true;
            Attacks++;
            OrderGroup(ai, view, state, true);
            return true;
        }

        /// <summary>
        /// A target it knows (never anything it hasn't seen): with <paramref name="hqFirst"/> the nearest known enemy HQ,
        /// else the nearest known enemy building (ghosts count: it has seen them), else the nearest last-seen enemy army.
        /// <paramref name="preferOwner"/>'s buildings first when it names one (an attack moving on through a base).
        /// </summary>
        internal static bool ChooseTarget(AiContext ai, float2 from, int preferOwner, bool hqFirst, out int id, out int owner, out int2 target)
        {
            id = 0;
            owner = 0;
            target = default;
            float best = float.MaxValue;
            for (int pass = 0; pass < 4 && id == 0; pass++)
            {
                bool hqOnly = pass < 2, ownerOnly = pass % 2 == 0;
                if (hqOnly && !hqFirst) continue;
                if (ownerOnly && preferOwner < 0) continue;
                foreach (AiBuilding b in ai.Board.EnemyBuildings.Values)
                {
                    if (ownerOnly && b.OwnerId != preferOwner) continue;
                    if (b.Type == BuildingType.Bomb || (hqOnly && b.Type != BuildingType.Base)) continue;
                    float d = math.distancesq((float2)b.Anchor, from);
                    if (d < best) { best = d; id = b.Id; owner = b.OwnerId; target = b.Anchor; }
                }
            }
            if (id != 0) return true;
            foreach (ArmySighting army in ai.Board.Armies.Values)
            {
                float d = math.distancesq(army.Centre, from);
                if (d < best) { best = d; owner = army.OwnerId; target = (int2)math.floor(army.Centre); }
            }
            return owner != 0;
        }

        private void Fight(AiContext ai, TeamView view, AiState state)
        {
            AiPlayConfig play = ai.Config.Play;
            if (group.Count == 0) { EndAttack(ai, state, false); return; }
            float2 centre = GroupCentre(view, false);

            // The target is gone (seen gone, or the army moved on): the next one, nearest the army.
            bool known = TargetId != 0 ? ai.Board.EnemyBuildings.ContainsKey(TargetId) : ai.Board.Armies.ContainsKey(TargetOwner);
            if (!known || (TargetId == 0 && math.distancesq(centre, Target) < play.ArmyRadius * play.ArmyRadius && EnemiesNear(view, centre, play.ArmyRadius) == 0))
            {
                if (!ChooseTarget(ai, centre, TargetOwner, false, out int id, out int owner, out int2 target)) { EndAttack(ai, state, true); return; }
                TargetId = id;
                TargetOwner = owner;
                Target = target;
                OrderGroup(ai, view, state, true);
            }

            // The fight is at the front (the group strings out between home and the target): the units nearest the target.
            float2 front = FrontCentre(view, state, play.ArmyRadius);
            if (ai.Difficulty.Retreats)
            {
                centre = front;
                float ours = 0f;
                float r2 = play.ArmyRadius * play.ArmyRadius;
                foreach (int i in state.Combat)
                {
                    AiUnit u = view.OwnUnits[i];
                    if (group.Contains(u.Id) && math.distancesq(u.Position, centre) <= r2) ours += StrengthEstimate.Of(ai.Game, u);
                }
                float theirs = StrengthEstimate.EnemyNear(ai.Game, view, centre, play.ArmyRadius, play.TurretStrength);
                if (theirs > 0f && ours < theirs * ai.Difficulty.RetreatRatio)
                {
                    Mode = ArmyMode.Retreat;
                    Retreats++;
                    retreatStarted = ai.Now;
                    lastAttackEnd = ai.Now;
                    GroupIds(view, state, false);
                    ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.Move, state.GatherPoint), AiKeys.Of(AiKeys.Army));
                    return;
                }
            }

            Reinforce(ai, view, state);
            if (ai.Difficulty.FocusFire && FocusTarget(view, front, play.ArmyRadius, out float2 weakest))
            {
                if (Due(ai, AiKeys.Army))
                {
                    GroupIds(view, state, ai.Difficulty.KeepsBacklineBack);
                    if (ids.Count > 0) ai.Propose(AiPriority.Normal + 5f, AiAction.OrderUnits(ids, OrderKind.AttackMove, (int2)math.floor(weakest)), AiKeys.Of(AiKeys.Army));
                }
            }
            else
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
            if (ai.Difficulty.KeepsBacklineBack) Backline(ai, view, state, centre);
            if (ai.Difficulty.DigsPastWalls) DigWalls(ai, view, state, centre);
            Detonate(ai, view, state);
        }

        private void EndAttack(AiContext ai, AiState state, bool sendHome)
        {
            Mode = ArmyMode.Gather;
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

        /// <summary>Orders the whole group at the target (Hard: its Artillery and Snipers to a point behind the front).</summary>
        private void OrderGroup(AiContext ai, TeamView view, AiState state, bool force)
        {
            GroupIds(view, state, ai.Difficulty.KeepsBacklineBack);
            if (ids.Count > 0) ai.Propose(AiPriority.High, AiAction.OrderUnits(ids, OrderKind.AttackMove, Target), AiKeys.Of(AiKeys.Army));
            orderedAt[AiKeys.Army] = ai.Now;
            if (ai.Difficulty.KeepsBacklineBack) Backline(ai, view, state, GroupCentre(view, true), force);
        }

        /// <summary>Hard: Artillery and Snipers attack-move to a point <c>BacklineDistance</c> behind the front, towards home.</summary>
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
                if (group.Contains(u.Id) || state.Scouts.Contains(u.Id) || math.distancesq(u.Position, g) > r2) continue;
                ids.Add(u.Id);
            }
            if (ids.Count < ai.Config.Play.ReinforceSize) return;
            foreach (int id in ids) group.Add(id);
            ai.Propose(AiPriority.Normal, AiAction.OrderUnits(ids, OrderKind.AttackMove, Target), AiKeys.Of(AiKeys.Army, 1));
        }

        /// <summary>Hard: the most damaged enemy unit it sees near its army.</summary>
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

        /// <summary>Hard: Diggers in the group dig out the enemy walls it knows near the army.</summary>
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
            if (!FindThreat(ai, state, out float2 threat))
            {
                if (Mode == ArmyMode.Defend)
                {
                    Mode = ArmyMode.Gather;
                    nextGather = ai.Now; // and back to the gathering point
                }
                return;
            }
            if (math.distance(threat, state.Home) > play.DefendRadius) return;
            float theirs = StrengthEstimate.EnemyNear(ai.Game, view, threat, play.ArmyRadius, play.TurretStrength);
            float home = 0f;
            ids.Clear();
            float d2 = play.DefendRadius * play.DefendRadius;
            foreach (int i in state.Combat)
            {
                AiUnit u = view.OwnUnits[i];
                if (group.Contains(u.Id) || state.Scouts.Contains(u.Id) || math.distancesq(u.Position, state.Home) > d2) continue;
                home += StrengthEstimate.Of(ai.Game, u);
                // Only those not already on their way there (an order is a click: it isn't repeated for nothing).
                if (!u.HasOrder || math.distancesq(u.OrderGoal, threat) > play.ArmyRadius * play.ArmyRadius) ids.Add(u.Id);
            }
            if (Mode == ArmyMode.Attack && home < theirs)
            {
                // Home can't hold: the attack comes back.
                foreach (int id in group) ids.Add(id);
                group.Clear();
                lastAttackEnd = ai.Now;
                Mode = ArmyMode.Defend;
            }
            else if (Mode != ArmyMode.Attack && Mode != ArmyMode.Retreat) Mode = ArmyMode.Defend;
            foreach (int id in ids) holding.Remove(id);
            if (ids.Count > 0 && Due(ai, AiKeys.Defend))
                ai.Propose(AiPriority.High + 5f, AiAction.OrderUnits(ids, OrderKind.AttackMove, (int2)math.floor(threat)), AiKeys.Of(AiKeys.Defend));
        }

        private bool FindThreat(AiContext ai, AiState state, out float2 at)
        {
            at = default;
            double newest = double.NegativeInfinity;
            Blackboard board = ai.Board;
            float recent = ai.Config.Play.OrderSeconds + board.ReactionSeconds;
            foreach (Threat t in board.Threats.Values)
            {
                if (!board.IsActionable(t) || board.Now - t.LastSeen > recent) continue;
                if (t.LastSeen > newest) { newest = t.LastSeen; at = t.Position; }
            }
            if (newest > double.NegativeInfinity) return true;
            board.Alerts.Ready(board.Now, alerts);
            foreach (AiAlert a in alerts)
            {
                if (a.Kind != AlertKind.UnderAttack || board.Now - a.Time > board.ReactionSeconds + recent) continue;
                if (a.Time > newest) { newest = a.Time; at = (float2)a.Tile + 0.5f; }
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
