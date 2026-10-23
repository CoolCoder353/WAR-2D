using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using WAR2D.AiView;

namespace WAR2D.Ai
{
    /// <summary>A building a manager wants (plan Task 7): the Builder manager finds it a spot and places it.</summary>
    public struct BuildRequest
    {
        public BuildingType Type;
        /// <summary>Where it should go (the search starts here) and how far from there it may go.</summary>
        public int2 Near;
        public float Radius;
        public float Priority;
        /// <summary>Names the request (stable across thinks): its placement search goes on under this key.</summary>
        public int Key;
        /// <summary>Free tiles kept around it (paths between buildings).</summary>
        public int Gap;
        /// <summary>True when it saves up for it: while it can't afford it, nothing requested after it is bought.</summary>
        public bool Save;
    }

    /// <summary>A unit a manager wants made (plan Task 7): the Production manager queues it on a producer that makes it.</summary>
    public struct UnitRequest
    {
        public UnitType Type;
        public float Priority;
    }

    /// <summary>A building the AI placed that its view doesn't show yet: its footprint stays reserved and it counts as built.</summary>
    public struct PendingBuilding
    {
        public BuildingType Type;
        public int2 Anchor;
        public double Until;
    }

    /// <summary>Stable proposal and request keys (never 0).</summary>
    public static class AiKeys
    {
        public static int Of(int kind, int a = 0, int b = 0)
        {
            unchecked
            {
                int h = (kind + 1) * 486187739;
                h = (h ^ a) * 16777619;
                h = (h ^ b) * 16777619;
                return h == 0 ? 1 : h;
            }
        }

        public const int Hq = 1, Place = 2, Build = 3, Repair = 4, Produce = 5, Queue = 6, Rally = 7, Army = 8, Scout = 9,
            Expand = 10, Cancel = 11, Wall = 12, Dig = 13, Detonate = 14, Backline = 15, Hold = 16, Defend = 17,
            Attack = 18, Share = 19, Gift = 20, Surrender = 21;
    }

    /// <summary>
    /// What the managers share within a think (plan Task 7), all derived from the <see cref="TeamView"/> and the AI's own
    /// earlier choices: its HQ, counts of its units and buildings, its economy (income and upkeep worked out from what it
    /// owns and the game's public numbers), the money the managers have claimed this think, the buildings it placed that
    /// its view doesn't show yet, the building and unit requests, the army's gathering point and the placement search.
    /// </summary>
    public sealed class AiState
    {
        private readonly AiContext ai;
        private static readonly int UnitTypes = System.Enum.GetValues(typeof(UnitType)).Length + 1;
        private static readonly int BuildingTypes = System.Enum.GetValues(typeof(BuildingType)).Length + 1;

        internal AiState(AiContext ai)
        {
            this.ai = ai;
            Placement = new Placement(this);
            UnitCount = new int[UnitTypes];
            QueuedCount = new int[UnitTypes];
            BuildingCount = new int[BuildingTypes];
            CompleteCount = new int[BuildingTypes];
        }

        public AiContext Context => ai;
        public TeamView View => ai.View;
        public GameConfigData Game => ai.Game;
        public AiPlayConfig Play => ai.Config.Play;

        // ---- its base ----
        /// <summary>True once its HQ is in its view.</summary>
        public bool HasHq;
        public AiBuilding Hq;
        /// <summary>The HQ's centre (world), or the map's centre before it has one.</summary>
        public float2 Home;
        /// <summary>True from its HQ placement until its view shows the HQ (it never places two).</summary>
        public bool HqPending => pendingHq;
        private bool pendingHq;

        /// <summary>The AI clock when the match started playing (NaN before), and seconds of play since.</summary>
        public double PlayingSince = double.NaN;
        public double PlayingSeconds;

        /// <summary>True once the style's build order is done or dropped (producers, defences and the army start then).</summary>
        public bool OpeningDone;

        // ---- counts this think ----
        /// <summary>Own living units by <see cref="UnitType"/> value.</summary>
        public readonly int[] UnitCount;
        /// <summary>Units queued on its producers, by type.</summary>
        public readonly int[] QueuedCount;
        /// <summary>Own buildings by <see cref="BuildingType"/> value, blueprints and pending placements included.</summary>
        public readonly int[] BuildingCount;
        /// <summary>Own finished buildings by type.</summary>
        public readonly int[] CompleteCount;
        /// <summary>Indices into <see cref="TeamView.Buildings"/>: its finished producers, its blueprints.</summary>
        public readonly List<int> Producers = new List<int>(16), Blueprints = new List<int>(16);
        /// <summary>Indices into <see cref="TeamView.OwnUnits"/>: its Builders, and those with nothing to do.</summary>
        public readonly List<int> Builders = new List<int>(16), IdleBuilders = new List<int>(16);
        /// <summary>Indices into <see cref="TeamView.OwnUnits"/>: every unit that fights (not Builders).</summary>
        public readonly List<int> Combat = new List<int>(256);

