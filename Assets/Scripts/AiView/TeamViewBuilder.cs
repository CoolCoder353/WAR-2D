using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Net.Replication;
using WAR2D.Sim;

namespace WAR2D.AiView
{
    /// <summary>
    /// The facts a team view takes from the match rather than the simulation (in a match, <see cref="GameCoreFacts"/>;
    /// tests pass their own): the state, the player order, and the owner's own records as its client is sent them.
    /// </summary>
    public interface ITeamViewFacts
    {
        /// <summary>The match state (<c>GameCore.CurrentState</c>).</summary>
        GameState State { get; }

        /// <summary>The player order: bit i of a diplomacy row is the owner at index i.</summary>
        IReadOnlyList<int> PlayerOrder { get; }

        /// <summary>
        /// Fills what the owner's client is told outside replication: its starting team, the match settings and the public
        /// player list, its own resources, income and upkeep, and its own and its teammates' start options.
        /// </summary>
        void Fill(int owner, TeamView view);
    }

    /// <summary>
    /// Builds each AI player's <see cref="TeamView"/> (v0.14, spec §4) from the settled world, with the predicates
    /// replication uses for a client of the same owner (<see cref="InterestRules"/>, <see cref="BuildingInterest"/>,
    /// <see cref="TerrainInterest"/>, <see cref="BombRules"/>, <see cref="VisibilityRules"/>, <c>Diplomacy.RowFor</c>).
    /// Between thinks it collects, per tracked owner, the alerts, attack events and blasts that owner's client would be
    /// sent; at a think it copies the rest into the owner's reused buffers. Server only, main thread, on the settled world
    /// (a <see cref="SimContext.Settled"/> handler, or while the tick doesn't run).
    /// </summary>
    public sealed class TeamViewBuilder : IDisposable
    {
        /// <summary>Most events of each kind kept per owner between two thinks; the rest are counted as dropped.</summary>
        public const int MaxEventsPerThink = 512;

        /// <summary>Answers what a tile index currently is (as replication's); null reads the plain kind.</summary>
        public Func<int, TerrainCell> TerrainCellOf;

        /// <summary>Answers whether owner <c>from</c> shares vision with owner <c>to</c> (bombs, as replication's); null: nobody does.</summary>
        public Func<int, int, bool> SharesVision;

        /// <summary>Fills the list with every live building (as replication's); null: no buildings.</summary>
        public Action<List<BuildingView>> CollectBuildings;

        /// <summary>The AI clock now (seconds), stamped on events; null uses the tick time.</summary>
        public Func<double> Clock;

        private sealed class Seat
        {
            public int Owner;
            public readonly TeamView View = new TeamView();
            public readonly BuildingInterest Buildings = new BuildingInterest();
            public readonly TerrainInterest Terrain = new TerrainInterest();
            /// <summary>Known buildings: own, seen and ghosts (what a client's ClientBuildings holds).</summary>
            public readonly Dictionary<int, AiBuilding> Known = new Dictionary<int, AiBuilding>();
            public readonly ViewList<AiAlert> Alerts = new ViewList<AiAlert>(16);
            public readonly ViewList<AiAttack> Attacks = new ViewList<AiAttack>(64);
            public readonly ViewList<AiBlast> Blasts = new ViewList<AiBlast>(16);
            public int Dropped;
            // Read by the cached predicates below (no closure per build).
            public SimData Data;
            public int Grid = -1;
            public Func<float2, bool> Sees;
            public Func<int, bool> MayKnowHidden;
        }

        private readonly SimContext sim;
        private readonly ITeamViewFacts facts;
        private readonly Dictionary<int, Seat> seats = new Dictionary<int, Seat>();
        private readonly List<Seat> seatList = new List<Seat>();
        private readonly List<BuildingView> buildingViews = new List<BuildingView>();
        private readonly HashSet<int> buildingIds = new HashSet<int>();
        private readonly List<TerrainCell> terrainNow = new List<TerrainCell>();
        private readonly List<TerrainCell> terrainScratch = new List<TerrainCell>();
        private readonly List<int> slotByIndex = new List<int>(16);
        private readonly List<float2> pendingDeaths = new List<float2>();
        private NativeList<AiUnit> ownUnits, otherUnits;
        private NativeArray<byte> fog;
        private bool disposed;

