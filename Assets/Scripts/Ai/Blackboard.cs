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

        public Blackboard(int owner, float reactionSeconds, global::Config.AiBrainConfig brain)
        {
            Owner = owner;
            ReactionSeconds = reactionSeconds;
            threatRadius = brain.ThreatRadius;
            armyMemory = brain.ArmyMemorySeconds;
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

        /// <summary>Threats near its own buildings, by building id.</summary>
        public readonly Dictionary<int, Threat> Threats = new Dictionary<int, Threat>();

        /// <summary>Its active plans.</summary>
        public readonly List<AiPlan> Plans = new List<AiPlan>();

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
            foreach (AiBuilding b in view.Buildings)
            {
                if (b.OwnerId == Owner || !view.IsEnemy(b.OwnerId)) continue;
                EnemyBuildings[b.Id] = b;
                bool hq = b.Type == BuildingType.Base;
                if (EnemyBases.TryGetValue(b.OwnerId, out EnemyBase known) && (known.IsHq || !hq)) continue;
                EnemyBases[b.OwnerId] = new EnemyBase { OwnerId = b.OwnerId, Anchor = b.Anchor, IsHq = hq, Ghost = b.Ghost, LastSeen = Now };
            }
        }

        private void ObserveThreats(TeamView view)
        {
            float r2 = threatRadius * threatRadius;
            foreach (AiBuilding own in view.Buildings)
            {
                if (own.OwnerId != Owner) continue;
                float2 at = own.Anchor;
                int count = 0;
                float2 sum = 0;
                foreach (AiUnit unit in view.OtherUnits)
                {
                    if (!view.IsEnemy(unit.OwnerId) || math.distancesq(unit.Position, at) > r2) continue;
                    count++;
                    sum += unit.Position;
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