        // ---- economy ----
        /// <summary>Income and upkeep per second, worked out from what it owns (its own records and the public numbers).</summary>
        public float Income, Upkeep;
        /// <summary>Money the managers may still claim this think (its balance less what it keeps back and what is claimed).</summary>
        public float Free;
        /// <summary>Upkeep the managers added this think.</summary>
        public float UpkeepAdded;
        /// <summary>True while its upkeep runs ahead of its income and its balance is short (it stops adding upkeep).</summary>
        public bool UpkeepTight;

        // ---- shared plans ----
        public readonly List<BuildRequest> BuildRequests = new List<BuildRequest>(16);
        public readonly List<UnitRequest> UnitRequests = new List<UnitRequest>(8);
        public readonly List<PendingBuilding> Pending = new List<PendingBuilding>(16);
        /// <summary>Anchors its placements were refused at, kept out of the search for a while (it learns from a refused click).</summary>
        public readonly Dictionary<long, double> Blocked = new Dictionary<long, double>();
        /// <summary>Where the army gathers and producers rally (set by the Army manager).</summary>
        public int2 GatherPoint;
        public bool HasGatherPoint;
        /// <summary>Chokepoint (or front) spots it defends, and the direction out of its base at each (set by the Builder manager).</summary>
        public readonly List<float2> DefenceSpots = new List<float2>(4), DefenceDirs = new List<float2>(4);
        /// <summary>Unit ids the Scouting manager uses (the army leaves them alone).</summary>
        public readonly HashSet<int> Scouts = new HashSet<int>();

        /// <summary>The placement search over what it knows of the map.</summary>
        public readonly Placement Placement;

        private readonly List<long> expired = new List<long>();

        /// <summary>Learns this think's view (call once per think, before the managers).</summary>
        public void Update(TeamView view)
        {
            double now = view.Time;
            if (view.State == GameState.Playing && double.IsNaN(PlayingSince)) PlayingSince = now;
            PlayingSeconds = double.IsNaN(PlayingSince) ? 0 : now - PlayingSince;

            System.Array.Clear(UnitCount, 0, UnitCount.Length);
            System.Array.Clear(QueuedCount, 0, QueuedCount.Length);
            System.Array.Clear(BuildingCount, 0, BuildingCount.Length);
            System.Array.Clear(CompleteCount, 0, CompleteCount.Length);
            Producers.Clear();
            Blueprints.Clear();
            Builders.Clear();
            IdleBuilders.Clear();
            Combat.Clear();
            BuildRequests.Clear();
            UnitRequests.Clear();

            HasHq = false;
            float upkeep = 0f, income = 0f;
            int owner = ai.Owner;
            for (int i = 0; i < view.Buildings.Count; i++)
            {
                AiBuilding b = view.Buildings[i];
                if (b.OwnerId != owner || b.Ghost) continue;
                BuildingCount[(int)b.Type]++;
                BuildingConfig config = BuildingOf(b.Type);
                if (config != null) upkeep += config.RunningCost;
                if (b.Progress >= 100)
                {
                    CompleteCount[(int)b.Type]++;
                    if (SpawnerRules.IsProducer(b.Type))
                    {
                        Producers.Add(i);
                        if ((uint)b.Producing < (uint)QueuedCount.Length) QueuedCount[(int)b.Producing] += b.Queued;
                        // Queued units are upkeep it has already chosen (they are charged as they spawn).
                        UnitConfig queued = UnitOf(b.Producing);
                        if (queued != null) upkeep += queued.RunningCost * b.Queued;
                    }
                    if (b.Type == BuildingType.Miner && view.TileAt(b.Anchor + MinerRules.FacingOffset(b.Rotation * 90f)) == TileType.Gem)
                        income += Game.Resources.MiningRate;
                }
                else Blueprints.Add(i);
                if (b.Type == BuildingType.Base)
                {
                    HasHq = true;
                    Hq = b;
                }
            }
            if (HasHq) pendingHq = false;
            Home = HasHq ? Footprint.VisualCenter(Hq.Anchor, SizeOf(BuildingType.Base)) : new float2(view.Width, view.Height) * 0.5f;

            for (int i = 0; i < view.OwnUnits.Count; i++)
            {
                AiUnit u = view.OwnUnits[i];
                if ((uint)u.Type < (uint)UnitCount.Length) UnitCount[(int)u.Type]++;
                UnitConfig config = UnitOf(u.Type);
                if (config != null) upkeep += config.RunningCost;
                if (u.Type == UnitType.Builder)
                {
                    Builders.Add(i);
                    if (!u.HasOrder && u.Work == 0) IdleBuilders.Add(i);
                }
                else Combat.Add(i);
            }

            // Placements its view doesn't show yet count as built (and reserve their ground) until they appear or expire.
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingBuilding p = Pending[i];
                bool shown = false;
                for (int k = 0; k < view.Buildings.Count && !shown; k++)
                {
                    AiBuilding b = view.Buildings[k];
                    shown = b.OwnerId == owner && b.Type == p.Type && b.Anchor.Equals(p.Anchor);
                }
                if (shown) { Pending.RemoveAt(i); continue; }
                if (now > p.Until)
                {
                    // Never showed up: the ground was not free after all (something it couldn't see). Keep off it for a while.
                    Block(p.Anchor, now);
                    Pending.RemoveAt(i);
                    if (p.Type == BuildingType.Base) pendingHq = false;
                    continue;
                }
                BuildingCount[(int)p.Type]++;
                BuildingConfig config = BuildingOf(p.Type);
                if (config != null) upkeep += config.RunningCost;
            }