        public TeamViewBuilder(SimContext sim, ITeamViewFacts facts)
        {
            this.sim = sim;
            this.facts = facts;
            ownUnits = new NativeList<AiUnit>(256, Allocator.Persistent);
            otherUnits = new NativeList<AiUnit>(256, Allocator.Persistent);
            sim.Settled += CollectEvents;
        }

        /// <summary>Starts collecting an owner's events and keeping its view (an AI that will think). Idempotent.</summary>
        public void Track(int owner)
        {
            if (seats.ContainsKey(owner)) return;
            var seat = new Seat { Owner = owner };
            seat.Sees = p => seat.Data.Sees(seat.Grid, p);
            seat.MayKnowHidden = bombOwner => BombRules.MayKnow(seat.Owner, bombOwner, SharesVision);
            byte[] initial = sim.Map.InitialTiles;
            seat.View.Owner = owner;
            seat.View.Width = sim.Map.Grid.Width;
            seat.View.Height = sim.Map.Grid.Height;
            seat.View.Tiles = (byte[])initial.Clone(); // the generated map, which a client regenerates from the seed
            seat.View.HqSites = sim.Map.HqSites != null ? (int2[])sim.Map.HqSites.Clone() : Array.Empty<int2>();
            seats[owner] = seat;
            seatList.Add(seat);
        }

        /// <summary>Stops tracking an owner (it left the match).</summary>
        public void Untrack(int owner)
        {
            if (!seats.TryGetValue(owner, out Seat seat)) return;
            seats.Remove(owner);
            seatList.Remove(seat);
        }

        /// <summary>True when the owner is tracked.</summary>
        public bool IsTracked(int owner) => seats.ContainsKey(owner);

        /// <summary>
        /// A death explosion at a position this boundary (in a match: the same list clients' explosions come from). Each
        /// tracked owner keeps it only if its grid sees the spot (<see cref="VisibilityRules.ShowsDeath"/>).
        /// </summary>
        public void ReportDeath(float2 position) => pendingDeaths.Add(position);

        /// <summary>Alerts sent to an owner (<see cref="AlertService.Observer"/>): kept for its next think when tracked.</summary>
        public void ReportAlerts(int owner, IReadOnlyList<Alert> alerts)
        {
            if (!seats.TryGetValue(owner, out Seat seat)) return;
            double now = Now(0);
            for (int i = 0; i < alerts.Count; i++)
            {
                Alert a = alerts[i];
                if (seat.Alerts.Count >= MaxEventsPerThink) { seat.Dropped++; continue; }
                seat.Alerts.Add(new AiAlert { Kind = a.Kind, Tile = a.Tile, OtherOwnerId = a.OtherOwnerId, Amount = a.Amount, Time = now });
            }
        }

        private double Now(int tick) => Clock != null ? Clock() : tick * sim.Config.Simulation.TickSeconds;

        private int GridOf(int owner) => sim.TrySlotOf(owner, out int slot) ? slot : -1;

