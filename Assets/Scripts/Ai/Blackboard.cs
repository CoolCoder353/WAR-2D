using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>
    /// Events with the AI clock time they arrived, actionable only after the reaction delay (spec §5.2: Easy reacts late,
    /// Hard quickly) and forgotten after the memory window.
    /// </summary>
    public sealed class ReactionQueue<T> where T : struct
    {
        private struct Item
        {
            public T Value;
            public double At;
        }

        private readonly List<Item> items = new List<Item>();

        public ReactionQueue(float delaySeconds, float memorySeconds)
        {
            Delay = delaySeconds;
            Memory = memorySeconds;
        }

        /// <summary>Seconds before an event may be acted on.</summary>
        public float Delay { get; }

        /// <summary>Seconds an event is kept.</summary>
        public float Memory { get; }

        /// <summary>Events held (actionable or not).</summary>
        public int Count => items.Count;

        /// <summary>Records an event that arrived at <paramref name="at"/>.</summary>
        public void Add(in T value, double at) => items.Add(new Item { Value = value, At = at });

        /// <summary>Fills <paramref name="into"/> with the events old enough to act on; returns how many.</summary>
        public int Ready(double now, List<T> into)
        {
            into.Clear();
            foreach (Item item in items) if (now - item.At >= Delay) into.Add(item.Value);
            return into.Count;
        }

        /// <summary>True when any event may be acted on.</summary>
        public bool AnyReady(double now)
        {
            foreach (Item item in items) if (now - item.At >= Delay) return true;
            return false;
        }

        /// <summary>Forgets events older than the memory window.</summary>
        public void Expire(double now)
        {
            for (int i = items.Count - 1; i >= 0; i--) if (now - items[i].At > Memory) items.RemoveAt(i);
        }

        public void Clear() => items.Clear();
    }

    /// <summary>The last sighting of one enemy's army: how many units, of which types, where and when.</summary>
    public struct ArmySighting
    {
        public int OwnerId;
        public int Count;
        public float2 Centre;
        public double Time;
        /// <summary>Units seen by <see cref="UnitType"/> value (index), at that sighting.</summary>
        public int[] ByType;
    }

    /// <summary>An enemy base the AI knows of: the enemy's HQ when it has seen one (else its first building seen), maybe only as a ghost.</summary>
    public struct EnemyBase
    {
        public int OwnerId;
        public int2 Anchor;
        public bool IsHq;
        /// <summary>Only known as a last-seen ghost (it may be gone).</summary>
        public bool Ghost;
        public double LastSeen;
    }

    /// <summary>Enemy units seen near one of the AI's own buildings.</summary>
    public struct Threat
    {
        public int BuildingId;
        public int2 Near;
        public float2 Position;
        public int Count;
        /// <summary>When it was first seen (it is actionable once the reaction delay passed) and last seen.</summary>
        public double FirstSeen, LastSeen;
    }

    /// <summary>What a plan is for.</summary>
    public enum AiPlanKind : byte
    {
        Opening,
        Expand,
        Defend,
        Attack,
        Scout,
        Retreat,
    }

    /// <summary>Something the AI is in the middle of doing (managers start and finish them).</summary>
    public sealed class AiPlan
    {
        public AiPlanKind Kind;
        public int2 Target;
        public int TargetOwner;
        public double Started;
        public readonly List<int> Units = new List<int>();
    }

    /// <summary>
    /// The AI's memory between thinks (spec §5.3), learned only from its <see cref="TeamView"/>: known enemy bases (from
    /// buildings and ghosts), the last-seen army size and mix per enemy, threats near its own buildings, alerts and newly
    /// seen armies behind the reaction delay, and its active plans.
    /// </summary>
    public sealed class Blackboard
    {
        private readonly float threatRadius, armyMemory;
        private readonly int typeCount = System.Enum.GetValues(typeof(UnitType)).Length + 1;
        private readonly Dictionary<int, int> seenNow = new Dictionary<int, int>();
        private readonly HashSet<int> seenLastThink = new HashSet<int>();
        private readonly List<int> scratch = new List<int>();

        /// <param name="armyMemorySeconds">How long a last-seen army is remembered (its difficulty's share of <c>ArmyMemorySeconds</c>); 0: the brain config's.</param>
        public Blackboard(int owner, float reactionSeconds, global::Config.AiBrainConfig brain, float armyMemorySeconds = 0f)
        {
            Owner = owner;
            ReactionSeconds = reactionSeconds;
            threatRadius = brain.ThreatRadius;
            armyMemory = armyMemorySeconds > 0f ? armyMemorySeconds : brain.ArmyMemorySeconds;
            Alerts = new ReactionQueue<AiAlert>(reactionSeconds, brain.EventMemorySeconds);
            NewArmies = new ReactionQueue<ArmySighting>(reactionSeconds, brain.EventMemorySeconds);
            EventMemorySeconds = brain.EventMemorySeconds;
        }

        public int Owner { get; }

        /// <summary>Seconds before an event may be acted on (its difficulty's <c>ReactionSeconds</c>).</summary>
        public float ReactionSeconds { get; }

        public float EventMemorySeconds { get; }

        /// <summary>The AI clock at the last think.</summary>
        public double Now { get; private set; }

        /// <summary>The view of the last think.</summary>
        public TeamView View { get; private set; }

        /// <summary>Alerts its owner was sent, actionable after the reaction delay.</summary>
        public readonly ReactionQueue<AiAlert> Alerts;

        /// <summary>An enemy's army coming into sight (not seen at the previous think), actionable after the reaction delay.</summary>
        public readonly ReactionQueue<ArmySighting> NewArmies;

        /// <summary>Enemy buildings it knows of (seen now or ghosts), by building id.</summary>
        public readonly Dictionary<int, AiBuilding> EnemyBuildings = new Dictionary<int, AiBuilding>();

        /// <summary>Known enemy bases, by enemy owner.</summary>
        public readonly Dictionary<int, EnemyBase> EnemyBases = new Dictionary<int, EnemyBase>();

        /// <summary>Last-seen army per enemy owner (forgotten after <c>ArmyMemorySeconds</c>).</summary>
        public readonly Dictionary<int, ArmySighting> Armies = new Dictionary<int, ArmySighting>();

        /// <summary>
        /// Where it last saw each enemy's HQ (a building or ghost it saw), kept while the view no longer shows it (a Jammer
        /// can hide a base it has seen): forgotten when its units stand by that spot and see no HQ there, or the owner is
        /// no longer an enemy.
        /// </summary>
        public readonly Dictionary<int, int2> KnownHqs = new Dictionary<int, int2>();

        /// <summary>Threats near its own buildings, by building id.</summary>
        public readonly Dictionary<int, Threat> Threats = new Dictionary<int, Threat>();

        /// <summary>Its active plans.</summary>
        public readonly List<AiPlan> Plans = new List<AiPlan>();

        // ---- AI overhaul §4: what it learns ----

        /// <summary>
        /// The kill ledger (AI overhaul §4.1): the worth of enemy units and buildings it saw die (vanish from a cell it
        /// still sees), and of its own it lost, since the match started.
        /// </summary>
        public float EnemyLost { get; private set; }
        public float OwnLost { get; private set; }

        /// <summary>Its picture of each enemy (AI overhaul §4.2), by owner.</summary>
        public readonly Dictionary<int, EnemyModel> Models = new Dictionary<int, EnemyModel>();

        /// <summary>Targets where an attack lost badly, kept out of favour until a time (AI overhaul §3.4).</summary>
        public readonly List<(int2 At, double Until)> Avoided = new List<(int2, double)>();

        /// <summary>The AI clock when it last met a threat at its base (the home guard's clock), or -infinity.</summary>
        public double LastBaseThreat = double.NegativeInfinity;

        /// <summary>True when <paramref name="at"/> is within <paramref name="radius"/> of a target it avoids now.</summary>
        public bool IsAvoided(int2 at, float radius)
        {
            float r2 = radius * radius;
            foreach ((int2 a, double until) in Avoided)
                if (Now < until && math.distancesq((float2)a, (float2)at) <= r2) return true;
            return false;
        }

        /// <summary>Avoids a target until <paramref name="until"/>.</summary>
        public void Avoid(int2 at, double until)
        {
            for (int i = Avoided.Count - 1; i >= 0; i--) if (Now >= Avoided[i].Until) Avoided.RemoveAt(i);
            Avoided.Add((at, until));
        }

        /// <summary>The model of an enemy (made on first use).</summary>
        public EnemyModel Model(int owner)
        {
            if (!Models.TryGetValue(owner, out EnemyModel m)) Models[owner] = m = new EnemyModel(owner, typeCount, buildingTypeCount);
            return m;
        }

        private readonly int buildingTypeCount = System.Enum.GetValues(typeof(BuildingType)).Length + 1;
        private Dictionary<int, (UnitType Type, float2 At)> lastEnemyUnits = new Dictionary<int, (UnitType, float2)>(), enemyUnitsNow = new Dictionary<int, (UnitType, float2)>();
        private Dictionary<int, (BuildingType Type, int2 At)> lastEnemyBuildings = new Dictionary<int, (BuildingType, int2)>(), enemyBuildingsNow = new Dictionary<int, (BuildingType, int2)>();
        private Dictionary<int, UnitType> lastOwnUnits = new Dictionary<int, UnitType>(), ownUnitsNow = new Dictionary<int, UnitType>();
        private Dictionary<int, BuildingType> lastOwnBuildings = new Dictionary<int, BuildingType>(), ownBuildingsNow = new Dictionary<int, BuildingType>();
        private readonly HashSet<int> buildingIdsNow = new HashSet<int>();
        private double lastModelUpdate = double.NaN;

        /// <summary>
        /// Learns this think's kills and losses and updates the enemy models (AI overhaul §4.1, §4.2). Call once per think
        /// after <see cref="Observe"/>, with the seconds of play so far.
        /// </summary>
        public void UpdateModels(TeamView view, double playing, global::Config.AiPolicyConfig policy, global::Config.GameConfigData game)
        {
            // ---- the kill ledger: what vanished since the last think ----
            enemyUnitsNow.Clear();
            foreach (AiUnit u in view.OtherUnits) if (view.IsEnemy(u.OwnerId)) enemyUnitsNow[u.Id] = (u.Type, u.Position);
            // A unit that vanished counts as dead only when everywhere it could have walked since is in sight (else it left).
            float since = double.IsNaN(lastModelUpdate) ? 0f : (float)System.Math.Max(0.0, Now - lastModelUpdate);
            foreach (KeyValuePair<int, (UnitType Type, float2 At)> e in lastEnemyUnits)
            {
                if (enemyUnitsNow.ContainsKey(e.Key)) continue;
                float speed = game.Units.TryGetValue(e.Value.Type, out global::Config.UnitConfig uc) ? uc.MoveSpeed : 0f;
                if (SeenAround(view, e.Value.At, speed * since + 1f)) EnemyLost += UnitCost(game, e.Value.Type);
            }
            buildingIdsNow.Clear();
            enemyBuildingsNow.Clear();
            ownBuildingsNow.Clear();
            foreach (AiBuilding b in view.Buildings)
            {
                buildingIdsNow.Add(b.Id);
                if (b.OwnerId == Owner) { if (!b.Ghost && b.Progress >= 100) ownBuildingsNow[b.Id] = b.Type; }
                else if (!b.Ghost && view.IsEnemy(b.OwnerId)) enemyBuildingsNow[b.Id] = (b.Type, b.Anchor);
            }
            foreach (KeyValuePair<int, (BuildingType Type, int2 At)> e in lastEnemyBuildings)
                if (!buildingIdsNow.Contains(e.Key) && view.FogAt((float2)e.Value.At + 0.5f) == 2) EnemyLost += BuildingCost(game, e.Value.Type);
            ownUnitsNow.Clear();
            foreach (AiUnit u in view.OwnUnits) ownUnitsNow[u.Id] = u.Type;
            foreach (KeyValuePair<int, UnitType> e in lastOwnUnits) if (!ownUnitsNow.ContainsKey(e.Key)) OwnLost += UnitCost(game, e.Value);
            foreach (KeyValuePair<int, BuildingType> e in lastOwnBuildings) if (!ownBuildingsNow.ContainsKey(e.Key)) OwnLost += BuildingCost(game, e.Value);
            (lastEnemyUnits, enemyUnitsNow) = (enemyUnitsNow, lastEnemyUnits);
            (lastEnemyBuildings, enemyBuildingsNow) = (enemyBuildingsNow, lastEnemyBuildings);
            (lastOwnUnits, ownUnitsNow) = (ownUnitsNow, lastOwnUnits);
            (lastOwnBuildings, ownBuildingsNow) = (ownBuildingsNow, lastOwnBuildings);

            // ---- the enemy models ----
            bool inPlay = view.State == GameState.Playing;
            float decay = double.IsNaN(lastModelUpdate) ? 1f : (float)System.Math.Pow(0.5, System.Math.Max(0.0, Now - lastModelUpdate) / System.Math.Max(1f, policy.ModelHalfLife));
            lastModelUpdate = Now;
            foreach (EnemyModel m in Models.Values)
            {
                for (int t = 0; t < m.Peak.Length; t++) m.Peak[t] *= decay;
                System.Array.Clear(m.SeenNow, 0, m.SeenNow.Length);
                System.Array.Clear(m.Buildings, 0, m.Buildings.Length);
            }
            foreach (AiUnit u in view.OtherUnits)
            {
                if (!view.IsEnemy(u.OwnerId)) continue;
                EnemyModel m = Model(u.OwnerId);
                int t = (int)u.Type;
                if ((uint)t < (uint)m.SeenNow.Length) m.SeenNow[t]++;
                if (u.Type != UnitType.Builder && double.IsNaN(m.FirstCombatSeen) && inPlay) m.FirstCombatSeen = playing;
            }
            foreach (EnemyModel m in Models.Values)
            {
                int combat = 0;
                for (int t = 0; t < m.Peak.Length; t++)
                {
                    if (m.SeenNow[t] > m.Peak[t]) m.Peak[t] = m.SeenNow[t];
                    if ((UnitType)t != UnitType.Builder) combat += m.SeenNow[t];
                }
                if (inPlay && playing < policy.RushSeconds && combat > m.EarlyArmy) m.EarlyArmy = combat;
            }
            foreach (AiBuilding b in EnemyBuildings.Values)
            {
                EnemyModel m = Model(b.OwnerId);
                int t = (int)b.Type;
                if ((uint)t >= (uint)m.Buildings.Length) continue;
                m.Buildings[t]++;
                if (double.IsNaN(m.FirstBuildingSeen[t]) && inPlay) m.FirstBuildingSeen[t] = playing;
                if (double.IsNaN(m.FirstExpansionSeen) && inPlay && AtExpansion(view, b.Anchor)) m.FirstExpansionSeen = playing;
            }
            foreach (KeyValuePair<int, AiWall> w in view.Walls)
                if (w.Value.OwnerId != Owner && view.IsEnemy(w.Value.OwnerId)) Model(w.Value.OwnerId).Walls = true;
            foreach (EnemyModel m in Models.Values) m.UpdateGuess(policy);
        }

        /// <summary>True when a point and the four points <paramref name="reach"/> away from it are all in sight now.</summary>
        private static bool SeenAround(TeamView view, float2 at, float reach) =>
            view.FogAt(at) == 2 && view.FogAt(at + new float2(reach, 0f)) == 2 && view.FogAt(at - new float2(reach, 0f)) == 2
            && view.FogAt(at + new float2(0f, reach)) == 2 && view.FogAt(at - new float2(0f, reach)) == 2;

        private static bool AtExpansion(TeamView view, int2 at)
        {
            foreach (AiRegion r in view.Regions)
                if (r.Kind == AiRegionKind.Expansion && math.distancesq((float2)r.Centre, (float2)at) <= (r.Radius + 4f) * (r.Radius + 4f)) return true;
            return false;
        }

        private static float UnitCost(global::Config.GameConfigData game, UnitType t) => game.Units.TryGetValue(t, out global::Config.UnitConfig c) ? c.UpfrontCost : 0f;
        private static float BuildingCost(global::Config.GameConfigData game, BuildingType t) => game.Buildings.TryGetValue(t, out global::Config.BuildingConfig c) ? c.UpfrontCost : 0f;

        /// <summary>True when the threat has been known long enough to act on.</summary>
        public bool IsActionable(in Threat threat) => Now - threat.FirstSeen >= ReactionSeconds;

        /// <summary>Learns this think's view.</summary>
        public void Observe(TeamView view)
        {
            View = view;
            Now = view.Time;
            foreach (AiAlert alert in view.Alerts) Alerts.Add(alert, alert.Time);
            Alerts.Expire(Now);
            NewArmies.Expire(Now);
            ObserveArmies(view);
            ObserveBuildings(view);
            ObserveThreats(view);
        }

        private void ObserveArmies(TeamView view)
        {
            seenNow.Clear();
            foreach (AiUnit unit in view.OtherUnits)
                if (view.IsEnemy(unit.OwnerId)) { seenNow.TryGetValue(unit.OwnerId, out int n); seenNow[unit.OwnerId] = n + 1; }
            foreach (KeyValuePair<int, int> seen in seenNow)
            {
                int enemy = seen.Key;
                Armies.TryGetValue(enemy, out ArmySighting sighting);
                if (sighting.ByType == null) sighting.ByType = new int[typeCount];
                else System.Array.Clear(sighting.ByType, 0, sighting.ByType.Length);
                float2 sum = 0;
                foreach (AiUnit unit in view.OtherUnits)
                {
                    if (unit.OwnerId != enemy) continue;
                    sum += unit.Position;
                    int type = (int)unit.Type;
                    if ((uint)type < (uint)sighting.ByType.Length) sighting.ByType[type]++;
                }
                sighting.OwnerId = enemy;
                sighting.Count = seen.Value;
                sighting.Centre = sum / seen.Value;
                sighting.Time = Now;
                Armies[enemy] = sighting;
                if (!seenLastThink.Contains(enemy)) NewArmies.Add(sighting, Now);
            }
            seenLastThink.Clear();
            foreach (int enemy in seenNow.Keys) seenLastThink.Add(enemy);
            scratch.Clear();
            foreach (KeyValuePair<int, ArmySighting> army in Armies) if (Now - army.Value.Time > armyMemory) scratch.Add(army.Key);
            foreach (int enemy in scratch) Armies.Remove(enemy);
        }

        private void ObserveBuildings(TeamView view)
        {
            EnemyBuildings.Clear();
            EnemyBases.Clear();
            ForgetHqs(view);
            foreach (AiBuilding b in view.Buildings)
            {
                if (b.OwnerId == Owner || !view.IsEnemy(b.OwnerId)) continue;
                EnemyBuildings[b.Id] = b;
                bool hq = b.Type == BuildingType.Base;
                if (hq) KnownHqs[b.OwnerId] = b.Anchor;
                if (EnemyBases.TryGetValue(b.OwnerId, out EnemyBase known) && (known.IsHq || !hq)) continue;
                EnemyBases[b.OwnerId] = new EnemyBase { OwnerId = b.OwnerId, Anchor = b.Anchor, IsHq = hq, Ghost = b.Ghost, LastSeen = Now };
            }
        }

        private readonly List<int> forget = new List<int>(4);

        /// <summary>Drops remembered HQs of owners no longer enemies, and those its own units stand by and see no HQ at.</summary>
        private void ForgetHqs(TeamView view)
        {
            if (KnownHqs.Count == 0) return;
            forget.Clear();
            foreach (KeyValuePair<int, int2> hq in KnownHqs)
            {
                if (!view.IsEnemy(hq.Key)) { forget.Add(hq.Key); continue; }
                float2 centre = (float2)hq.Value + 2.5f;
                bool near = false;
                foreach (AiUnit u in view.OwnUnits) if (math.distancesq(u.Position, centre) < 9f) { near = true; break; }
                if (!near) continue;
                bool there = false;
                foreach (AiBuilding b in view.Buildings) if (b.OwnerId == hq.Key && b.Type == BuildingType.Base && b.Anchor.Equals(hq.Value)) { there = true; break; }
                if (!there) forget.Add(hq.Key);
            }
            foreach (int owner in forget) KnownHqs.Remove(owner);
        }

        // Enemy units seen now, bucketed by threat-radius cells (sorted by cell), so each own building looks only at
        // the 3x3 cells around it: a big army in sight never costs buildings x units per think.
        private long[] cellKeys = new long[64];
        private float2[] cellPositions = new float2[64];

        private long CellKey(float2 p)
        {
            int2 c = (int2)math.floor(p / math.max(1f, threatRadius));
            return ((long)c.x << 32) | (uint)c.y;
        }

        private void ObserveThreats(TeamView view)
        {
            float r2 = threatRadius * threatRadius;
            int n = 0;
            foreach (AiUnit unit in view.OtherUnits)
            {
                if (!view.IsEnemy(unit.OwnerId)) continue;
                if (n == cellKeys.Length)
                {
                    System.Array.Resize(ref cellKeys, n * 2);
                    System.Array.Resize(ref cellPositions, n * 2);
                }
                cellKeys[n] = CellKey(unit.Position);
                cellPositions[n] = unit.Position;
                n++;
            }
            if (n > 1) System.Array.Sort(cellKeys, cellPositions, 0, n);
            foreach (AiBuilding own in view.Buildings)
            {
                if (n == 0) break;
                if (own.OwnerId != Owner) continue;
                float2 at = own.Anchor;
                int count = 0;
                float2 sum = 0;
                int2 c = (int2)math.floor(at / math.max(1f, threatRadius));
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        long key = ((long)(c.x + dx) << 32) | (uint)(c.y + dy);
                        int i = System.Array.BinarySearch(cellKeys, 0, n, key);
                        if (i < 0) continue;
                        while (i > 0 && cellKeys[i - 1] == key) i--;
                        for (; i < n && cellKeys[i] == key; i++)
                        {
                            if (math.distancesq(cellPositions[i], at) > r2) continue;
                            count++;
                            sum += cellPositions[i];
                        }
                    }
                if (count == 0) continue;
                Threats.TryGetValue(own.Id, out Threat threat);
                if (threat.Count == 0) threat.FirstSeen = Now;
                threat.BuildingId = own.Id;
                threat.Near = own.Anchor;
                threat.Position = sum / count;
                threat.Count = count;
                threat.LastSeen = Now;
                Threats[own.Id] = threat;
            }
            scratch.Clear();
            foreach (KeyValuePair<int, Threat> threat in Threats) if (Now - threat.Value.LastSeen > EventMemorySeconds) scratch.Add(threat.Key);
            foreach (int id in scratch) Threats.Remove(id);
        }
    }
}