            expired.Clear();
            foreach (KeyValuePair<long, double> b in Blocked) if (now > b.Value) expired.Add(b.Key);
            foreach (long key in expired) Blocked.Remove(key);

            if (view.State == GameState.Playing) income += Game.Resources.PassiveGenerationRate;
            Income = income;
            Upkeep = upkeep;
            UpkeepAdded = 0f;
            AiDifficultyConfig d = ai.Difficulty;
            float shortfall = math.max(0f, Upkeep - Income * Play.UpkeepIncomeShare);
            // Normal and Hard keep a few seconds of upkeep in hand at all times (upkeep is charged entity by entity through
            // the second, so an empty balance leaves some unpaid), more while their upkeep runs ahead of their income.
            float reserve = d.KeepsUpkeepPaid ? math.max(d.UpkeepReserveSeconds * shortfall, Play.UpkeepFloorSeconds * Upkeep) : 0f;
            Free = view.Resources - reserve;
            UpkeepTight = d.KeepsUpkeepPaid && Upkeep > Income && view.Resources < (Upkeep - Income) * math.max(1f, d.UpkeepReserveSeconds);

            Placement.Refresh(view);
        }

        // ---- money ----

        /// <summary>True when it may add this much upkeep: Easy always; Normal and Hard only while their income covers it, or their balance covers the deficit for <c>UpkeepBankSeconds</c>.</summary>
        public bool MayAddUpkeep(float upkeep)
        {
            if (!ai.Difficulty.KeepsUpkeepPaid || upkeep <= 0f) return true;
            if (UpkeepTight) return false;
            float after = Upkeep + UpkeepAdded + upkeep;
            float shortfall = after - Income * Play.UpkeepIncomeShare;
            return shortfall <= 0f || Free >= shortfall * Play.UpkeepBankSeconds;
        }

        /// <summary>Claims money and upkeep for something it is about to buy; false (nothing claimed) when it can't.</summary>
        public bool TrySpend(float cost, float upkeep)
        {
            if (Free < cost || !MayAddUpkeep(upkeep)) return false;
            Free -= cost;
            UpkeepAdded += upkeep;
            return true;
        }

        /// <summary>Holds money back for something it saves up for (nothing cheaper is bought before it).</summary>
        public void Reserve(float cost) => Free -= cost;

        /// <summary>Gives back money claimed this think.</summary>
        public void Release(float cost, float upkeep)
        {
            Free += cost;
            UpkeepAdded -= upkeep;
        }

        // ---- requests ----

        /// <summary>Asks the Builder manager for a building near a tile (it searches, pays and places it this think or a later one).</summary>
        public void RequestBuilding(BuildingType type, int2 near, float radius, float priority, int key, int gap = 0, bool save = false) =>
            BuildRequests.Add(new BuildRequest { Type = type, Near = near, Radius = radius, Priority = priority, Key = key, Gap = gap, Save = save });