        /// <summary>Every settled tick: the attacks, blasts and deaths each tracked owner's client would be told about.</summary>
        private void CollectEvents(SimData data, int tick, int count)
        {
            if (seatList.Count == 0) { pendingDeaths.Clear(); return; }
            double now = Now(tick);
            GameState state = facts.State;
            ReplicationInput input = ReplicationInput.From(data, count);
            bool anyAttacks = data.AttackEvents.Length > 0 || data.BuildingAttackEvents.Length > 0;
            NativeArray<int2> attacks = default;
            NativeArray<AttackTarget> targets = default;
            NativeArray<BuildingAttack> buildingAttacks = default;
            if (anyAttacks)
            {
                attacks = data.AttackEvents.AsArray();
                targets = ReplicationService.AttackTargets(data, attacks);
                buildingAttacks = ReplicationService.BuildingAttacks(data);
            }
            NativeArray<BombBlast> blasts = data.BombBlasts.Count > 0 ? data.BombBlasts.ToArray(Allocator.Temp) : default;
            foreach (Seat seat in seatList)
            {
                int grid = GridOf(seat.Owner);
                if (anyAttacks)
                {
                    // An AI has no camera: anything its client could pan to counts (the whole map).
                    for (int i = 0; i < attacks.Length; i++)
                    {
                        if (!InterestRules.UnitAttack(input, default, InterestRules.WholeMap, attacks[i], targets[i], seat.Owner, grid, out AttackEvent shown)) continue;
                        AddAttack(seat, new AiAttack { AttackerId = attacks[i].x, TargetId = shown.TargetId, Time = now });
                    }
                    for (int i = 0; i < buildingAttacks.Length; i++)
                    {
                        if (!InterestRules.BuildingAttack(input, default, InterestRules.WholeMap, buildingAttacks[i], seat.Owner, grid, out AttackEvent shown)) continue;
                        AddAttack(seat, new AiAttack { ByBuilding = true, AttackerId = shown.AttackerId, TargetId = shown.TargetId, Time = now });
                    }
                }
                if (blasts.IsCreated)
                    foreach (BombBlast blast in blasts)
                        if (data.Sees(grid, blast.Position))
                            AddBlast(seat, new AiBlast { Kind = blast.Bomber != 0 ? AiBlastKind.Bomber : AiBlastKind.Bomb, Position = blast.Position, Time = now });
                foreach (float2 death in pendingDeaths)
                    if (VisibilityRules.ShowsDeath(state, data.Sees(grid, death)))
                        AddBlast(seat, new AiBlast { Kind = AiBlastKind.Death, Position = death, Time = now });
            }
            pendingDeaths.Clear();
            if (targets.IsCreated) targets.Dispose();
            if (buildingAttacks.IsCreated) buildingAttacks.Dispose();
            if (blasts.IsCreated) blasts.Dispose();
        }

        private static void AddAttack(Seat seat, in AiAttack attack)
        {
            if (seat.Attacks.Count >= MaxEventsPerThink) { seat.Dropped++; return; }
            seat.Attacks.Add(attack);
        }

        private static void AddBlast(Seat seat, in AiBlast blast)
        {
            if (seat.Blasts.Count >= MaxEventsPerThink) { seat.Dropped++; return; }
            seat.Blasts.Add(blast);
        }

        /// <summary>
        /// Builds a tracked owner's view on the settled world (<paramref name="count"/> live unit slots) and returns it (the
        /// same instance every think). The events collected since its last build move into the view. Null when untracked.
        /// </summary>
        public TeamView Build(int owner, SimData data, int tick, int count)
        {
            if (disposed || !seats.TryGetValue(owner, out Seat seat)) return null;
            TeamView view = seat.View;
            int grid = GridOf(owner);
            seat.Data = data;
            seat.Grid = grid;
            view.Tick = tick;
            view.Time = Now(tick);

            BuildUnits(view, data, count, owner, grid);
            BuildBuildings(seat);
            BuildFog(view, data, grid);
            BuildTerrain(seat, data);
            BuildDiplomacy(view, owner);
            facts.Fill(owner, view);

            Move(seat.Alerts, view.Alerts);
            Move(seat.Attacks, view.Attacks);
            Move(seat.Blasts, view.Blasts);
            view.DroppedEvents = seat.Dropped;
            seat.Dropped = 0;
            return view;
        }

        private static void Move<T>(ViewList<T> from, ViewList<T> to) where T : struct
        {
            to.SetCount(from.Count);
            Array.Copy(from.Items, to.Items, from.Count);
            from.Clear();
        }

