using WAR2D.Dev.Logging;
using System;
using System.Collections.Generic;
using Config;
using Mirror;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Sim;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// One replication payload: whole encoded messages. Reliable batches carry the stateful messages
    /// (Enter, Leave, MoveOrder, Health); unreliable ones carry one Correction or Attack message each.
    /// </summary>
    public struct ReplicationBatch : NetworkMessage
    {
        /// <summary>The simulation tick the messages describe.</summary>
        public int Tick;
        /// <summary>Reserved (bit 0: snapshot chunk).</summary>
        public byte Flags;
        public ArraySegment<byte> Payload;
    }

    /// <summary>
    /// The server's unit send pipeline. At every settled tick boundary it re-traces routes, builds each
    /// client's interest set (its team's units plus units its team's fog grid sees), encodes, and sends
    /// each client its bytes, plus its own team's fog changes (<see cref="FogBatch"/>). The camera box a
    /// client reports only tunes correction fidelity; it never widens what the client may know. A joining client's snapshot is paced: at most a tick's share of
    /// <c>SnapshotBytesPerSecond</c> of new units is admitted per tick.
    /// </summary>
    public sealed partial class ReplicationService : IDisposable
    {
        public const int MaxClients = 16;
        /// <summary>The channel a virtual sink is given for fog payloads (they are not replication messages).</summary>
        public const int FogSinkChannel = -1;
        /// <summary>The channel a virtual sink is given for building payloads.</summary>
        public const int BuildingSinkChannel = -2;
        /// <summary>The channel a virtual sink is given for terrain payloads.</summary>
        public const int TerrainSinkChannel = -3;
        /// <summary>v0.20: the virtual sink channel of <see cref="CombatBatch"/> payloads.</summary>
        public const int CombatSinkChannel = -4;

        /// <summary>Answers what a tile index currently is (set by the world; null sends the plain kind). Walls add owner and health.</summary>
        public Func<int, TerrainCell> TerrainCellOf;

        /// <summary>Answers whether owner <c>from</c> shares vision with owner <c>to</c> (set by the world; null means nobody does, so only an owner knows its bombs).</summary>
        public Func<int, int, bool> SharesVision;

        /// <summary>Fills the list with every live building (set by the world; null sends no buildings).</summary>
        public Action<List<BuildingView>> CollectBuildings;
        private readonly List<BuildingView> buildingViews = new List<BuildingView>();
        private int buildingViewsTick = -1;

        /// <summary>
        /// The live buildings collected at <paramref name="tick"/>'s boundary, when replication collected them then (the AI's
        /// team view reuses them rather than walking every building again). False otherwise.
        /// </summary>
        public bool TryGetBuildingViews(int tick, out IReadOnlyList<BuildingView> views)
        {
            views = buildingViews;
            return tick >= 0 && buildingViewsTick == tick;
        }
        private readonly HashSet<int> buildingIds = new HashSet<int>();
        private const int EnterBytesEstimate = 24;
        /// <summary>Room a ReplicationBatch needs around its payload (tick, flags, length prefix).</summary>
        private const int BatchHeaderRoom = 16;

        private sealed class Client
        {
            public NetworkConnectionToClient Connection;
            public Action<ArraySegment<byte>, int> VirtualSink;
            public int OwnerId;
            /// <summary>The match recorder: sees every unit and building, bombs included.</summary>
            public bool SeesAll;
            /// <summary>The recorder's off-boundary unit replication (all-seeing clients only), else null.</summary>
            public DeferredLane Lane;
            /// <summary>The fog grid the client was last sent a snapshot of, or -1 (send one).</summary>
            public int FogGrid = -1;
            /// <summary>The fog grid terrain was last walked for, or -1.</summary>
            public int TerrainGrid = -1;
            /// <summary>What the client knows about buildings.</summary>
            public readonly BuildingInterest Buildings = new BuildingInterest();
            /// <summary>What the client knows about changed terrain.</summary>
            public readonly TerrainInterest Terrain = new TerrainInterest();
            public long EntersSent;
            public long BytesSent;
            /// <summary>v0.20: status bits last sent, by unit id index; shells this client was told about.</summary>
            public readonly Dictionary<int, byte> SentStatus = new Dictionary<int, byte>();
            /// <summary>True once a full status diff fitted its batch: from then on its status news can be this tick's changes alone.</summary>
            public bool StatusSynced;
            public readonly HashSet<int> SentShells = new HashSet<int>();
            /// <summary>v0.20: a lane client's combat payload, held until its units (a tick late) have gone out, and its tick.</summary>
            public byte[] PendingCombat;
            public int PendingCombatTick = -1;
        }

        /// <summary>The live service (server only), or null.</summary>
        public static ReplicationService Instance { get; private set; }

        private readonly SimContext sim;
        private readonly GameConfigData config;
        private readonly RouteStore routes;
        private readonly InterestSets interests;
        private readonly ReplicationEncoder encoder;
        private readonly Client[] clients = new Client[MaxClients];
        private readonly bool[] active = new bool[MaxClients];
        /// <summary>Clients the boundary encodes (active, without a deferred lane).</summary>
        private readonly bool[] encodeNow = new bool[MaxClients];
        private readonly NetworkWriter writer = new NetworkWriter();
        private byte[] copy = new byte[64 * 1024];
        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        private bool disposed;

        /// <summary>The tick of the payload a virtual sink is being handed (a deferred lane's is a tick old).</summary>
        public int SinkTick { get; private set; }

        /// <summary>Main-thread milliseconds the last boundary's replication took.</summary>
        public static double LastMilliseconds;

        public ReplicationService(SimContext sim, GameConfigData config)
        {
            this.sim = sim;
            this.config = config;
            int idCapacity = config.Simulation.MaxEntities;
            routes = new RouteStore(idCapacity, Allocator.Persistent);
            interests = new InterestSets(MaxClients, idCapacity, Allocator.Persistent)
            {
                MaxEntersPerBuild = math.max(64, (int)(config.Replication.SnapshotBytesPerSecond * config.Simulation.TickSeconds / EnterBytesEstimate)),
            };
            encoder = new ReplicationEncoder(EncoderConfig.From(config), interests, routes, idCapacity, Allocator.Persistent);
            sim.Settled += AfterBoundary;
            Instance = this;
        }

        /// <summary>Starts replicating to a connection that owns <paramref name="ownerId"/>'s units.</summary>
        public void OnClientJoined(NetworkConnectionToClient conn, int ownerId)
        {
            if (conn == null || Find(conn) >= 0) return;
            Add(new Client { Connection = conn, OwnerId = ownerId });
        }

        /// <summary>A sink that receives a client's bytes without a connection (the perf harness's bot streams).</summary>
        public void AddVirtualClient(int ownerId, Action<ArraySegment<byte>, int> sink, bool seesAll = false) =>
            Add(new Client { VirtualSink = sink, OwnerId = ownerId, SeesAll = seesAll });

        private void Add(Client client)
        {
            for (int c = 0; c < MaxClients; c++)
            {
                if (clients[c] != null) continue;
                clients[c] = client;
                active[c] = true;
                interests.ResetClient(c);
                encoder.ResetClient(c);
                interests.SetClient(c, client.OwnerId, -1); // the grid is set at the next boundary
                encoder.SetView(c, new int2(int.MinValue / 2), new int2(int.MinValue / 2));
                if (client.SeesAll)
                {
                    client.Lane = new DeferredLane(client.OwnerId, EncoderConfig.From(config), config.Simulation.MaxEntities, interests.MaxEntersPerBuild);
                    interests.SetClient(c, int.MinValue, -1); // its units go through the lane, not this boundary's sets
                }
                return;
            }
            WarLog.Error(LogCategory.Replication, "no free client slot");
        }

        /// <summary>Stops replicating to a connection.</summary>
        public void OnClientLeft(NetworkConnectionToClient conn)
        {
            int c = Find(conn);
            if (c < 0) return;
            clients[c].Terrain.Unwatch();
            clients[c] = null;
            active[c] = false;
            interests.ResetClient(c);
            encoder.ResetClient(c);
        }

        /// <summary>Stops replicating to a virtual client (the recorder once its file is finished), by owner.</summary>
        public void RemoveVirtualClient(int ownerId)
        {
            if (disposed) return;
            for (int c = 0; c < MaxClients; c++)
            {
                if (clients[c] == null || clients[c].VirtualSink == null || clients[c].OwnerId != ownerId) continue;
                clients[c].Lane?.Dispose();
                clients[c].Terrain.Unwatch();
                clients[c] = null;
                active[c] = false;
                interests.ResetClient(c);
                encoder.ResetClient(c);
            }
        }

        /// <summary>
        /// Dev overlay: the units (by id index) the client replicated for <paramref name="ownerId"/> knows after the
        /// last boundary, pacing included. False when no such client is replicated at this boundary.
        /// </summary>
        public bool TryGetKnown(int ownerId, out NativeArray<ulong> known)
        {
            known = default;
            if (disposed) return false;
            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c] || clients[c].Lane != null || clients[c].OwnerId != ownerId) continue;
                known = interests.Allowed(c);
                return true;
            }
            return false;
        }

        /// <summary>Sets a client's camera box (inclusive tiles, already clamped to the map). Only tunes correction fidelity.</summary>
        public void SetView(NetworkConnectionToClient conn, int2 min, int2 max)
        {
            int c = Find(conn);
            if (c < 0) return;
            encoder.SetView(c, min, max);
        }

        /// <summary>Sets a virtual client's view, by owner (perf harness).</summary>
        public void SetVirtualView(int ownerId, int2 min, int2 max)
        {
            for (int c = 0; c < MaxClients; c++)
            {
                if (clients[c] == null || clients[c].VirtualSink == null || clients[c].OwnerId != ownerId) continue;
                encoder.SetView(c, min, max);
            }
        }

        /// <summary>Test hook: Enter entries sent to a connection so far.</summary>
        internal long LastEnterCount(NetworkConnectionToClient conn)
        {
            int c = Find(conn);
            return c < 0 ? 0 : clients[c].EntersSent;
        }

        /// <summary>Payload bytes sent to a connection so far.</summary>
        public long BytesSent(NetworkConnectionToClient conn)
        {
            int c = Find(conn);
            return c < 0 ? 0 : clients[c].BytesSent;
        }

        private int Find(NetworkConnectionToClient conn)
        {
            for (int c = 0; c < MaxClients; c++) if (clients[c] != null && clients[c].Connection == conn) return c;
            return -1;
        }

        /// <summary>Runs on the settled world at the tick boundary: route upkeep, interest, encode, send.</summary>
        private void AfterBoundary(SimData data, int tick, int count)
        {
            watch.Restart();
            SinkTick = tick;
            bool any = false;
            for (int c = 0; c < MaxClients; c++) any |= active[c];

            ReplicationInput input = ReplicationInput.From(data, count);
            JobHandle handle = new RouteMaintenance
            {
                Count = count,
                Tick = tick,
                RetraceBudget = 4000,
                Tolerance = config.Replication.RouteTolerance,
                Positions = data.Positions,
                Health = data.Health,
                IdOf = data.IdOf,
                OrderSlot = data.OrderSlot,
                BlockOf = data.Orders.BlockOf,
                Blocks = data.Orders.Blocks.AsArray(),
                OrderReady = data.OrderReady,
                OrderGoal = data.OrderGoal,
                SectorsX = data.Orders.SectorsX,
                SectorsY = data.Orders.SectorsY,
                SectorCount = data.Orders.SectorCount,
                CellsPerSector = data.Orders.CellsPerSector,
                CellSize = data.Orders.CellSize,
                Width = data.Width,
                Height = data.Height,
                Arena = routes.Arena,
                Start = routes.Start,
                RouteCount = routes.Count,
                Generation = routes.Generation,
                TracedOrder = routes.TracedOrder,
                TracedId = routes.TracedId,
                Counters = routes.Counters,
            }.Schedule(default);
            if (!any)
            {
                handle.Complete();
                LastMilliseconds = watch.Elapsed.TotalMilliseconds;
                return;
            }
            for (int c = 0; c < MaxClients; c++)
            {
                encodeNow[c] = active[c] && clients[c].Lane == null;
                if (encodeNow[c]) interests.SetClient(c, clients[c].OwnerId, GridFor(clients[c]));
            }
            // Interest reads only the settled sim arrays (read-only, as the route jobs do) and writes its own sets, so it runs
            // alongside the route upkeep and the sends below instead of after them (perf gate 2026-11-06). Same sets, same order.
            JobHandle interestHandle = interests.Schedule(input, default);
            // The combat news's status list reads only the settled unit arrays too: its pass over every unit runs on a worker
            // alongside the sends (perf gate 2026-11-07: 2.7 ms on the main thread at 80,000 units).
            using var statuses = new NativeList<int2>(256, Allocator.TempJob);
            JobHandle statusHandle = ScheduleStatuses(data, tick, input.Count, statuses);
            double t = PerfSplits.Now;
            SendFog(data);
            PerfSplits.Mark(8, ref t);
            SendTerrain(data, tick);
            PerfSplits.Mark(9, ref t);
            SendBuildings(data);
            PerfSplits.Mark(10, ref t);
            handle.Complete(); // timed alone: the route upkeep ran alongside the sends above
            PerfSplits.Mark(7, ref t);
            interestHandle.Complete(); // the encoder reads the route arena by value, so it must not move under it
            PerfSplits.Mark(11, ref t);
            using var attacks = new NativeArray<int2>(data.AttackEvents.AsArray(), Allocator.TempJob);
            using NativeArray<AttackTarget> targets = AttackTargets(data, attacks);
            using NativeArray<BuildingAttack> buildingAttacks = BuildingAttacks(data);
            PerfSplits.Mark(12, ref t);
            // Each client's bytes go out as soon as that client is encoded, while the workers encode the rest (perf gate
            // 2026-11-06): the same bytes in the same order; repl.encode is now the wait for the first client only.
            using var encoded = new NativeArray<JobHandle>(MaxClients, Allocator.Temp);
            encoder.ScheduleEach(tick, input, attacks, targets, buildingAttacks, encodeNow, default, encoded);
            for (int c = 0; c < MaxClients; c++) if (encodeNow[c]) { encoded[c].Complete(); break; }
            PerfSplits.Mark(13, ref t);

            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                encoded[c].Complete(); // before anything that could reset this client's buffers (OnClientLeft)
                Client client = clients[c];
                if (client.Connection != null && (client.Connection.identity == null || !NetworkServer.connections.ContainsKey(client.Connection.connectionId)))
                {
                    OnClientLeft(client.Connection);
                    continue;
                }
                if (client.Lane != null)
                {
                    // Last tick's units (encoded during this tick), then this tick's copied for the next.
                    if (client.Lane.PendingTick >= 0)
                    {
                        var (r, rs, u, us) = client.Lane.Collect();
                        SinkTick = client.Lane.PendingTick;
                        SendSplit(client, SinkTick, r, rs, Channels.Reliable, BatchLimit(Channels.Reliable, WireLimits.ReliableChunk * 4));
                        SendSplit(client, SinkTick, u, us, Channels.Unreliable, BatchLimit(Channels.Unreliable, WireLimits.CorrectionMessageLimit));
                        FlushPendingCombat(client);
                        SinkTick = tick;
                    }
                    client.Lane.Schedule(data, tick, count, routes, attacks, targets, buildingAttacks);
                    continue;
                }
                client.EntersSent += interests.Entered(c).Length;
                var (reliable, reliableSizes, unreliable, unreliableSizes) = encoder.Output(c);
                SendSplit(client, tick, reliable, reliableSizes, Channels.Reliable, BatchLimit(Channels.Reliable, WireLimits.ReliableChunk * 4));
                SendSplit(client, tick, unreliable, unreliableSizes, Channels.Unreliable, BatchLimit(Channels.Unreliable, WireLimits.CorrectionMessageLimit));
            }
            for (int c = 0; c < MaxClients; c++) encoded[c].Complete(); // a client that left mid-loop: its job still has to finish
            PerfSplits.Mark(14, ref t);
            SendCombat(data, tick, input, statuses, statusHandle);
            PerfSplits.Mark(PerfSplits.ReplCombat, ref t);
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>The building each attack hit (parallel to <paramref name="attacks"/>), from the tick's building arrays; Id 0 for other targets.</summary>
        internal static NativeArray<AttackTarget> AttackTargets(SimData data, NativeArray<int2> attacks)
        {
            var targets = new NativeArray<AttackTarget>(attacks.Length, Allocator.TempJob);
            for (int i = 0; i < attacks.Length; i++) targets[i] = BuildingOf(data, attacks[i].y);
            return targets;
        }

        /// <summary>This tick's building attacks (v0.13), each with its attacking building and (when a building) its target.</summary>
        internal static NativeArray<BuildingAttack> BuildingAttacks(SimData data)
        {
            var attacks = new NativeArray<BuildingAttack>(data.BuildingAttackEvents.Length, Allocator.TempJob);
            for (int i = 0; i < attacks.Length; i++)
            {
                int2 attack = data.BuildingAttackEvents[i];
                attacks[i] = new BuildingAttack { Attacker = BuildingOf(data, attack.x), TargetId = attack.y, Target = BuildingOf(data, attack.y) };
            }
            return attacks;
        }

        /// <summary>A building as the encoder's may-know rule needs it, from the tick's building arrays; Id 0 when the id is not a live building.</summary>
        internal static AttackTarget BuildingOf(SimData data, int id)
        {
            int count = math.min(data.BuildingCount[0], data.BuildingCapacity);
            int index = NetIdAllocator.IndexOf(id);
            if (id <= 0 || (uint)index >= (uint)data.BuildingSlotOfIndex.Length) return default;
            int b = data.BuildingSlotOfIndex[index];
            if ((uint)b >= (uint)count || data.BuildingIds[b] != id) return default;
            return new AttackTarget
            {
                Id = id,
                OwnerId = data.BuildingOwnerId[b],
                Position = data.BuildingPositions[b],
                Hidden = (byte)((data.BuildingFlag[b] & global::BuildingFlags.Hidden) != 0 ? 1 : 0),
            };
        }

        private readonly Dictionary<int, List<int>> fogChanges = new Dictionary<int, List<int>>();
        private NativeArray<byte> allSeen;

        /// <summary>
        /// Answers whether an owner is a defeated player spectating (server: eliminated while the match is
        /// still Playing). Such a client is given <see cref="SimData.SeesAll"/>: every unit, building and a clear
        /// fog. Null (tests, the menu battle) means nobody spectates.
        /// </summary>
        public Func<int, bool> Spectating;

        /// <summary>The grid a spectating owner watches: <see cref="SimData.SeesAll"/> or the player they chose (null: the whole map).</summary>
        public Func<int, int> SpectatorGrid;

        /// <summary>A client's grid: everything for the recorder, else as <see cref="GridOf"/>.</summary>
        /// <summary>
        /// What the owner's own (network) client was told: its buildings, ghosts included, and its terrain. False for an owner
        /// with no such client (an AI, or nobody connected), whose placements are judged on the real map.
        /// </summary>
        public bool TryKnowledgeOf(int ownerId, out BuildingInterest buildings, out TerrainInterest terrain)
        {
            for (int c = 0; c < MaxClients; c++)
            {
                Client client = clients[c];
                if (!active[c] || client == null || client.SeesAll || client.VirtualSink != null || client.OwnerId != ownerId) continue;
                buildings = client.Buildings;
                terrain = client.Terrain;
                return true;
            }
            buildings = null;
            terrain = null;
            return false;
        }

        private int GridFor(Client client) => client.SeesAll ? SimData.SeesAll : GridOf(client.OwnerId);

        /// <summary>The fog grid an owner's client is replicated against.</summary>
        private int GridOf(int ownerId) => Spectating != null && Spectating(ownerId) ? SpectatorGrid != null ? SpectatorGrid(ownerId) : SimData.SeesAll : sim.VisionOf(ownerId);

        /// <summary>
        /// Sends every client its own team's fog: a snapshot when its grid is new to it, otherwise the
        /// cells that flipped in the last vision pass. Runs on the settled world, before the queue is cleared.
        /// </summary>
        private void SendFog(SimData data)
        {
            foreach (List<int> list in fogChanges.Values) list.Clear();
            while (data.FogChanges.TryDequeue(out int index))
            {
                int grid = index / data.FogCells;
                if (!fogChanges.TryGetValue(grid, out List<int> list)) fogChanges[grid] = list = new List<int>();
                list.Add(index - grid * data.FogCells);
            }
            foreach (List<int> list in fogChanges.Values) list.Sort();

            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                Client client = clients[c];
                int grid = GridFor(client);
                if (grid < 0) continue;
                if (grid == SimData.SeesAll)
                {
                    // A spectator: one snapshot of a fully seen map, then nothing (nothing can flip).
                    if (client.FogGrid == grid) continue;
                    client.FogGrid = grid;
                    if (!allSeen.IsCreated || allSeen.Length != data.FogCells)
                    {
                        if (allSeen.IsCreated) allSeen.Dispose();
                        allSeen = new NativeArray<byte>(data.FogCells, Allocator.Persistent);
                        for (int i = 0; i < allSeen.Length; i++) allSeen[i] = 1;
                    }
                    SendFogSnapshot(client, data, allSeen, allSeen, 0);
                    continue;
                }
                if (client.FogGrid != grid)
                {
                    client.FogGrid = grid;
                    SendFogSnapshot(client, data, data.Visible, data.Explored, grid * data.FogCells);
                    continue;
                }
                if (!fogChanges.TryGetValue(grid, out List<int> cells)) continue;
                int limit = FogLimit;
                for (int start = 0; start < cells.Count;)
                {
                    int count = FogCodec.DeltaCountWithin(cells, start, limit);
                    writer.Position = 0;
                    FogCodec.WriteDelta(writer, cells, start, count);
                    SendFogPayload(client, writer.ToArraySegment());
                    start += count;
                }
            }
        }

        /// <summary>Largest fog payload: one that passes the transport's cap is dropped and never resent.</summary>
        private static int FogLimit => BatchLimit(Channels.Reliable, FogCodec.MaxPayloadBytes);

        /// <summary>Sends a whole fog grid as snapshot parts, each under <see cref="FogLimit"/>.</summary>
        private void SendFogSnapshot(Client client, SimData data, NativeArray<byte> visible, NativeArray<byte> explored, int offset)
        {
            int cells = data.FogW * data.FogH, limit = FogLimit;
            for (int start = 0; start < cells;)
            {
                writer.Position = 0;
                start = FogCodec.WriteSnapshot(writer, data.FogW, data.FogH, data.FogCellSize, visible, explored, offset, start, limit);
                SendFogPayload(client, writer.ToArraySegment());
            }
        }

        /// <summary>
        /// Sends every client the changed tiles its grid sees (see <see cref="TerrainInterest"/>). Fog only moves on
        /// vision ticks, so the walk runs then, and whenever a client's fog grid was just sent. Each client walks only
        /// the tiles it was not yet told about (<see cref="TerrainInterest.SelectPending"/>), so the cost no longer grows
        /// with every tile changed since the match started.
        /// </summary>
        private void SendTerrain(SimData data, int tick)
        {
            if (sim.Map.ChangedSinceStart.Count == 0) return;
            bool visionTick = (tick - 1) % data.VisionInterval == 0;
            int limit = BatchLimit(Channels.Reliable, WireLimits.ReliableChunk * 4);
            Func<int, TerrainCell> cellOf = TerrainCellOf ?? (i => new TerrainCell { Index = i, Type = data.Tiles[i] });
            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                Client client = clients[c];
                if (!client.Terrain.Watching) client.Terrain.Watch(sim.Map);
                int grid = GridFor(client);
                if (grid < 0 || (!visionTick && client.TerrainGrid == grid)) continue;
                client.TerrainGrid = grid;
                if (client.Terrain.PendingCount == 0) continue;
                while (true)
                {
                    writer.Position = 0;
                    int cells = client.Terrain.UpdatePending(cellOf, p => data.Sees(grid, p), data.Width, sim.Map.InitialTiles, writer, limit);
                    if (cells == 0) break;
                    ArraySegment<byte> payload = writer.ToArraySegment();
                    client.BytesSent += payload.Count;
                    if (client.VirtualSink != null) client.VirtualSink(payload, TerrainSinkChannel);
                    else client.Connection.Send(new TerrainBatch { Payload = payload }, Channels.Reliable);
                    if (cells < TerrainCodec.MaxCells) break;
                }
            }
        }

        /// <summary>Sends every client the building records that bring it up to date (see <see cref="BuildingInterest"/>).</summary>
        private void SendBuildings(SimData data)
        {
            if (CollectBuildings == null) return;
            buildingViews.Clear();
            buildingIds.Clear();
            CollectBuildings(buildingViews);
            buildingViewsTick = SinkTick;
            foreach (BuildingView v in buildingViews) buildingIds.Add(v.Id);
            int limit = BatchLimit(Channels.Reliable, WireLimits.ReliableChunk * 4);
            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                Client client = clients[c];
                int grid = GridFor(client);
                writer.Position = 0;
                if (client.Buildings.Update(buildingViews, buildingIds, client.OwnerId, p => data.Sees(grid, p), writer, limit, owner => client.SeesAll || BombRules.MayKnow(client.OwnerId, owner, SharesVision)) == 0) continue;
                ArraySegment<byte> payload = writer.ToArraySegment();
                client.BytesSent += payload.Count;
                if (client.VirtualSink != null) client.VirtualSink(payload, BuildingSinkChannel);
                else client.Connection.Send(new BuildingBatch { Payload = payload }, Channels.Reliable);
            }
        }

        private static void SendFogPayload(Client client, ArraySegment<byte> payload)
        {
            client.BytesSent += payload.Count;
            if (client.VirtualSink != null) client.VirtualSink(payload, FogSinkChannel);
            else client.Connection.Send(new FogBatch { Payload = payload }, Channels.Reliable);
        }

        /// <summary>Largest payload one batch may carry on a channel: Mirror's limit less the header, capped.</summary>
        private static int BatchLimit(int channel, int cap) =>
            Transport.active != null ? math.min(cap, NetworkMessages.MaxContentSize(channel) - BatchHeaderRoom) : cap;

        /// <summary>Sends whole messages in batches no larger than <paramref name="limit"/> bytes.</summary>
        private void SendSplit(Client client, int tick, NativeList<byte> bytes, NativeList<int> sizes, int channel, int limit)
        {
            if (bytes.Length == 0) return;
            if (copy.Length < bytes.Length) copy = new byte[math.ceilpow2(bytes.Length)];
            NativeArray<byte>.Copy(bytes.AsArray(), 0, copy, 0, bytes.Length);
            int offset = 0, i = 0;
            while (i < sizes.Length)
            {
                int start = offset, length = 0;
                while (i < sizes.Length && (length == 0 || length + sizes[i] <= limit))
                {
                    length += sizes[i];
                    i++;
                }
                offset += length;
                var segment = new ArraySegment<byte>(copy, start, length);
                client.BytesSent += length;
                if (client.VirtualSink != null)
                {
                    client.VirtualSink(segment, channel);
                }
                else
                {
                    client.Connection.Send(new ReplicationBatch { Tick = tick, Payload = segment }, channel);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            sim.Settled -= AfterBoundary;
            if (Instance == this) Instance = null;
            foreach (Client client in clients)
            {
                client?.Lane?.Dispose();
                client?.Terrain.Unwatch();
            }
            routes.Dispose();
            interests.Dispose();
            if (allSeen.IsCreated) allSeen.Dispose();
            encoder.Dispose();
        }
    }
}
