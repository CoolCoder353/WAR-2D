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
    /// client's interest set (own units plus units inside its camera box), encodes, and sends each
    /// client its bytes. A joining client's snapshot is paced: at most a tick's share of
    /// <c>SnapshotBytesPerSecond</c> of new units is admitted per tick.
    /// </summary>
    public sealed class ReplicationService : IDisposable
    {
        public const int MaxClients = 16;
        private const int EnterBytesEstimate = 24;
        private const int ReliableBatchLimit = 60 * 1024;

        private sealed class Client
        {
            public NetworkConnectionToClient Connection;
            public Action<ArraySegment<byte>, int> VirtualSink;
            public int OwnerId;
            public long EntersSent;
            public long BytesSent;
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
        private readonly NetworkWriter writer = new NetworkWriter();
        private byte[] copy = new byte[64 * 1024];
        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        private bool disposed;

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
        public void AddVirtualClient(int ownerId, Action<ArraySegment<byte>, int> sink) =>
            Add(new Client { VirtualSink = sink, OwnerId = ownerId });

        private void Add(Client client)
        {
            for (int c = 0; c < MaxClients; c++)
            {
                if (clients[c] != null) continue;
                clients[c] = client;
                active[c] = true;
                interests.ResetClient(c);
                encoder.ResetClient(c);
                interests.SetClient(c, client.OwnerId, new int2(int.MinValue / 2), new int2(int.MinValue / 2));
                encoder.SetView(c, new int2(int.MinValue / 2), new int2(int.MinValue / 2));
                return;
            }
            Debug.LogError("[Replication] no free client slot");
        }

        /// <summary>Stops replicating to a connection.</summary>
        public void OnClientLeft(NetworkConnectionToClient conn)
        {
            int c = Find(conn);
            if (c < 0) return;
            clients[c] = null;
            active[c] = false;
            interests.ResetClient(c);
            encoder.ResetClient(c);
        }

        /// <summary>Sets a client's camera box (inclusive tiles, already clamped to the map).</summary>
        public void SetView(NetworkConnectionToClient conn, int2 min, int2 max)
        {
            int c = Find(conn);
            if (c < 0) return;
            interests.SetClient(c, clients[c].OwnerId, min, max);
            encoder.SetView(c, min, max);
        }

        /// <summary>Sets a virtual client's view, by owner (perf harness).</summary>
        public void SetVirtualView(int ownerId, int2 min, int2 max)
        {
            for (int c = 0; c < MaxClients; c++)
            {
                if (clients[c] == null || clients[c].VirtualSink == null || clients[c].OwnerId != ownerId) continue;
                interests.SetClient(c, ownerId, min, max);
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
            bool any = false;
            for (int c = 0; c < MaxClients; c++) any |= active[c];

            var input = new ReplicationInput
            {
                Count = count, Positions = data.Positions, Health = data.Health, MaxHealth = data.MaxHealth,
                OwnerId = data.OwnerId, Type = data.Type, IdOf = data.IdOf, IndexOfId = data.IndexOfId, SpeedByType = data.SpeedByType,
            };
            JobHandle handle = new RouteMaintenanceJob
            {
                Count = count,
                Tick = tick,
                RetraceBudget = 4000,
                Positions = data.Positions,
                Health = data.Health,
                IdOf = data.IdOf,
                OrderSlot = data.OrderSlot,
                BlockOf = data.Orders.BlockOf,
                Blocks = data.Orders.Blocks.AsArray(),
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
            }.Schedule();
            if (!any)
            {
                handle.Complete();
                LastMilliseconds = watch.Elapsed.TotalMilliseconds;
                return;
            }
            handle = interests.Schedule(input, handle);
            handle.Complete(); // the encoder reads the route arena by value, so it must not move under it
            using var attacks = new NativeArray<int2>(data.AttackEvents.AsArray(), Allocator.TempJob);
            encoder.Schedule(tick, input, attacks, active, default).Complete();

            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                Client client = clients[c];
                if (client.Connection != null && (client.Connection.identity == null || !NetworkServer.connections.ContainsKey(client.Connection.connectionId)))
                {
                    OnClientLeft(client.Connection);
                    continue;
                }
                client.EntersSent += interests.Entered(c).Length;
                var (reliable, reliableSizes, unreliable, unreliableSizes) = encoder.Output(c);
                SendSplit(client, tick, reliable, reliableSizes, Channels.Reliable, ReliableBatchLimit);
                SendSplit(client, tick, unreliable, unreliableSizes, Channels.Unreliable, WireLimits.CorrectionMessageLimit);
            }
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

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
            routes.Dispose();
            interests.Dispose();
            encoder.Dispose();
        }
    }
}
