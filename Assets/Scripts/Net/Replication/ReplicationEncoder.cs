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
        /// <summary>Prediction error (tiles) that triggers a correction in view.</summary>
        public float CorrectionThreshold;
        /// <summary>Ticks between correction passes in view.</summary>
        public int CorrectionInterval;
        /// <summary>Correction quantisation steps per tile.</summary>
        public byte DeltaScale;
        /// <summary>Prediction error (tiles) that triggers a correction outside the view.</summary>
        public float OffscreenThreshold;
        /// <summary>Ticks between correction passes outside the view.</summary>
        public int OffscreenInterval;
        /// <summary>Seconds per tick: message time is <c>tick * Dt</c>.</summary>
        public float Dt;

        /// <summary>Builds the encoder tuning from the loaded config.</summary>
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
    /// camera view, coarser outside it. Attack events go to clients whose view holds the attacker; the target is
    /// named only when the client may know it (see <see cref="AttackTarget"/>), else it is sent as 0. A building's
    /// attack (v0.13, <see cref="BuildingAttack"/>) goes only to a client that may know the building: it owns it, or its
    /// grid sees it now (the rule <see cref="BuildingInterest"/> follows).
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
        // The shared pass (EncodeMaskJob): each unit's health byte and route generation at the last pass, and its masks.
        private NativeArray<byte> lastHealth;
        private NativeArray<int> lastGeneration;
        private NativeArray<ulong> changed, turn;
        private int pass;
        private JobHandle lastEncode;
        private bool disposed;

        public ReplicationEncoder(in EncoderConfig config, InterestSets interests, RouteStore routes, int idCapacity, Allocator allocator)
        {
            this.config = config;
            this.interests = interests;
            this.routes = routes;
            buffers = new ClientBuffers[interests.Clients];
            for (int c = 0; c < buffers.Length; c++) buffers[c] = new ClientBuffers(idCapacity, allocator);
            lastHealth = new NativeArray<byte>(idCapacity, allocator);
            lastGeneration = new NativeArray<int>(idCapacity, allocator);
            changed = new NativeArray<ulong>(BitSet.Words(idCapacity), allocator);
            turn = new NativeArray<ulong>(BitSet.Words(idCapacity), allocator);
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
            buffers[client].Pass = -1;
        }

        /// <summary>Schedules every client's encode after <paramref name="dependency"/> (the interest build).</summary>
        public JobHandle Schedule(int tick, in ReplicationInput input, NativeArray<int2> attacks, bool[] active, JobHandle dependency) =>
            Schedule(tick, input, attacks, default, active, dependency);

        /// <summary>
        /// As above, with what each attack's building target is (parallel to <paramref name="attacks"/>): a
        /// building target is named only to its owner and to clients whose grid sees it. Without
        /// <paramref name="targets"/>, only unit targets the client may know are named.
        /// </summary>
        public JobHandle Schedule(int tick, in ReplicationInput input, NativeArray<int2> attacks, NativeArray<AttackTarget> targets, bool[] active, JobHandle dependency) =>
            Schedule(tick, input, attacks, targets, default, active, dependency);

        /// <summary>As above, with this tick's building attacks (v0.13; may be default for none).</summary>
        public JobHandle Schedule(int tick, in ReplicationInput input, NativeArray<int2> attacks, NativeArray<AttackTarget> targets, NativeArray<BuildingAttack> buildingAttacks, bool[] active, JobHandle dependency)
        {
            var handles = new NativeArray<JobHandle>(buffers.Length, Allocator.Temp);
            ScheduleEach(tick, input, attacks, targets, buildingAttacks, active, dependency, handles);
            JobHandle all = JobHandle.CombineDependencies(handles);
            handles.Dispose();
            return all;
        }

        /// <summary>
        /// As above, with each client's own handle in <paramref name="handles"/> (one per client; an inactive client's is
        /// the shared pass's), so a caller can send a client's bytes as soon as that client is encoded.
        /// One shared pass runs first (<see cref="EncodeMaskJob"/>); a client that was not encoded on the call before
        /// this one walks every unit it may know instead, because the masks only cover one call's changes.
        /// </summary>
        public void ScheduleEach(int tick, in ReplicationInput input, NativeArray<int2> attacks, NativeArray<AttackTarget> targets, NativeArray<BuildingAttack> buildingAttacks, bool[] active, JobHandle dependency, NativeArray<JobHandle> handles)
        {
            JobHandle masks = new EncodeMaskJob
            {
                Config = config, Tick = tick, Input = input, RouteGeneration = routes.Generation,
                LastHealth = lastHealth, LastGeneration = lastGeneration, Changed = changed, Turn = turn,
            }.Schedule(changed.Length, 64, JobHandle.CombineDependencies(dependency, lastEncode));
            for (int c = 0; c < buffers.Length; c++)
            {
                if (active != null && !active[c]) { handles[c] = masks; continue; }
                EncodeClientJob job = Job(c, tick, input, attacks, targets, buildingAttacks);
                job.Masked = buffers[c].Pass == pass;
                buffers[c].Pass = pass + 1;
                handles[c] = job.Schedule(masks);
            }
            pass++;
            lastEncode = JobHandle.CombineDependencies(masks, JobHandle.CombineDependencies(handles));
            JobHandle.ScheduleBatchedJobs();
        }

        /// <summary>Encodes every client now (the interest sets must be built for this tick).</summary>
        public void EncodeAll(int tick, in ReplicationInput input, NativeArray<int2> attacks) =>
            Schedule(tick, input, attacks, null, default).Complete();

        /// <summary>Encodes every client now, with each attack's building target (see <see cref="AttackTarget"/>).</summary>
        public void EncodeAll(int tick, in ReplicationInput input, NativeArray<int2> attacks, NativeArray<AttackTarget> targets) =>
            Schedule(tick, input, attacks, targets, null, default).Complete();

        /// <summary>Encodes every client now, with unit and building attacks.</summary>
        public void EncodeAll(int tick, in ReplicationInput input, NativeArray<int2> attacks, NativeArray<AttackTarget> targets, NativeArray<BuildingAttack> buildingAttacks) =>
            Schedule(tick, input, attacks, targets, buildingAttacks, null, default).Complete();

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

        private EncodeClientJob Job(int client, int tick, in ReplicationInput input, NativeArray<int2> attacks, NativeArray<AttackTarget> targets, NativeArray<BuildingAttack> buildingAttacks)
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
                // Deferred: the interest build may still be running when this is scheduled (the recorder's lane).
                Entered = interests.Entered(client).AsDeferredJobArray(),
                Left = interests.Left(client).AsDeferredJobArray(),
                Attacks = attacks,
                AttackTargets = targets.IsCreated ? targets : b.NoTargets,
                BuildingAttacks = buildingAttacks.IsCreated ? buildingAttacks : b.NoBuildingAttacks,
                Owner = interests.OwnerOf(client),
                Grid = interests.GridOf(client),
                View = b.View,
                Changed = changed,
                Turn = turn,
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

        /// <summary>Waits for the last scheduled encode, then frees the native buffers.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lastEncode.Complete();
            foreach (ClientBuffers b in buffers) b.Dispose();
            lastHealth.Dispose(); lastGeneration.Dispose(); changed.Dispose(); turn.Dispose();
        }

        private sealed class ClientBuffers : IDisposable
        {
            /// <summary>Per-unit state the client is known to predict.</summary>
            public NativeArray<PredictState> States;
            public NativeArray<int4> View;
            /// <summary>The encoder's call count after this client's last encode, or -1: equal to the count now when it was encoded on the call before.</summary>
            public int Pass = -1;
            /// <summary>Encoded bytes for the reliable and unreliable channels.</summary>
            public NativeList<byte> Reliable, Unreliable;
            /// <summary>Sizes of the whole messages in each channel, in order.</summary>
            public NativeList<int> ReliableSizes, UnreliableSizes;
            /// <summary>Scratch: units entering.</summary>
            public NativeList<EnterUnit> Enters;
            public NativeList<int2> EnterTiles, MoveTiles;
            /// <summary>Scratch: units with a new route.</summary>
            public NativeList<RouteUnit> MoveOrders;
            /// <summary>Scratch: units leaving.</summary>
            public NativeList<LeaveUnit> Leaves;
            /// <summary>Scratch: units whose health changed.</summary>
            public NativeList<HealthUnit> Healths;
            /// <summary>Scratch: units to correct.</summary>
            public NativeList<CorrectionUnit> Corrections;
            /// <summary>Scratch: attacks to show.</summary>
            public NativeList<AttackEvent> Attacks;
            /// <summary>An empty target list, for encodes given none.</summary>
            public NativeArray<AttackTarget> NoTargets;
            /// <summary>An empty building attack list, for encodes given none.</summary>
            public NativeArray<BuildingAttack> NoBuildingAttacks;

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
                NoTargets = new NativeArray<AttackTarget>(0, allocator);
                NoBuildingAttacks = new NativeArray<BuildingAttack>(0, allocator);
            }

            /// <summary>Frees the native buffers.</summary>
            public void Dispose()
            {
                States.Dispose(); View.Dispose(); Reliable.Dispose(); Unreliable.Dispose(); ReliableSizes.Dispose();
                UnreliableSizes.Dispose(); Enters.Dispose(); EnterTiles.Dispose(); MoveOrders.Dispose(); MoveTiles.Dispose();
                Leaves.Dispose(); Healths.Dispose(); Corrections.Dispose(); Attacks.Dispose(); NoTargets.Dispose(); NoBuildingAttacks.Dispose();
            }
        }
    }

    /// <summary>
    /// One pass per encode call, shared by every client: which units have a new health byte or route generation since the
    /// call before (<see cref="Changed"/>), and which may be due a correction check this tick (<see cref="Turn"/>: either
    /// interval's turn; the client's view picks the interval). A client encoded on both calls already holds every other
    /// unit's health and route, so it visits only these units instead of every unit it may know (at 100k units most of the
    /// encode's worker time went on units with nothing to send). Deterministic float mode: the health byte must be the one
    /// <see cref="EncodeClientJob"/> computes.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic)]
    public struct EncodeMaskJob : IJobParallelFor
    {
        /// <summary>Encoder tuning (the two correction intervals).</summary>
        public EncoderConfig Config;
        /// <summary>The tick being encoded.</summary>
        public int Tick;
        /// <summary>The settled world.</summary>
        public ReplicationInput Input;
        [ReadOnly] public NativeArray<int> RouteGeneration;
        /// <summary>Each unit's health byte and route generation at the last call, by id index (a word of indices per batch item).</summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> LastHealth;
        [NativeDisableParallelForRestriction] public NativeArray<int> LastGeneration;
        /// <summary>Output bitsets over id indices, one word per batch item.</summary>
        public NativeArray<ulong> Changed, Turn;

        /// <summary>Fills one word of both masks.</summary>
        public void Execute(int word)
        {
            int first = word * 64;
            ulong due = Every(first, Config.CorrectionInterval) | Every(first, Config.OffscreenInterval), differs = 0;
            for (int bit = 0; bit < 64; bit++)
            {
                int index = first + bit;
                int slot = Input.SlotOfIndex(index);
                if (slot < 0) continue;
                byte health = EncodeClientJob.HealthByte(Input.Health[slot], Input.MaxHealth[slot]);
                int generation = RouteGeneration[index];
                if (health == LastHealth[index] && generation == LastGeneration[index]) continue;
                LastHealth[index] = health;
                LastGeneration[index] = generation;
                differs |= 1UL << bit;
            }
            Changed[word] = differs;
            Turn[word] = due;
        }

        /// <summary>The bits of the word starting at id index <paramref name="first"/> whose <c>(Tick + index) % interval</c> is 0.</summary>
        private ulong Every(int first, int interval)
        {
            int offset = (Tick + first) % interval; // negative for a negative tick: the next multiple is then -offset bits on
            ulong bits = 0;
            for (int bit = offset > 0 ? interval - offset : -offset; bit < 64; bit += interval) bits |= 1UL << bit;
            return bits;
        }
    }

    /// <summary>One client's encode for one tick. Deterministic float mode, matching the client's prediction job.</summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic)]
    public struct EncodeClientJob : IJob
    {
        /// <summary>Encoder tuning.</summary>
        public EncoderConfig Config;
        /// <summary>The tick being encoded.</summary>
        public int Tick;
        /// <summary>The settled world the client may see.</summary>
        public ReplicationInput Input;
        [ReadOnly] public NativeArray<float2> Waypoints;
        [ReadOnly] public NativeArray<int> RouteStart, RouteCount, RouteGeneration;
        [ReadOnly] public NativeArray<ulong> Allowed;
        [ReadOnly] public NativeArray<int> Entered, Left;
        [ReadOnly] public NativeArray<int2> Attacks;
        /// <summary>What each attack's building target is, parallel to <see cref="Attacks"/> (may be empty).</summary>
        [ReadOnly] public NativeArray<AttackTarget> AttackTargets;
        /// <summary>This tick's building attacks (v0.13), each with what its attacker and target are.</summary>
        [ReadOnly] public NativeArray<BuildingAttack> BuildingAttacks;
        /// <summary>The client's owner id and fog grid (as its interest set uses them).</summary>
        public int Owner, Grid;
        [ReadOnly] public NativeArray<int4> View;
        /// <summary>This call's masks from <see cref="EncodeMaskJob"/>.</summary>
        [ReadOnly] public NativeArray<ulong> Changed, Turn;
        /// <summary>True when the client was encoded on the call before, so <see cref="Changed"/> covers everything new to it.</summary>
        public bool Masked;
        public NativeArray<PredictState> States;
        /// <summary>Encoded bytes for the reliable and unreliable channels.</summary>
        public NativeList<byte> Reliable, Unreliable;
        /// <summary>Sizes of the whole messages in each channel, in order.</summary>
        public NativeList<int> ReliableSizes, UnreliableSizes;
        /// <summary>Scratch: units entering.</summary>
        public NativeList<EnterUnit> Enters;
        public NativeList<int2> EnterTiles, MoveTiles;
        /// <summary>Scratch: units with a new route.</summary>
        public NativeList<RouteUnit> MoveOrders;
        /// <summary>Scratch: units leaving.</summary>
        public NativeList<LeaveUnit> Leaves;
        /// <summary>Scratch: units whose health changed.</summary>
        public NativeList<HealthUnit> Healths;
        /// <summary>Scratch: units to correct.</summary>
        public NativeList<CorrectionUnit> Corrections;
        /// <summary>Output: attacks to send this client.</summary>
        public NativeList<AttackEvent> AttackOut;

        /// <summary>Runs the job.</summary>
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
                LeaveReason reason = alive ? LeaveReason.LeftView
                    : (uint)index < (uint)Input.LastHitKind.Length ? LeaveReasons.Death(Input.LastHitKind[index]) : LeaveReason.Died;
                Leaves.Add(new LeaveUnit { Index = index, Reason = reason });
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

            // Everything the client may know: health, new routes and corrections. Only the units the shared pass marked
            // are visited: the client holds every other unit's health and route from the call before.
            for (int w = 0; w < Allowed.Length; w++)
            {
                ulong differs = Masked ? Changed[w] : ulong.MaxValue;
                ulong bits = Allowed[w] & (differs | Turn[w]);
                while (bits != 0)
                {
                    int bit = math.tzcnt(bits);
                    bits &= bits - 1;
                    int index = w * 64 + bit;
                    int slot = Input.SlotOfIndex(index);
                    if (slot < 0) continue;
                    bool inView = InView(view, Input.Positions[slot]);
                    bool due = (Tick + index) % (inView ? Config.CorrectionInterval : Config.OffscreenInterval) == 0;
                    if (!due && (differs & (1UL << bit)) == 0) continue; // the other interval's turn, and nothing new
                    PredictState state = States[index];
                    if (state.Id != Input.IdOf[slot] || state.Fresh != 0) continue;

                    // The state is written back only when this tick changed it: most units change nothing on most ticks, and
                    // a write to every unit's state dirtied the whole array for every client each tick.
                    bool dirty = false;
                    byte health = HealthByte(slot);
                    if (health != state.LastHealth)
                    {
                        Healths.Add(new HealthUnit { Index = index, Health = health });
                        state.LastHealth = health;
                        dirty = true;
                    }
                    bool routed = RouteGeneration[index] != state.RouteGeneration;
                    if (routed)
                    {
                        dirty = true;
                        MoveOrders.Add(new RouteUnit { Index = index, First = MoveTiles.Length, Count = AppendRoute(index, Input.Positions[slot], MoveTiles) });
                        state.RouteGeneration = RouteGeneration[index];
                        MovementPrediction.StartRoute(ref state, Waypoints.Length > 0 && RouteCount[index] > 0 ? Waypoints[RouteStart[index]] : Input.Positions[slot], now);
                    }
                    // Not on a new route's tick: the correction goes unreliably and the route reliably, so it could arrive first and be
                    // applied to the old route (bug review 2026-11-05). The new route starts where the unit is; the next check corrects it.
                    if (!routed && due)
                    {
                        CheckCorrection(slot, index, ref state, now, inView);
                        dirty = true;
                    }
                    if (dirty) States[index] = state;
                }
            }

            // Attacks whose attacker this client can see in its view.
            for (int i = 0; i < Attacks.Length; i++)
            {
                AttackTarget target = i < AttackTargets.Length ? AttackTargets[i] : default;
                if (InterestRules.UnitAttack(Input, Allowed, view, Attacks[i], target, Owner, Grid, out AttackEvent shown)) AttackOut.Add(shown);
            }

            // Building attacks (v0.13): only from a building the client may know, in its view (its own buildings
            // wherever the camera is: the owner's turret status must not depend on it); the target as above.
            for (int i = 0; i < BuildingAttacks.Length; i++)
                if (InterestRules.BuildingAttack(Input, Allowed, view, BuildingAttacks[i], Owner, Grid, out AttackEvent shown)) AttackOut.Add(shown);

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

        private static bool InView(int4 view, float2 p) => InterestRules.InView(view, p);

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

        /// <summary>Appends a unit's route (in <see cref="RouteWire"/> half tiles) to a message's list; a unit with none gets its own position.</summary>
        private int AppendRoute(int index, float2 position, NativeList<int2> into)
        {
            int count = RouteCount[index];
            if (count <= 0)
            {
                into.Add(RouteWire.Encode(position));
                return 1;
            }
            int first = RouteStart[index];
            for (int i = 0; i < count; i++) into.Add(RouteWire.Encode(Waypoints[first + i]));
            return count;
        }

        private byte HealthByte(int slot) => HealthByte(Input.Health[slot], Input.MaxHealth[slot]);

        /// <summary>A unit's health as the wire carries it: a percentage, never 0 while it lives.</summary>
        public static byte HealthByte(float health, float maxHealth)
        {
            float fraction = math.saturate(health / math.max(1f, maxHealth));
            byte percent = (byte)math.round(fraction * 100f);
            return percent == 0 && fraction > 0f ? (byte)1 : percent; // 0 is reserved for dead, never a living sliver
        }

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
            NativeArray<RouteUnit> units = MoveOrders.AsArray();
            var copies = new Messages.RouteCopies(Allocator.Temp);
            for (int start = 0; start < units.Length;)
            {
                int end = Messages.MoveOrderChunkEnd(tiles, units, start, ref copies);
                int before = sink.Position;
                Messages.EncodeMoveOrder(ref sink, Tick, tiles, units.GetSubArray(start, end - start));
                ReliableSizes.Add(sink.Position - before);
                start = end;
            }
            copies.Dispose();
        }

        private void FlushHealths()
        {
            var sink = new NativeSink(Reliable);
            NativeArray<HealthUnit> units = Healths.AsArray();
            for (int start = 0; start < units.Length;)
            {
                int end = Messages.HealthChunkEnd(units, start);
                int before = sink.Position;
                Messages.EncodeHealth(ref sink, units.GetSubArray(start, end - start));
                ReliableSizes.Add(sink.Position - before);
                start = end;
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

    /// <summary>
    /// The building an attack hit, as the encoder needs it to decide who may know it (see
    /// <see cref="ReplicationService"/>). <see cref="Id"/> is 0 when the target is not a building.
    /// </summary>
    public struct AttackTarget
    {
        public int Id, OwnerId;
        public float2 Position;
        public byte Hidden;
    }

    /// <summary>
    /// One building's attack (v0.13), as the encoder needs it: the attacking building (to decide who may know it) and
    /// the target (<see cref="Target"/> is filled when the target is a building, as for unit attacks).
    /// </summary>
    public struct BuildingAttack
    {
        public AttackTarget Attacker;
        public int TargetId;
        public AttackTarget Target;
    }
}