        private void BuildUnits(TeamView view, SimData data, int count, int owner, int grid)
        {
            ownUnits.Clear();
            otherUnits.Clear();
            new CollectUnitsJob
            {
                Input = ReplicationInput.From(data, count), Owner = owner, Grid = grid,
                Stance = data.Stance, WorkKind = data.WorkKind, OrderSlot = data.OrderSlot, OrderLive = data.OrderLive, OrderGoal = data.OrderGoal, Unpaid = data.Unpaid,
                Own = ownUnits, Others = otherUnits,
            }.Run();
            Copy(ownUnits, view.OwnUnits);
            Copy(otherUnits, view.OtherUnits);
        }

        private static void Copy(NativeList<AiUnit> from, ViewList<AiUnit> to)
        {
            to.SetCount(from.Length);
            if (from.Length > 0) NativeArray<AiUnit>.Copy(from.AsArray(), to.Items, from.Length);
        }

        /// <summary>The units an owner's client may know (<see cref="InterestRules.UnitVisible"/>), its own with their orders.</summary>
        [BurstCompile]
        private struct CollectUnitsJob : IJob
        {
            public ReplicationInput Input;
            public int Owner, Grid;
            [ReadOnly] public NativeArray<byte> Stance, WorkKind, OrderLive, Unpaid;
            [ReadOnly] public NativeArray<int> OrderSlot;
            [ReadOnly] public NativeArray<float2> OrderGoal;
            public NativeList<AiUnit> Own, Others;

            public void Execute()
            {
                for (int i = 0; i < Input.Count; i++)
                {
                    if (!InterestRules.UnitVisible(Input, i, Owner, Grid)) continue;
                    var unit = new AiUnit
                    {
                        Id = Input.IdOf[i], OwnerId = Input.OwnerId[i], Type = (UnitType)Input.Type[i], Position = Input.Positions[i],
                        Health = Input.Health[i], MaxHealth = Input.MaxHealth[i],
                    };
                    if (unit.OwnerId != Owner) { Others.Add(unit); continue; }
                    unit.Stance = Stance[i];
                    unit.Work = WorkKind[i];
                    int order = OrderSlot[i];
                    if (order >= 0 && order < OrderLive.Length && OrderLive[order] != 0)
                    {
                        unit.HasOrder = true;
                        unit.OrderGoal = OrderGoal[order];
                    }
                    unit.Unpaid = Unpaid[i] != 0;
                    Own.Add(unit);
                }
            }
        }

        /// <summary>Applies building records straight to an AI's store, as <see cref="BuildingCodec.Apply"/> does to a client's.</summary>
        private struct StoreSink : IBuildingRecordSink
        {
            public Dictionary<int, AiBuilding> Known;

            public void Enter(in BuildingView v) => Known[v.Id] = new AiBuilding
            {
                Id = v.Id, OwnerId = v.OwnerId, Type = v.Type, Anchor = math.max(v.Anchor, 0), Rotation = (byte)(v.Rotation & 3),
                Health = math.max(0, v.Health), MaxHealth = math.max(0, v.MaxHealth), Progress = v.Progress,
            };

            public void Record(BuildingRecord kind, int id, int value)
            {
                if (kind == BuildingRecord.Gone) { Known.Remove(id); return; }
                if (!Known.TryGetValue(id, out AiBuilding b)) return;
                switch (kind)
                {
                    case BuildingRecord.Health: b.Health = b.MaxHealth > 0 ? math.clamp(value, 0, b.MaxHealth) : math.max(0, value); break;
                    case BuildingRecord.Progress: b.Progress = (byte)math.clamp(value, 0, 100); break;
                    case BuildingRecord.Unpaid: b.Unpaid = value != 0; break;
                    case BuildingRecord.Hide: b.Ghost = true; break;
                }
                Known[id] = b;
            }

            public bool Full => false;
        }

        private void BuildBuildings(Seat seat)
        {
            buildingViews.Clear();
            buildingIds.Clear();
            CollectBuildings?.Invoke(buildingViews);
            foreach (BuildingView v in buildingViews) buildingIds.Add(v.Id);
            var sink = new StoreSink { Known = seat.Known };
            seat.Buildings.Update(buildingViews, buildingIds, seat.Owner, seat.Sees, ref sink, seat.MayKnowHidden);
            ViewList<AiBuilding> into = seat.View.Buildings;
            into.Clear();
            foreach (AiBuilding b in seat.Known.Values) into.Add(b);
        }

