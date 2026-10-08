using System;
using Config;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>Tuning for <see cref="ReplicationEncoder"/>, from <see cref="ReplicationConfig"/>.</summary>
    public struct EncoderConfig
    {
        public float CorrectionThreshold;
        public int CorrectionInterval;
        public byte DeltaScale;
        public float OffscreenThreshold;
        public int OffscreenInterval;
        /// <summary>Seconds per tick: message time is <c>tick * Dt</c>.</summary>
        public float Dt;

        public static EncoderConfig From(GameConfigData config) => new EncoderConfig
        {
            CorrectionThreshold = config.Replication.CorrectionThreshold,
            CorrectionInterval = math.max(1, config.Replication.CorrectionIntervalTicks),
            DeltaScale = (byte)math.clamp(config.Replication.DeltaScale, 1, 127),
            OffscreenThreshold = config.Replication.OffscreenThreshold,
            OffscreenInterval = math.max(1, config.Replication.OffscreenIntervalTicks),
            Dt = config.Simulation.TickSeconds,
        };
    }

    /// <summary>
    /// The server half of route-plus-correction replication. Per client and tick it turns the settled
    /// world into messages: Leave and Enter from the interest diff, Health on change, MoveOrder when a
    /// unit's route changes, and Correction when the client's prediction (the same
    /// <see cref="MovementPrediction"/> the client runs) drifts past the threshold — finer inside the
    /// camera view, coarser outside it. Attack events go to clients whose view holds the attacker.
    ///
    /// <para><b>What it can see.</b> Every unit loop is driven by the client's
    /// <see cref="InterestSets"/>: a unit outside the allowed set is never encoded for that client.</para>
    /// </summary>
    public sealed class ReplicationEncoder : IDisposable
    {
        private readonly EncoderConfig config;
        private readonly InterestSets interests;
        private readonly RouteStore routes;
        private readonly ClientBuffers[] buffers;
        private bool disposed;

        public ReplicationEncoder(in EncoderConfig config, InterestSets interests, RouteStore routes, int idCapacity, Allocator allocator)
        {
            this.config = config;
            this.interests = interests;
            this.routes = routes;
            buffers = new ClientBuffers[interests.Clients];
            for (int c = 0; c < buffers.Length; c++) buffers[c] = new ClientBuffers(idCapacity, allocator);
        }

        /// <summary>Sets a client's camera box (inclusive tiles) for the view tier and attack filter.</summary>
        public void SetView(int client, int2 min, int2 max)
        {
            buffers[client].View[0] = new int4(math.min(min, max), math.max(min, max));
        }

        /// <summary>Forgets a client's predictions (it left, or a new client takes the slot).</summary>
        public void ResetClient(int client)
        {
            NativeArray<PredictState> states = buffers[client].States;
            for (int i = 0; i < states.Length; i++) states[i] = default;
        }

        /// <summary>Schedules every client's encode after <paramref name="dependency"/> (the interest build).</summary>
        public JobHandle Schedule(int tick, in ReplicationInput input, NativeArray<int2> attacks, bool[] active, JobHandle dependency)
        {
            var handles = new NativeArray<JobHandle>(buffers.Length, Allocator.Temp);
            for (int c = 0; c < buffers.Length; c++)
            {
                if (active != null && !active[c]) { handles[c] = dependency; continue; }
                handles[c] = Job(c, tick, input, attacks).Schedule(dependency);
            }
            JobHandle all = JobHandle.CombineDependencies(handles);
            handles.Dispose();
            return all;
        }

        /// <summary>Encodes every client now (the interest sets must be built for this tick).</summary>
        public void EncodeAll(int tick, in ReplicationInput input, NativeArray<int2> attacks) =>
            Schedule(tick, input, attacks, null, default).Complete();

        /// <summary>A client's last encoded bytes: reliable messages (with each message's size) and unreliable ones.</summary>
        public (NativeList<byte> reliable, NativeList<int> reliableSizes, NativeList<byte> unreliable, NativeList<int> unreliableSizes) Output(int client)
        {
            ClientBuffers b = buffers[client];
            return (b.Reliable, b.ReliableSizes, b.Unreliable, b.UnreliableSizes);
        }

        /// <summary>The position the client predicts for a unit (by id index) after the last encode.</summary>
        public float2 PredictedPosition(int client, int index) => buffers[client].States[index].LastPredicted;

        /// <summary>The client's prediction state for a unit (by id index).</summary>
        public PredictState State(int client, int index) => buffers[client].States[index];

        private EncodeClientJob Job(int client, int tick, in ReplicationInput input, NativeArray<int2> attacks)
        {
            ClientBuffers b = buffers[client];
            return new EncodeClientJob
            {
                Config = config,
                Tick = tick,
                Input = input,
                Waypoints = routes.Arena.AsArray(),
                RouteStart = routes.Start,
                RouteCount = routes.Count,
                RouteGeneration = routes.Generation,
                Allowed = interests.Allowed(client),
                Entered = interests.Entered(client).AsArray(),
                Left = interests.Left(client).AsArray(),
                Attacks = attacks,
                View = b.View,
                States = b.States,
                Reliable = b.Reliable,
                ReliableSizes = b.ReliableSizes,
                Unreliable = b.Unreliable,
                UnreliableSizes = b.UnreliableSizes,
                Enters = b.Enters,
                EnterTiles = b.EnterTiles,
                MoveOrders = b.MoveOrders,
                MoveTiles = b.MoveTiles,
                Leaves = b.Leaves,
                Healths = b.Healths,
                Corrections = b.Corrections,
                AttackOut = b.Attacks,
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (ClientBuffers b in buffers) b.Dispose();
        }

        private sealed class ClientBuffers : IDisposable
        {
            public NativeArray<PredictState> States;
            public NativeArray<int4> View;
            public NativeList<byte> Reliable, Unreliable;
            public NativeList<int> ReliableSizes, UnreliableSizes;
            public NativeList<EnterUnit> Enters;
            public NativeList<int2> EnterTiles, MoveTiles;
            public NativeList<RouteUnit> MoveOrders;
            public NativeList<LeaveUnit> Leaves;
            public NativeList<HealthUnit> Healths;
            public NativeList<CorrectionUnit> Corrections;
            public NativeList<AttackEvent> Attacks;

            public ClientBuffers(int idCapacity, Allocator allocator)
            {
                States = new NativeArray<PredictState>(idCapacity, allocator);
                View = new NativeArray<int4>(1, allocator);
                View[0] = new int4(int.MinValue / 2, int.MinValue / 2, int.MaxValue / 2, int.MaxValue / 2);
                Reliable = new NativeList<byte>(64 * 1024, allocator);
                Unreliable = new NativeList<byte>(16 * 1024, allocator);
                ReliableSizes = new NativeList<int>(64, allocator);
                UnreliableSizes = new NativeList<int>(64, allocator);
                Enters = new NativeList<EnterUnit>(256, allocator);
                EnterTiles = new NativeList<int2>(1024, allocator);
                MoveOrders = new NativeList<RouteUnit>(256, allocator);
                MoveTiles = new NativeList<int2>(1024, allocator);
                Leaves = new NativeList<LeaveUnit>(64, allocator);
                Healths = new NativeList<HealthUnit>(256, allocator);
                Corrections = new NativeList<CorrectionUnit>(1024, allocator);
                Attacks = new NativeList<AttackEvent>(256, allocator);
            }

            public void Dispose()
            {
                States.Dispose(); View.Dispose(); Reliable.Dispose(); Unreliable.Dispose(); ReliableSizes.Dispose();
                UnreliableSizes.Dispose(); Enters.Dispose(); EnterTiles.Dispose(); MoveOrders.Dispose(); MoveTiles.Dispose();
                Leaves.Dispose(); Healths.Dispose(); Corrections.Dispose(); Attacks.Dispose();
            }
        }
    }

    /// <summary>One client's encode for one tick. Deterministic float mode, matching the client's prediction job.</summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic)]
    public struct EncodeClientJob : IJob
    {
        public EncoderConfig Config;
        public int Tick;
        public ReplicationInput Input;
        [ReadOnly] public NativeArray<float2> Waypoints;
        [ReadOnly] public NativeArray<int> RouteStart, RouteCount, RouteGeneration;
        [ReadOnly] public NativeArray<ulong> Allowed;
        [ReadOnly] public NativeArray<int> Entered, Left;
        [ReadOnly] public NativeArray<int2> Attacks;
        [ReadOnly] public NativeArray<int4> View;
        public NativeArray<PredictState> States;
        public NativeList<byte> Reliable, Unreliable;
        public NativeList<int> ReliableSizes, UnreliableSizes;
        public NativeList<EnterUnit> Enters;
        public NativeList<int2> EnterTiles, MoveTiles;
        public NativeList<RouteUnit> MoveOrders;
        public NativeList<LeaveUnit> Leaves;
        public NativeList<HealthUnit> Healths;
        public NativeList<CorrectionUnit> Corrections;
        public NativeList<AttackEvent> AttackOut;

        public void Execute()
        {
            float now = Tick * Config.Dt;
            Reliable.Clear(); Unreliable.Clear(); ReliableSizes.Clear(); UnreliableSizes.Clear();
            Enters.Clear(); EnterTiles.Clear(); MoveOrders.Clear(); MoveTiles.Clear();
            Leaves.Clear(); Healths.Clear(); Corrections.Clear(); AttackOut.Clear();
            int4 view = View[0];

            // Departures first: the interest diff has already moved on, so this is the only report.
            for (int i = 0; i < Left.Length; i++)
            {
                int id = Left[i];
                int index = NetIdAllocator.IndexOf(id);
                int slot = Input.SlotOfIndex(index);
                bool alive = slot >= 0 && Input.IdOf[slot] == id;
                Leaves.Add(new LeaveUnit { Index = index, Reason = alive ? LeaveReason.LeftView : LeaveReason.Died });
                States[index] = default;
            }
            SortLeaves();

            // Arrivals, anchored at their real position and resuming on their route.
            for (int i = 0; i < Entered.Length; i++)
            {
                int id = Entered[i];
                int index = NetIdAllocator.IndexOf(id);
                int slot = Input.SlotOfIndex(index);
                if (slot < 0 || Input.IdOf[slot] != id) continue;
                float2 position = Input.Positions[slot];
                var entry = new EnterUnit
                {
                    Id = id,
                    Type = Input.Type[slot],
                    OwnerId = Input.OwnerId[slot],
                    X = Quantise.Encode(position.x),
                    Y = Quantise.Encode(position.y),
                    Health = HealthByte(slot),
                    First = EnterTiles.Length,
                };
                entry.Count = AppendRoute(index, position, EnterTiles);
                Enters.Add(entry);
                float fullSpeed = Input.Type[slot] < Input.SpeedByType.Length ? Input.SpeedByType[Input.Type[slot]] : 0f;
                var state = new PredictState { Id = id, LastHealth = entry.Health, RouteGeneration = RouteGeneration[index], FullSpeed = fullSpeed, Fresh = 1 };
                EnterState(ref state, index, entry, now);
                state.LastActual = position;
                state.LastCheck = now;
                States[index] = state;
            }

            // Everything the client may know: health, new routes and corrections.
            for (int w = 0; w < Allowed.Length; w++)
            {
                ulong bits = Allowed[w];
                while (bits != 0)
                {
                    int bit = math.tzcnt(bits);
                    bits &= bits - 1;
                    int index = w * 64 + bit;
                    int slot = Input.SlotOfIndex(index);
                    if (slot < 0) continue;
                    PredictState state = States[index];
                    if (state.Id != Input.IdOf[slot] || state.Fresh != 0) continue;

                    byte health = HealthByte(slot);
                    if (health != state.LastHealth)
                    {
                        Healths.Add(new HealthUnit { Index = index, Health = health });
                        state.LastHealth = health;
                    }
                    if (RouteGeneration[index] != state.RouteGeneration)
                    {
                        MoveOrders.Add(new RouteUnit { Index = index, First = MoveTiles.Length, Count = AppendRoute(index, Input.Positions[slot], MoveTiles) });
                        state.RouteGeneration = RouteGeneration[index];
                        MovementPrediction.StartRoute(ref state, Waypoints.Length > 0 && RouteCount[index] > 0 ? Waypoints[RouteStart[index]] : Input.Positions[slot], now);
                    }
                    bool inView = InView(view, Input.Positions[slot]);
                    int every = inView ? Config.CorrectionInterval : Config.OffscreenInterval;
                    if ((Tick + index) % every == 0) CheckCorrection(slot, index, ref state, now, inView);
                    States[index] = state;
                }
            }

            // Attacks whose attacker this client can see in its view.
            for (int i = 0; i < Attacks.Length; i++)
            {
                int2 attack = Attacks[i];
                int index = NetIdAllocator.IndexOf(attack.x);
                if (!BitSet.Get(Allowed, index)) continue;
                int slot = Input.SlotOfIndex(index);
                if (slot < 0 || !InView(view, Input.Positions[slot])) continue;
                AttackOut.Add(new AttackEvent { AttackerIndex = index, TargetId = attack.y });
            }

            FlushLeaves();
            FlushEnters();
            FlushMoveOrders();
            FlushHealths();
            FlushCorrections();
            FlushAttacks();

            for (int i = 0; i < Enters.Length; i++)
            {
                int index = NetIdAllocator.IndexOf(Enters[i].Id);
                PredictState state = States[index];
                state.Fresh = 0;
                States[index] = state;
            }
        }

        /// <summary>The state both sides set up for an entering unit: anchored at its position, resuming on its route.</summary>
        private void EnterState(ref PredictState state, int index, in EnterUnit entry, float now)
        {
            float2 anchor = new float2(Quantise.Decode(entry.X), Quantise.Decode(entry.Y));
            int first = RouteCount[index] > 0 ? RouteStart[index] : -1;
            int resume = first >= 0 ? MovementPrediction.ProjectedResume(Waypoints, first, RouteCount[index], 0, anchor) : 0;
            state.Anchored = 1;
            state.Anchor = anchor;
            state.AnchorTime = now;
            state.Index = math.max(0, resume - 1);
            state.LastPredicted = anchor;
            state.Speed = state.FullSpeed;
        }

        private static bool InView(int4 view, float2 p) => p.x >= view.x && p.y >= view.y && p.x < view.z + 1 && p.y < view.w + 1;

        private void CheckCorrection(int slot, int index, ref PredictState state, float now, bool inView)
        {
            float2 actual = Input.Positions[slot];
            int first = RouteCount[index] > 0 ? RouteStart[index] : 0;
            int count = RouteCount[index];
            float2 predicted = MovementPrediction.Predict(ref state, Waypoints, first, count, now);
            state.LastPredicted = predicted;
            float elapsed = now - state.LastCheck;
            float measured = elapsed > 0f ? math.distance(actual, state.LastActual) / elapsed : state.Speed;
            state.LastActual = actual;
            state.LastCheck = now;
            float error = math.distance(actual, predicted);
            if (error <= (inView ? Config.CorrectionThreshold : Config.OffscreenThreshold)) return;

            int resume = MovementPrediction.ProjectedResume(Waypoints, first, count, state.Index, actual);
            int2 delta = math.clamp((int2)math.round((actual - predicted) * Config.DeltaScale), short.MinValue, short.MaxValue);
            var unit = new CorrectionUnit
            {
                Index = index,
                DX = (short)delta.x,
                DY = (short)delta.y,
                Resume = resume,
                Speed = state.FullSpeed > 0f ? (byte)math.clamp((int)math.round(measured / state.FullSpeed * 64f), 0, 255) : (byte)0,
            };
            Corrections.Add(unit);
            MovementPrediction.ApplyCorrection(ref state, Waypoints, first, count, now, unit, Config.DeltaScale);
        }

        /// <summary>Appends a unit's route (tiles) to a message's list; a unit with none gets its own tile.</summary>
        private int AppendRoute(int index, float2 position, NativeList<int2> into)
        {
            int count = RouteCount[index];
            if (count <= 0)
            {
                into.Add((int2)math.round(position));
                return 1;
            }
            int first = RouteStart[index];
            for (int i = 0; i < count; i++) into.Add((int2)Waypoints[first + i]);
            return count;
        }

        private byte HealthByte(int slot) =>
            (byte)math.round(math.saturate(Input.Health[slot] / math.max(1f, Input.MaxHealth[slot])) * 100f);

        private void SortLeaves()
        {
            // Insertion sort: the list is short and must be ascending for the delta coding.
            for (int i = 1; i < Leaves.Length; i++)
            {
                LeaveUnit item = Leaves[i];
                int j = i - 1;
                while (j >= 0 && Leaves[j].Index > item.Index) { Leaves[j + 1] = Leaves[j]; j--; }
                Leaves[j + 1] = item;
            }
            // Enters are diffed in ascending index order already; corrections, health and orders come
            // from the ascending bitset walk.
        }

        private void FlushLeaves()
        {
            var sink = new NativeSink(Reliable);
            for (int start = 0; start < Leaves.Length; start += 4096)
            {
                int n = math.min(4096, Leaves.Length - start);
                int before = sink.Position;
                Messages.EncodeLeave(ref sink, Leaves.AsArray().GetSubArray(start, n));
                ReliableSizes.Add(sink.Position - before);
            }
        }

        private void FlushEnters()
        {
            if (Enters.Length == 0) return;
            NativeArray<int2> tiles = EnterTiles.AsArray();
            var sink = new NativeSink(Reliable);
            int index = 0;
            while (index < Enters.Length)
            {
                int start = index, size = 4, previous = 0;
                while (index < Enters.Length)
                {
                    int cost = Messages.EnterUnitSize(tiles, Enters[index], previous);
                    if (index > start && size + cost > WireLimits.ReliableChunk) break;
                    size += cost;
                    previous = NetIdAllocator.IndexOf(Enters[index].Id);
                    index++;
                }
                int before = sink.Position;
                Messages.EncodeEnter(ref sink, tiles, Enters.AsArray().GetSubArray(start, index - start));
                ReliableSizes.Add(sink.Position - before);
            }
        }

        private void FlushMoveOrders()
        {
            if (MoveOrders.Length == 0) return;
            NativeArray<int2> tiles = MoveTiles.AsArray();
            var sink = new NativeSink(Reliable);
            int index = 0;
            while (index < MoveOrders.Length)
            {
                int start = index, size = 8, previous = 0;
                while (index < MoveOrders.Length)
                {
                    RouteUnit u = MoveOrders[index];
                    int cost = VarInt.Size((uint)(u.Index - previous)) + Messages.RouteSize(tiles, u.First, u.Count);
                    if (index > start && size + cost > WireLimits.ReliableChunk) break;
                    size += cost;
                    previous = u.Index;
                    index++;
                }
                int before = sink.Position;
                Messages.EncodeMoveOrder(ref sink, Tick, tiles, MoveOrders.AsArray().GetSubArray(start, index - start));
                ReliableSizes.Add(sink.Position - before);
            }
        }

        private void FlushHealths()
        {
            var sink = new NativeSink(Reliable);
            for (int start = 0; start < Healths.Length; start += 8192)
            {
                int n = math.min(8192, Healths.Length - start);
                int before = sink.Position;
                Messages.EncodeHealth(ref sink, Healths.AsArray().GetSubArray(start, n));
                ReliableSizes.Add(sink.Position - before);
            }
        }

        /// <summary>Corrections are unreliable, so each message must fit one datagram.</summary>
        private void FlushCorrections()
        {
            if (Corrections.Length == 0) return;
            var sink = new NativeSink(Unreliable);
            int index = 0;
            while (index < Corrections.Length)
            {
                int start = index, size = 1 + VarInt.Size((uint)Tick) + 1 + 3, previous = 0;
                while (index < Corrections.Length)
                {
                    int cost = Messages.CorrectionUnitSize(Corrections[index], previous);
                    if (index > start && size + cost > WireLimits.CorrectionMessageLimit) break;
                    size += cost;
                    previous = Corrections[index].Index;
                    index++;
                }
                int before = sink.Position;
                Messages.EncodeCorrection(ref sink, Tick, Config.DeltaScale, Corrections.AsArray().GetSubArray(start, index - start));
                UnreliableSizes.Add(sink.Position - before);
            }
        }

        private void FlushAttacks()
        {
            var sink = new NativeSink(Unreliable);
            for (int start = 0; start < AttackOut.Length; start += 100) // ~10 bytes each, well inside one datagram
            {
                int n = math.min(100, AttackOut.Length - start);
                int before = sink.Position;
                Messages.EncodeAttack(ref sink, AttackOut.AsArray().GetSubArray(start, n));
                UnreliableSizes.Add(sink.Position - before);
            }
        }
    }
}
