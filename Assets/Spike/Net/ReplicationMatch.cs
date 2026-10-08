using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// The world both replication stages measure: the scripted 60-second battle of the plan's
    /// scenario, the flow fields its orders point at, the fog that decides what each team may see,
    /// every unit's route, and the encoder writing all eight clients' streams. Stage A counts the
    /// bytes, Stage B pushes the same streams through the transport, and both drive this one object,
    /// so their streams are identical.
    ///
    /// <para>The tick is split into the parts the benches time separately: <see cref="Step"/> does the
    /// scripted order pulse, the terrain change, the simulation tick and the fog tick; then
    /// <see cref="Maintain"/> re-traces the routes, <see cref="BuildInterests"/> rebuilds the allowed
    /// sets, and <see cref="Encode"/> writes one client's messages.</para>
    /// </summary>
    public sealed class ReplicationMatch : IDisposable
    {
        /// <summary>Extra unit slots above the starting army, for the churn's transient overshoot.</summary>
        private const int CapacitySlack = 2048;

        /// <summary>Ticks between order pulses: two seconds at the spike's 20 Hz.</summary>
        public const int OrderIntervalTicks = 40;

        /// <summary>The plan's fallback: at most two flow-field rebuilds a tick.</summary>
        private const int RebuildCap = 2;

        /// <summary>The scenario's players; the spike always places eight.</summary>
        public const int Players = 8;

        private readonly NativeList<int2> terrain;
        private readonly Allocator allocator;
        private readonly int largePercent;
        private bool disposed;

        /// <summary>The options this match was built for; the benches publish their metrics with them.</summary>
        public readonly SpikeArgs Args;

        /// <summary>The map the match is played on.</summary>
        public SpikeMap Map;

        /// <summary>The scripted scenario: placement, orders and terrain changes.</summary>
        public readonly SpikeScenario Scenario;

        /// <summary>The flow-field cache the order slots came from.</summary>
        public readonly FlowFieldCache Cache;

        /// <summary>The simulation.</summary>
        public readonly SpikeSim Sim;

        /// <summary>The fog: per-team visibility, refreshed at 5 Hz.</summary>
        public readonly FogSystem Fog;

        /// <summary>Every unit's route.</summary>
        public readonly RouteStore Routes;

        /// <summary>The server's route maintenance.</summary>
        public readonly RouteMaintenance Maintenance;

        /// <summary>What each client is allowed to know.</summary>
        public readonly InterestSets Interests;

        /// <summary>The encoder.</summary>
        public readonly ReplicationEncoder Encoder;

        /// <summary>The reliable writer the encoder appends to; reset it (Position = 0) between clients.</summary>
        public readonly NetworkWriter Reliable = new NetworkWriter();

        /// <summary>The unreliable writer the encoder appends to.</summary>
        public readonly NetworkWriter Unreliable = new NetworkWriter();

        /// <summary>Clients encoded per tick.</summary>
        public readonly int Clients;

        /// <summary>Fog teams: 8 (FFA) or 2 (4v4).</summary>
        public readonly int Teams;

        /// <summary>Measured ticks this match runs for.</summary>
        public readonly int Ticks;

        /// <summary>Vision radius of a unit, in tiles.</summary>
        public readonly int UnitVision;

        private ReplicationMatch(SpikeArgs a, int ticks, Allocator allocator)
        {
            this.allocator = allocator;
            Args = a;
            largePercent = a.LargePercent;
            Clients = math.clamp(a.Clients, 1, Players);
            Teams = FogTeams.Count(a.Teams);
            Ticks = math.max(1, ticks);

            Map = MapGenerator.Generate(a.MapSize, (uint)a.Seed, allocator);
            var build = SpikeScenarioConfig.Defaults;
            build.Players = Players;
            build.UnitsPerPlayer = math.max(1, a.Units / Players);
            build.LargePercent = a.LargePercent;
            build.Placement = SpikePlacement.Fronts; // the plan's battle layout
            build.OrderIntervalTicks = OrderIntervalTicks;
            build.ScriptTicks = 0; // the bench decides how long the match runs
            Scenario = SpikeScenario.Create(build, Map, allocator);

            Cache = new FlowFieldCache(Map, allocator);
            var config = SpikeSimConfig.Defaults;
            config.Players = Players;
            config.Capacity = Scenario.UnitCount + CapacitySlack;
            config.IdCapacity = config.Capacity + SpikeSimRules.SpawnPerTick * Players * (Ticks + 64) + 64;
            config.Slice = a.SliceTicks;
            config.CellSize = a.CellSize;
            config.SpawnPerTick = SpikeSimRules.SpawnPerTick;
            config.SpawnTarget = Scenario.UnitsPerPlayer;
            config.SpawnOrigins = Scenario.HqSites;
            Sim = SpikeSim.Create(config, Map, allocator, Cache);
            Sim.AddScenario(Scenario);
            SetupOrders();

            var fogConfig = FogConfig.Defaults;
            fogConfig.Teams = Teams;
            fogConfig.UnitVision = math.max(1, a.VisionRadius);
            fogConfig.BuildingVision = math.max(1, a.BuildingVision);
            UnitVision = fogConfig.UnitVision;
            Fog = new FogSystem(fogConfig, Map.Width, Map.Height, Map.Tiles, allocator);
            Fog.SetUnits(Sim.Data.Positions, Sim.Data.Team, Sim.Data.Health, Sim.Data.Capacity);
            Fog.SetBuildings(Scenario.BuildingPositions, Scenario.BuildingOwners, Scenario.BuildingHealth, Scenario.BuildingCount);

            Routes = new RouteStore(Sim.Data.IdCapacity, allocator);
            Maintenance = new RouteMaintenance(RouteConfig.Defaults, Map, allocator);
            Maintenance.SetWorld(Sim.Data);
            Maintenance.SetStore(Routes);

            Interests = new InterestSets(Clients, Teams, Sim.Data.IdCapacity, allocator);
            Interests.SetFog(Fog);
            Interests.SetTileGrid(Map.Width, Map.Height);

            Encoder = new ReplicationEncoder(EncoderConfig.Defaults, Interests, Routes,
                Sim.Data.Capacity, Sim.Data.IdCapacity, allocator);
            Encoder.SetUnits(Sim.Data.Positions, Sim.Data.Health, Sim.Data.Team, Sim.Data.IdOf, Sim.Data.IndexOfId, Sim.Data.Capacity);
            Encoder.SetFog(Fog);

            terrain = new NativeList<int2>(8, allocator);
        }

        /// <summary>Builds a match for <paramref name="a"/>'s options, running for <paramref name="ticks"/> measured ticks.</summary>
        public static ReplicationMatch Create(SpikeArgs a, int ticks, Allocator allocator) =>
            new ReplicationMatch(a, ticks, allocator);

        /// <summary>
        /// Prebuilds one field per (goal, size class) the order stream uses (the plan's ruling: the
        /// measured tick never builds a field) and points each army's size classes at their slot.
        /// </summary>
        private void SetupOrders()
        {
            var slotByGoal = new Dictionary<long, int>();
            foreach ((int player, int2 goal, float _) in Scenario.OrdersAt(0))
            {
                int classes = largePercent > 0 ? 2 : 1;
                for (int sizeClass = 0; sizeClass < classes; sizeClass++)
                {
                    long key = ((long)goal.y * 100000 + goal.x) * 2 + sizeClass;
                    if (!slotByGoal.TryGetValue(key, out int slot))
                    {
                        slot = Sim.AddField(Cache, goal, sizeClass);
                        slotByGoal[key] = slot;
                    }
                    Sim.SetPlayerSlot(player, sizeClass, slot);
                }
            }
        }

        /// <summary>The scripted order pulse, applied between ticks on a completed world.</summary>
        public void Intake(int tick)
        {
            foreach ((int _, int2 _, float fraction) in Scenario.OrdersAt(tick))
            {
                Sim.ApplyOrders(tick / math.max(1, Scenario.OrderIntervalTicks), (int)math.round(1f / fraction));
                return;
            }
        }

        /// <summary>
        /// The scenario's terrain script: the tile flips, the flow fields covering it are invalidated
        /// and at most two are rebuilt this tick (the plan's fallback), and the sim takes the rebuilt
        /// directions.
        ///
        /// <para><b>Routes are not re-traced after a terrain change.</b> The bench keeps the route the
        /// client was given and lets the correction channel carry any divergence, because a re-trace
        /// starts at the unit's <i>current</i> tile: re-tracing a unit that has walked on always
        /// produces a different polyline (it is shorter), so every re-trace would look like a path
        /// change and the route store would hand every unit a MoveOrder every tick. A production server
        /// that wants to refresh paths after terrain changes has to compare the field's route from the
        /// route's own start point (see <see cref="RouteMaintenance.InvalidateOrder"/>) and then re-base
        /// the residual route; that is a follow-up, and this bench measures the cost of not doing it.
        /// </para>
        /// </summary>
        public void ApplyTerrain(int tick)
        {
            terrain.Clear();
            Scenario.TerrainChangesAt(tick, terrain);
            if (terrain.Length == 0) return;

            for (int i = 0; i < terrain.Length; i++)
            {
                int2 tile = terrain[i];
                int cell = tile.y * Map.Width + tile.x;
                Map.Tiles[cell] = Map.Tiles[cell] == SpikeMap.Floor ? SpikeMap.Rock : SpikeMap.Floor;
                Cache.Invalidate(tile);
            }
            Cache.RebuildDirty(RebuildCap).Complete();
            Cache.CompleteRebuilds();
            Sim.RefreshFields(Cache);
            // Routes are deliberately not re-traced here; see the summary above.
        }

        /// <summary>One tick's scripted work: orders, terrain, the simulation and the fog, all untimed.</summary>
        public void Step(int tick)
        {
            Intake(tick);
            ApplyTerrain(tick);
            Sim.Tick();
            Fog.Tick();
        }

        /// <summary>Re-traces the routes the tick invalidated; the benches time this separately.</summary>
        public void Maintain() => Maintenance.Maintain();

        /// <summary>Rebuilds every client's allowed and known sets for the tick about to be encoded.</summary>
        public void BuildInterests() => Interests.Build();

        /// <summary>
        /// Encodes one client's tick into <see cref="Reliable"/> and <see cref="Unreliable"/> and
        /// returns the two payload sizes. The caller resets the writers' positions between clients.
        /// </summary>
        public (int reliable, int unreliable) Encode(int client, int tick)
        {
            Encoder.Encode(client, tick, Reliable, Unreliable);
            return (Reliable.Position, Unreliable.Position);
        }

        /// <summary>Payload bytes the last <see cref="Encode"/> would cost on the wire.</summary>
        public long LastWireBytes() =>
            SpikeWire.WireBytes(Encoder.ReliableMessageSizes) + SpikeWire.WireBytes(Encoder.UnreliableMessageSizes);

        /// <summary>Live units in the SoA right now.</summary>
        public int LiveUnits()
        {
            int live = 0;
            for (int i = 0; i < Sim.Data.Capacity; i++) if (Sim.Data.Health[i] > 0f) live++;
            return live;
        }

        /// <summary>Disposes everything the match allocated. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            terrain.Dispose();
            Encoder.Dispose();
            Interests.Dispose();
            Maintenance.Dispose();
            Routes.Dispose();
            Fog.Dispose();
            Sim.Dispose();
            Cache.Dispose();
            Scenario.Dispose();
            Map.Dispose();
        }
    }
}