        private void BuildFog(TeamView view, SimData data, int grid)
        {
            view.FogW = data.FogW;
            view.FogH = data.FogH;
            view.FogCellSize = data.FogCellSize;
            if (view.Fog.Length != data.FogCells) view.Fog = new byte[data.FogCells];
            if (!fog.IsCreated || fog.Length != data.FogCells)
            {
                if (fog.IsCreated) fog.Dispose();
                fog = new NativeArray<byte>(data.FogCells, Allocator.Persistent);
            }
            new FogJob { Visible = data.Visible, Explored = data.Explored, Offset = grid * data.FogCells, Grid = grid, Out = fog }.Run();
            NativeArray<byte>.Copy(fog, view.Fog, data.FogCells);
        }

        /// <summary>One grid's fog as <c>FogState</c> bytes (what <see cref="FogCodec"/> sends the owner's client).</summary>
        [BurstCompile]
        private struct FogJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Visible, Explored;
            public int Offset, Grid;
            public NativeArray<byte> Out;

            public void Execute()
            {
                if (Grid < 0) { for (int i = 0; i < Out.Length; i++) Out[i] = (byte)FogState.Unexplored; return; }
                for (int i = 0; i < Out.Length; i++)
                    Out[i] = Visible[Offset + i] != 0 ? (byte)FogState.Visible : Explored[Offset + i] != 0 ? (byte)FogState.Explored : (byte)FogState.Unexplored;
            }
        }

        private void BuildTerrain(Seat seat, SimData data)
        {
            TeamView view = seat.View;
            view.TerrainChanged.Clear();
            if (sim.Map.ChangedSinceStart.Count == 0) return;
            terrainNow.Clear();
            foreach (int index in sim.Map.ChangedSinceStartSet)
                terrainNow.Add(TerrainCellOf != null ? TerrainCellOf(index) : new TerrainCell { Index = index, Type = data.Tiles[index] });
            while (true)
            {
                int cells = seat.Terrain.Select(terrainNow, seat.Sees, view.Width, sim.Map.InitialTiles, terrainScratch);
                foreach (TerrainCell c in terrainScratch)
                {
                    view.Tiles[c.Index] = c.Type;
                    if (c.OwnerId != 0) view.Walls[c.Index] = new AiWall { OwnerId = c.OwnerId, Health = c.Health };
                    else view.Walls.Remove(c.Index);
                    view.TerrainChanged.Add(c.Index);
                }
                if (cells < TerrainCodec.MaxCells) break;
            }
        }

        private void BuildDiplomacy(TeamView view, int owner)
        {
            view.Attacking.Clear();
            view.SharingWith.Clear();
            view.SharedWithMe.Clear();
            if (!sim.TrySlotOf(owner, out int slot)) return;
            IReadOnlyList<int> order = facts.PlayerOrder;
            slotByIndex.Clear();
            for (int i = 0; i < order.Count; i++) slotByIndex.Add(sim.TrySlotOf(order[i], out int s) ? s : -1);
            // Its own row only, as GameCore.SendDiplomacy sends a client.
            (ushort attack, ushort share, ushort sharedWithMe) = sim.Diplomacy.RowFor(slot, slotByIndex);
            for (int i = 0; i < order.Count && i < 16; i++)
            {
                if ((attack >> i & 1) != 0) view.Attacking.Add(order[i]);
                if ((share >> i & 1) != 0) view.SharingWith.Add(order[i]);
                if ((sharedWithMe >> i & 1) != 0) view.SharedWithMe.Add(order[i]);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            sim.Settled -= CollectEvents;
            if (ownUnits.IsCreated) ownUnits.Dispose();
            if (otherUnits.IsCreated) otherUnits.Dispose();
            if (fog.IsCreated) fog.Dispose();
        }
    }
}