        /// <summary>
        /// Its own buildings of a type within a square of half-side <paramref name="radius"/> around a point (blueprints and
        /// pending placements included): the same square a placement search with that radius covers.
        /// </summary>
        public int CountNear(BuildingType type, float2 at, float radius)
        {
            int n = 0;
            foreach (AiBuilding b in View.Buildings)
                if (b.OwnerId == ai.Owner && !b.Ghost && b.Type == type && math.cmax(math.abs((float2)b.Anchor - at)) <= radius) n++;
            foreach (PendingBuilding p in Pending)
                if (p.Type == type && math.cmax(math.abs((float2)p.Anchor - at)) <= radius) n++;
            return n;
        }

        public void RequestUnit(UnitType type, float priority)
        {
            for (int i = 0; i < UnitRequests.Count; i++) if (UnitRequests[i].Type == type) return;
            UnitRequests.Add(new UnitRequest { Type = type, Priority = priority });
        }

        /// <summary>Remembers a placement it proposed: its ground is reserved and it counts as built until its view shows it.</summary>
        public void AddPending(BuildingType type, int2 anchor)
        {
            Pending.Add(new PendingBuilding { Type = type, Anchor = anchor, Until = ai.Now + math.max(Play.PendingSeconds, ai.Config.Brain.ProposalWaitSeconds) });
            BuildingCount[(int)type]++;
            if (type == BuildingType.Base) pendingHq = true;
            Placement.Reserve(type, anchor);
        }

        /// <summary>A refused click: a placement it proposed was refused, so that ground is kept out of its search for a while.</summary>
        internal void OnExecuted(in AiAction action, bool accepted)
        {
            if (accepted) return;
            if (action.Kind != AiActionKind.PlaceBuilding && action.Kind != AiActionKind.PlaceHq) return;
            for (int i = Pending.Count - 1; i >= 0; i--)
                if (Pending[i].Anchor.Equals(action.Tile))
                {
                    if (Pending[i].Type == BuildingType.Base) pendingHq = false;
                    Pending.RemoveAt(i);
                }
            Block(action.Tile, ai.Now);
        }

        private void Block(int2 anchor, double now) => Blocked[Pack(anchor)] = now + Play.BlockedSeconds;

        public bool IsBlocked(int2 anchor) => Blocked.Count > 0 && Blocked.ContainsKey(Pack(anchor));

        private static long Pack(int2 t) => ((long)t.x << 32) | (uint)t.y;

        // ---- public numbers ----

        public BuildingConfig BuildingOf(BuildingType type) => Game.Buildings.TryGetValue(type, out BuildingConfig c) ? c : null;
        public UnitConfig UnitOf(UnitType type) => Game.Units.TryGetValue(type, out UnitConfig c) ? c : null;
        public int2 SizeOf(BuildingType type) => BuildingOf(type)?.Size ?? new int2(1, 1);
        public float CostOf(BuildingType type) => BuildingOf(type)?.UpfrontCost ?? 0f;
        public float UpkeepOf(BuildingType type) => BuildingOf(type)?.RunningCost ?? 0f;
        public float CostOf(UnitType type) => UnitOf(type)?.UpfrontCost ?? 0f;
        public float UpkeepOf(UnitType type) => UnitOf(type)?.RunningCost ?? 0f;

        /// <summary>A building's centre in world units.</summary>
        public float2 CentreOf(in AiBuilding b) => Footprint.VisualCenter(b.Anchor, SizeOf(b.Type));

        /// <summary>The nearest ground tile it knows within <paramref name="radius"/> of a point, not under a building (or the point itself).</summary>
        public int2 GroundNear(float2 point, int radius)
        {
            int2 c = (int2)math.floor(point);
            for (int r = 0; r <= radius; r++)
                for (int i = 0, n = r == 0 ? 1 : 8 * r; i < n; i++)
                {
                    int2 t = c + Placement.RingOffset(r, i);
                    if (View.TileAt(t) == TileType.Ground && !Placement.IsUsed(t)) return t;
                }
            return math.clamp(c, 0, new int2(math.max(0, View.Width - 1), math.max(0, View.Height - 1)));
        }

        /// <summary>The region of the map's plan that holds a point (the nearest whose radius covers it), or -1.</summary>
        public int RegionAt(float2 point, float slack = 4f)
        {
            int best = -1;
            float bestD = float.MaxValue;
            AiRegion[] regions = View.Regions;
            for (int i = 0; i < regions.Length; i++)
            {
                float d = math.distance(point, regions[i].Centre);
                if (d <= regions[i].Radius + slack && d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }
}
