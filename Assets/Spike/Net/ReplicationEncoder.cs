using System;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using ReadOnly = Unity.Collections.ReadOnlyAttribute;

namespace WAR2D.Spike
{
    /// <summary>Tuning for <see cref="ReplicationEncoder"/>.</summary>
    public struct EncoderConfig
    {
        /// <summary>Tiles the predicted position may be off by before a correction is sent.</summary>
        public float CorrectionThreshold;

        /// <summary>Ticks between two correction checks of the same unit; the plan's two.</summary>
        public int CorrectionInterval;

        /// <summary>Move speed the client predicts along a route with.</summary>
        public float Speed;

        /// <summary>Health a full bar is 100 % of.</summary>
        public float MaxHealth;

        /// <summary>
        /// 0 sends corrections as absolute 1/16-tile positions. Otherwise they are deltas against the
        /// client's own prediction in 1/DeltaScale tile (the bandwidth ladder's coarser quantisation).
        /// </summary>
        public byte DeltaScale;

        /// <summary>
        /// Payload bytes a second each client may receive; 0 is unlimited. When set, the tick's
        /// reliable messages are paid for first and the corrections that still fit go out largest
        /// error first, from a bucket that holds at most one second of budget. A correction that does
        /// not fit is retried at the unit's next check, by then with a larger error.
        /// </summary>
        public int BudgetBytesPerSecond;

        /// <summary>
        /// Half the client's camera view in tiles (x, y); zero disables the view tier. Units outside
        /// the view are checked every <see cref="OffscreenInterval"/> ticks against
        /// <see cref="OffscreenThreshold"/>: the minimap needs where they are, not where they are to
        /// 1/4 tile.
        /// </summary>
        public float2 ViewHalfExtents;

        /// <summary>
        /// Corrections carry the unit's measured speed (one byte), and the client predicts at it until
        /// the next order or correction: a unit that stopped to fight holds, one in a crowd crawls.
        /// </summary>
        public bool SendSpeed;

        /// <summary>
        /// A correction resumes at the waypoint after the route segment nearest the unit's real
        /// position, instead of after the leg the prediction had reached (which may be behind the unit
        /// or around a corner ahead of it).
        /// </summary>
        public bool ProjectResume;

        /// <summary>Tiles an off-screen unit may be off before a correction is sent.</summary>
        public float OffscreenThreshold;

        /// <summary>Ticks between two checks of an off-screen unit.</summary>
        public int OffscreenInterval;

        /// <summary>The plan's defaults: 0.25 tiles, every other tick, 5 tiles/s, 100 health.</summary>
        public static EncoderConfig Defaults => new EncoderConfig
        {
            CorrectionThreshold = 0.25f,
            CorrectionInterval = 2,
            Speed = SpikeSimRules.Speed,
            MaxHealth = 100f,
            OffscreenThreshold = 2f,
            OffscreenInterval = 20,
        };
    }

    /// <summary>
    /// The server half of the v0.5 hybrid movement model, and the spike's byte counter. For one
    /// client it turns the tick's state into the seven message types.
    ///
    /// <para><b>What it can see.</b> The encoder's unit loop is driven entirely by the client's
    /// <see cref="InterestSets"/> allowed bitset: an id that is not in it is never looked up, never
    /// encoded, and cannot leak. The only world data it reads is the SoA it was bound to, through the
    /// sim's id-to-slot map of an allowed id. The v0.5 leak tests have their prototype here.</para>
    ///
    /// <para><b>Prediction and corrections.</b> A unit the client knows carries a route (the traced
    /// flow path) and a leg to walk. The client evaluates <see cref="PathFollower.Evaluate"/> over the
    /// route at speed; the server does exactly the same for the same unit, and when the simulation's
    /// position is more than <see cref="EncoderConfig.CorrectionThreshold"/> tiles off the prediction,
    /// it sends the corrected point and the waypoint to resume from, at most once every
    /// <see cref="EncoderConfig.CorrectionInterval"/> ticks per unit. After a correction the client
    /// predicts straight from the corrected point toward the resumed waypoint, and so does the
    /// server.</para>
    ///
    /// <para><b>Cadence.</b> Each client's encode is a Burst job (<see cref="EncodeClientJob"/>) that
    /// writes into its own native byte lists. <see cref="EncodeAll"/> runs every client's job in
    /// parallel after the tick's simulation completes; <see cref="CopyOut"/> then hands one client's
    /// bytes to Mirror's managed writers. <see cref="Encode"/> does both for one client, in place, for
    /// tests. This is the plan's fallback for an encoder over its 4 ms budget.</para>
    /// </summary>
    public sealed class ReplicationEncoder : IDisposable
    {
        internal const int TypeCount = 7;

        private readonly EncoderConfig config;
        private readonly InterestSets interests;
        private readonly RouteStore routes;
        private readonly int clients, idCapacity, capacity;
        private readonly ClientBuffers[] buffers;
        private readonly NativeArray<int> noFog;
        private byte[] copyBuffer = new byte[64 * 1024];

        private NativeArray<float2> positions;
        private NativeArray<float> health;
        private NativeArray<byte> player;
        private NativeArray<int> ids;
        private NativeArray<int> indexOfId;
        private FogSystem fog;
        private int lastClient;
        private bool disposed;

        public ReplicationEncoder(in EncoderConfig config, InterestSets interests, RouteStore routes,
            int capacity, int idCapacity, Allocator allocator)
        {
            this.config = config;
            this.interests = interests;
            this.routes = routes;
            this.capacity = math.max(1, capacity);
            this.idCapacity = math.max(1, idCapacity);
            clients = interests.Clients;
            buffers = new ClientBuffers[clients];
            for (int client = 0; client < clients; client++)
                buffers[client] = new ClientBuffers(this.idCapacity, config.BudgetBytesPerSecond, allocator);
            noFog = new NativeArray<int>(0, allocator);
        }

        /// <summary>Clients this encoder serves.</summary>
        public int Clients => clients;

        /// <summary>Binds the unit SoA the encoder reads, including the sim's id-to-slot map.</summary>
        public void SetUnits(NativeArray<float2> positions, NativeArray<float> health, NativeArray<byte> player,
            NativeArray<int> ids, NativeArray<int> indexOfId, int capacity)
        {
            this.positions = positions;
            this.health = health;
            this.player = player;
            this.ids = ids;
            this.indexOfId = indexOfId;
            interests.SetUnits(positions, player, health, ids, capacity);
        }

        /// <summary>Binds the fog whose changed-cell lists become FogDelta messages.</summary>
        public void SetFog(FogSystem fog) => this.fog = fog;

        /// <summary>Centres a client's camera view (tiles); only used while the view tier is on.</summary>
        public void SetView(int client, float2 centre)
        {
            ClientScalars scalars = buffers[client].Scalars[0];
            scalars.ViewCentre = centre;
            buffers[client].Scalars[0] = scalars;
        }

        /// <summary>Payload bytes the client was sent for one message type over the whole run.</summary>
        public long Bytes(int client, SpikeMessageType type) => buffers[client].Totals[(int)type];

        /// <summary>Units (or explosions, or cells) one message type carried for the client.</summary>
        public long Count(int client, SpikeMessageType type) => buffers[client].Totals[TypeCount + 1 + (int)type];

        /// <summary>Times a slot changed hands underneath a client's prediction (diagnostic only).</summary>
        public long Reseeds
        {
            get
            {
                long total = 0;
                for (int client = 0; client < clients; client++) total += buffers[client].Scalars[0].Reseeds;
                return total;
            }
        }

        /// <summary>
        /// The reliable messages the last encoded client was sent, by payload size, in order. The
        /// bench feeds these to <see cref="SpikeWire.WireBytes"/> so Mirror's batching and KCP's
        /// segmentation are counted message by message rather than guessed from the total.
        /// </summary>
        public NativeArray<int> ReliableMessageSizes => buffers[lastClient].ReliableSizes.AsArray();

        /// <summary>The unreliable messages the last encoded client was sent, by payload size.</summary>
        public NativeArray<int> UnreliableMessageSizes => buffers[lastClient].UnreliableSizes.AsArray();

        /// <summary>The clock the client's last encode used, in seconds.</summary>
        public float LastNow(int client) => buffers[client].Scalars[0].Now;

        /// <summary>The position the client predicts for a unit after the last encode.</summary>
        public float2 PredictedPosition(int client, int id) => buffers[client].States[id].LastPredicted;

        /// <summary>True when the client currently predicts from a corrected point rather than a waypoint.</summary>
        public bool PredictsFromCorrection(int client, int id) => buffers[client].States[id].Anchored != 0;

        /// <summary>
        /// The route leg the client's last prediction walked: the slice it evaluates and the time it
        /// started from. False when the last prediction came from a correction anchor, which is not a
        /// plain route evaluation. This is what the prediction test feeds <see cref="PathFollowerJob"/> with.
        /// </summary>
        public bool PredictedLeg(int client, int id, out int first, out int count, out float startTime)
        {
            Prediction prediction = buffers[client].Scalars[0].LastPrediction;
            first = prediction.First;
            count = prediction.Count;
            startTime = prediction.StartTime;
            return prediction.Id == id && prediction.Anchored == 0;
        }

        /// <summary>The error every client's predictions showed, all units (see <see cref="EncodeClientJob"/>).</summary>
        public ErrorHistogram AllError => Aggregate(0);

        /// <summary>The error of units inside each client's view.</summary>
        public ErrorHistogram ViewError => Aggregate(1);

        /// <summary>Diagnostic: the error at every check before any correction (in-view units).</summary>
        public ErrorHistogram RawViewError => Aggregate(2);

        /// <summary>Diagnostic: the same for checks of anchored predictions only (right after a correction).</summary>
        public ErrorHistogram RawAnchoredError => Aggregate(3);

        private ErrorHistogram Aggregate(int which)
        {
            var histogram = new ErrorHistogram();
            for (int client = 0; client < clients; client++)
                histogram.AddCounts(buffers[client].Errors, which * ErrorHistogram.Buckets, ErrorHistogram.Buckets);
            return histogram;
        }

        /// <summary>
        /// Encodes one tick for one client in place and appends its bytes to the two writers (reliable:
        /// the stateful messages; unreliable: the corrections). The tick's
        /// <see cref="InterestSets.Build"/> must have run first.
        /// </summary>
        public void Encode(int client, int tick, NetworkWriter reliable, NetworkWriter unreliable)
        {
            ThrowIfDisposed();
            Job(client, tick).Run();
            CopyOut(client, reliable, unreliable);
        }

        /// <summary>Encodes one tick for every client, in parallel, and waits for them.</summary>
        public void EncodeAll(int tick)
        {
            ThrowIfDisposed();
            var handles = new NativeArray<JobHandle>(clients, Allocator.Temp);
            for (int client = 0; client < clients; client++) handles[client] = Job(client, tick).Schedule();
            JobHandle.CompleteAll(handles);
            handles.Dispose();
        }

        /// <summary>Appends a client's last encoded bytes to Mirror's writers; returns the two sizes.</summary>
        public (int reliable, int unreliable) CopyOut(int client, NetworkWriter reliable, NetworkWriter unreliable)
        {
            lastClient = client;
            ClientBuffers b = buffers[client];
            Append(b.Reliable, reliable);
            Append(b.Unreliable, unreliable);
            return (b.Reliable.Length, b.Unreliable.Length);
        }

        /// <summary>A client's last encoded bytes without copying them (for byte counting).</summary>
        public (int reliable, int unreliable) LastSizes(int client)
        {
            lastClient = client;
            return (buffers[client].Reliable.Length, buffers[client].Unreliable.Length);
        }

        private void Append(NativeList<byte> bytes, NetworkWriter writer)
        {
            int length = bytes.Length;
            if (length == 0) return;
            if (copyBuffer.Length < length) copyBuffer = new byte[math.ceilpow2(length)];
            NativeArray<byte>.Copy(bytes.AsArray(), 0, copyBuffer, 0, length);
            writer.WriteBytes(copyBuffer, 0, length);
        }

        private EncodeClientJob Job(int client, int tick)
        {
            ClientBuffers b = buffers[client];
            NativeArray<int> fogCells = noFog;
            if (fog != null)
            {
                int team = interests.TeamOfClient(client);
                for (int i = 0; i < fog.LastTickTeams; i++)
                {
                    if (fog.LastTickTeam(i) != team) continue;
                    fogCells = fog.Changed(team).AsArray();
                    break;
                }
            }
            return new EncodeClientJob
            {
                Config = config,
                Tick = tick,
                Capacity = capacity,
                IdCapacity = idCapacity,
                Positions = positions,
                Health = health,
                Player = player,
                Ids = ids,
                IndexOfId = indexOfId,
                Waypoints = routes.Waypoints,
                RouteStart = routes.Starts,
                RouteCount = routes.Counts,
                RouteGeneration = routes.Generations,
                Allowed = interests.Allowed(client),
                Entered = interests.Entered(client).AsArray(),
                Left = interests.Left(client).AsArray(),
                FogCells = fogCells,
                B = b.ForJob(),
            };
        }

        /// <summary>Frees every per-client table and scratch list. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int client = 0; client < clients; client++) buffers[client].Dispose();
            noFog.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ReplicationEncoder));
        }
    }

    /// <summary>What one client believes about one unit: its prediction and what it was last sent.</summary>
    internal struct PredictState
    {
        /// <summary>The last position the client was told (the anchor while <see cref="Anchored"/>).</summary>
        public float2 Anchor;

        /// <summary>The position the client's prediction produced at the last check.</summary>
        public float2 LastPredicted;

        /// <summary>When an anchored prediction started, i.e. the tick of the correction.</summary>
        public float AnchorTime;

        /// <summary>When the current route leg started; the client computes it from the same tick.</summary>
        public float StartTime;

        /// <summary>Route waypoint the current leg departs from.</summary>
        public int Index;

        /// <summary>1 while the client predicts from a corrected point rather than from a waypoint.</summary>
        public byte Anchored;

        /// <summary>1 while the client knows the unit.</summary>
        public byte Known;

        /// <summary>1 for the tick the unit entered, so it is not also corrected that tick.</summary>
        public byte Fresh;

        public byte LastHealth;
        public int KnownGeneration;

        /// <summary>The speed the client predicts at, tiles a second (full speed until a speed correction).</summary>
        public float Speed;

        /// <summary>The real position at the last check, and when, for the measured speed.</summary>
        public float2 LastActual;
        public float LastCheck;
    }

    /// <summary>The last prediction evaluated for a client, kept so tests can see its inputs.</summary>
    internal struct Prediction
    {
        public int Id;
        public int First;
        public int Count;
        public float StartTime;
        public byte Anchored;
    }

    /// <summary>A unit whose prediction is off by more than its threshold this tick.</summary>
    internal struct Candidate
    {
        public int Id;
        public float2 Actual;
        public float Error; // negative for an off-screen unit
        public int Weight;
        public byte Speed;
    }

    /// <summary>One client's scalars, in a one-element array so a job can write them back.</summary>
    internal struct ClientScalars
    {
        public float Tokens;
        public float Now;
        public float2 ViewCentre;
        public long Reseeds;
        public Prediction LastPrediction;
    }

    /// <summary>Everything one client's encode owns: its knowledge, its scratch and its output.</summary>
    internal sealed class ClientBuffers : IDisposable
    {
        public NativeArray<PredictState> States;
        public NativeArray<ClientScalars> Scalars;
        public NativeArray<long> Totals;   // bytes [0..7], then counts [8..15], by message type
        public NativeArray<long> Errors;   // two histograms: all units, in-view units
        public NativeList<byte> Reliable, Unreliable;
        public NativeList<int> ReliableSizes, UnreliableSizes;
        public NativeList<RouteUnit> MoveOrders;
        public NativeList<int2> MoveOrderTiles, EnterTiles;
        public NativeList<EnterUnit> Enters;
        public NativeList<LeaveUnit> Leaves;
        public NativeList<HealthUnit> Healths;
        public NativeList<CorrectionUnit> Corrections;
        public NativeList<Candidate> Candidates;
        public NativeList<ushort> ExplosionX, ExplosionY;

        public ClientBuffers(int idCapacity, int budget, Allocator allocator)
        {
            States = new NativeArray<PredictState>(idCapacity, allocator);
            Scalars = new NativeArray<ClientScalars>(1, allocator);
            Scalars[0] = new ClientScalars { Tokens = budget };
            Totals = new NativeArray<long>((ReplicationEncoder.TypeCount + 1) * 2, allocator);
            Errors = new NativeArray<long>(ErrorHistogram.Buckets * 4, allocator);
            Reliable = new NativeList<byte>(64 * 1024, allocator);
            Unreliable = new NativeList<byte>(16 * 1024, allocator);
            ReliableSizes = new NativeList<int>(64, allocator);
            UnreliableSizes = new NativeList<int>(64, allocator);
            MoveOrders = new NativeList<RouteUnit>(256, allocator);
            MoveOrderTiles = new NativeList<int2>(1024, allocator);
            Enters = new NativeList<EnterUnit>(256, allocator);
            EnterTiles = new NativeList<int2>(1024, allocator);
            Leaves = new NativeList<LeaveUnit>(64, allocator);
            Healths = new NativeList<HealthUnit>(256, allocator);
            Corrections = new NativeList<CorrectionUnit>(1024, allocator);
            Candidates = new NativeList<Candidate>(1024, allocator);
            ExplosionX = new NativeList<ushort>(64, allocator);
            ExplosionY = new NativeList<ushort>(64, allocator);
        }

        public ClientJobBuffers ForJob() => new ClientJobBuffers
        {
            States = States, Scalars = Scalars, Totals = Totals, Errors = Errors,
            Reliable = Reliable, Unreliable = Unreliable, ReliableSizes = ReliableSizes, UnreliableSizes = UnreliableSizes,
            MoveOrders = MoveOrders, MoveOrderTiles = MoveOrderTiles, Enters = Enters, EnterTiles = EnterTiles,
            Leaves = Leaves, Healths = Healths, Corrections = Corrections, Candidates = Candidates,
            ExplosionX = ExplosionX, ExplosionY = ExplosionY,
        };

        public void Dispose()
        {
            States.Dispose(); Scalars.Dispose(); Totals.Dispose(); Errors.Dispose();
            Reliable.Dispose(); Unreliable.Dispose(); ReliableSizes.Dispose(); UnreliableSizes.Dispose();
            MoveOrders.Dispose(); MoveOrderTiles.Dispose(); Enters.Dispose(); EnterTiles.Dispose();
            Leaves.Dispose(); Healths.Dispose(); Corrections.Dispose(); Candidates.Dispose();
            ExplosionX.Dispose(); ExplosionY.Dispose();
        }
    }

    /// <summary>The job-side view of <see cref="ClientBuffers"/>.</summary>
    internal struct ClientJobBuffers
    {
        public NativeArray<PredictState> States;
        public NativeArray<ClientScalars> Scalars;
        public NativeArray<long> Totals;
        public NativeArray<long> Errors;
        public NativeList<byte> Reliable, Unreliable;
        public NativeList<int> ReliableSizes, UnreliableSizes;
        public NativeList<RouteUnit> MoveOrders;
        public NativeList<int2> MoveOrderTiles, EnterTiles;
        public NativeList<EnterUnit> Enters;
        public NativeList<LeaveUnit> Leaves;
        public NativeList<HealthUnit> Healths;
        public NativeList<CorrectionUnit> Corrections;
        public NativeList<Candidate> Candidates;
        public NativeList<ushort> ExplosionX, ExplosionY;
    }

    /// <summary>
    /// One client's encode for one tick, Burst compiled. The float mode matches
    /// <see cref="PathFollowerJob"/>'s, so the server's prediction is bit-identical to the client's.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic)]
    internal struct EncodeClientJob : IJob
    {
        public EncoderConfig Config;
        public int Tick, Capacity, IdCapacity;

        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Player;
        [ReadOnly] public NativeArray<int> Ids;
        [ReadOnly] public NativeArray<int> IndexOfId;
        [ReadOnly] public NativeArray<float2> Waypoints;
        [ReadOnly] public NativeArray<int> RouteStart, RouteCount, RouteGeneration;
        [ReadOnly] public NativeArray<ulong> Allowed;
        [ReadOnly] public NativeArray<int> Entered, Left, FogCells;

        public ClientJobBuffers B;

        public void Execute()
        {
            float now = Tick * SpikeSimRules.TickSeconds;
            ClientScalars scalars = B.Scalars[0];
            scalars.Now = now;
            B.Reliable.Clear();
            B.Unreliable.Clear();
            B.ReliableSizes.Clear();
            B.UnreliableSizes.Clear();
            B.MoveOrders.Clear();
            B.MoveOrderTiles.Clear();
            B.Enters.Clear();
            B.EnterTiles.Clear();
            B.Leaves.Clear();
            B.Healths.Clear();
            B.Corrections.Clear();
            B.Candidates.Clear();
            B.ExplosionX.Clear();
            B.ExplosionY.Clear();

            // Departures first. The interest build has already reseeded the client's knowledge, so a
            // unit that died or left vision has no other way to be reported.
            for (int i = 0; i < Left.Length; i++)
            {
                int id = Left[i];
                int slot = SlotOf(id);
                bool valid = (uint)slot < (uint)Capacity && Ids[slot] == id;
                PredictState state = B.States[id];
                bool died = valid && Health[slot] <= 0f;
                float2 at = state.Known != 0 ? state.LastPredicted : valid ? Positions[slot] : float2.zero;
                B.Leaves.Add(new LeaveUnit { Id = id, Reason = died ? LeaveReason.Died : LeaveReason.LeftVision });
                if (died)
                {
                    B.ExplosionX.Add(SpikeQuantise.Encode(at.x));
                    B.ExplosionY.Add(SpikeQuantise.Encode(at.y));
                }
                state.Known = 0;
                state.KnownGeneration = 0;
                B.States[id] = state;
            }

            // Arrivals: a fresh unit, with its route.
            for (int i = 0; i < Entered.Length; i++)
            {
                int id = Entered[i];
                int slot = SlotOf(id);
                if ((uint)slot >= (uint)Capacity || Ids[slot] != id) continue;
                if (B.States[id].Known != 0) scalars.Reseeds++;

                var entry = new EnterUnit
                {
                    Id = id,
                    Type = 0,
                    Owner = Player[slot],
                    X = SpikeQuantise.Encode(Positions[slot].x),
                    Y = SpikeQuantise.Encode(Positions[slot].y),
                    Health = HealthByte(Health[slot]),
                    First = B.EnterTiles.Length,
                };
                entry.Count = AppendRoute(id, B.EnterTiles);
                B.Enters.Add(entry);
                B.States[id] = new PredictState
                {
                    Anchor = Positions[slot],
                    LastPredicted = Positions[slot],
                    AnchorTime = now,
                    StartTime = now,
                    Index = 0,
                    Known = 1,
                    Fresh = 1,
                    LastHealth = entry.Health,
                    KnownGeneration = Generation(id),
                    Speed = Config.Speed,
                    LastActual = Positions[slot],
                    LastCheck = now,
                };
            }

            // Everything the client is allowed to know: health changes, new routes and corrections.
            int interval = math.max(1, Config.CorrectionInterval);
            int words = Allowed.Length;
            for (int w = 0; w < words; w++)
            {
                ulong bits = Allowed[w];
                while (bits != 0)
                {
                    int bit = math.tzcnt(bits);
                    bits &= bits - 1;
                    int id = w * 64 + bit;
                    if (id >= IdCapacity) continue;
                    int slot = SlotOf(id);
                    if ((uint)slot >= (uint)Capacity || Ids[slot] != id) continue;

                    PredictState state = B.States[id];
                    if (state.Fresh != 0 || state.Known == 0) continue;

                    byte healthByte = HealthByte(Health[slot]);
                    if (healthByte != state.LastHealth)
                    {
                        B.Healths.Add(new HealthUnit { Id = id, Health = healthByte });
                        state.LastHealth = healthByte;
                    }

                    int generation = Generation(id);
                    if (generation != state.KnownGeneration)
                    {
                        B.MoveOrders.Add(new RouteUnit
                        {
                            Id = id,
                            First = B.MoveOrderTiles.Length,
                            Count = AppendRoute(id, B.MoveOrderTiles),
                        });
                        state.KnownGeneration = generation;
                        // A new route restarts the client's prediction at its first waypoint.
                        state.Index = 0;
                        state.Anchored = 0;
                        state.StartTime = now;
                        state.Anchor = Positions[slot];
                        state.Speed = Config.Speed;
                    }

                    bool inView = InView(scalars.ViewCentre, Positions[slot]);
                    int every = inView ? interval : math.max(1, Config.OffscreenInterval);
                    if ((Tick + id) % every == 0)
                        CheckCorrection(ref scalars, slot, id, ref state, now, inView, every);

                    B.States[id] = state;
                }
            }

            FlushEnters();
            FlushMoveOrders();
            FlushLeaves();
            FlushHealths();
            FlushExplosions();
            FlushFogDelta();
            SelectCorrections(ref scalars, now, B.Reliable.Length);
            FlushCorrections();

            // The tick's fresh units are ordinary units from the next tick on.
            for (int i = 0; i < B.Enters.Length; i++)
            {
                int id = B.Enters[i].Id;
                PredictState state = B.States[id];
                if (state.Fresh == 0) continue;
                state.Fresh = 0;
                B.States[id] = state;
            }
            B.Scalars[0] = scalars;
        }

        private bool InView(float2 centre, float2 position)
        {
            if (Config.ViewHalfExtents.x <= 0f) return true;
            float2 d = math.abs(position - centre);
            return d.x <= Config.ViewHalfExtents.x && d.y <= Config.ViewHalfExtents.y;
        }

        /// <summary>
        /// The correction rule: the client's predicted position against the simulation's. A unit off by
        /// more than its threshold becomes a candidate; the rest just record their error.
        /// </summary>
        private void CheckCorrection(ref ClientScalars scalars, int slot, int id, ref PredictState state, float now,
            bool inView, int weight)
        {
            float2 actual = Positions[slot];
            int routeFirst = First(id);
            int routeCount = Count(id);
            float2 predicted = Predict(ref state, routeFirst, routeCount, now, out Prediction leg);

            leg.Id = id;
            scalars.LastPrediction = leg;
            state.LastPredicted = predicted;

            float elapsed = now - state.LastCheck;
            float measured = elapsed > 0f ? math.distance(actual, state.LastActual) / elapsed : state.Speed;
            state.LastActual = actual;
            state.LastCheck = now;

            float error = math.distance(actual, predicted);
            if (inView)
            {
                int raw = math.min(ErrorHistogram.Buckets - 1, (int)(error * ErrorHistogram.BucketsPerTile));
                B.Errors[2 * ErrorHistogram.Buckets + raw] += 1;
                if (leg.Anchored != 0) B.Errors[3 * ErrorHistogram.Buckets + raw] += 1;
            }
            float threshold = inView ? Config.CorrectionThreshold : Config.OffscreenThreshold;
            if (error <= threshold)
            {
                RecordError(inView, error, weight);
                return;
            }
            B.Candidates.Add(new Candidate
            {
                Id = id, Actual = actual, Error = inView ? error : -error, Weight = weight, Speed = SpeedByte(measured),
            });
        }

        /// <summary>
        /// Turns the tick's candidates into corrections. Without a budget every candidate is sent.
        /// With one, the reliable bytes are paid first and the rest go largest error first (in-view
        /// units ahead of off-screen ones) while the bucket lasts; the others keep their prediction.
        /// </summary>
        private void SelectCorrections(ref ClientScalars scalars, float now, int reliableBytes)
        {
            int budget = Config.BudgetBytesPerSecond;
            float available = float.MaxValue;
            if (budget > 0)
            {
                scalars.Tokens = math.min(budget, scalars.Tokens + budget * SpikeSimRules.TickSeconds) - reliableBytes;
                available = scalars.Tokens;
                B.Candidates.Sort(new CandidateOrder());
            }

            int previous = 0;
            float spent = 0f;
            for (int i = 0; i < B.Candidates.Length; i++)
            {
                Candidate candidate = B.Candidates[i];
                bool inView = candidate.Error >= 0f;
                float error = math.abs(candidate.Error);
                PredictState state = B.States[candidate.Id];
                int routeCount = Count(candidate.Id);
                int resume = Config.ProjectResume
                    ? ProjectedResume(First(candidate.Id), routeCount, state.Index, candidate.Actual)
                    : math.clamp(state.Index + 1, 0, math.max(0, routeCount - 1));

                // What the client will reconstruct, so both sides anchor on the same point.
                var unit = new CorrectionUnit { Id = candidate.Id, Resume = resume, Speed = candidate.Speed };
                float2 anchor;
                int cost;
                if (Config.DeltaScale == 0)
                {
                    unit.X = SpikeQuantise.Encode(candidate.Actual.x);
                    unit.Y = SpikeQuantise.Encode(candidate.Actual.y);
                    anchor = new float2(SpikeQuantise.Decode(unit.X), SpikeQuantise.Decode(unit.Y));
                    cost = SpikeMessages.CorrectionUnitSize(unit.Id, previous, resume);
                }
                else
                {
                    float scale = Config.DeltaScale;
                    int2 delta = (int2)math.round((candidate.Actual - state.LastPredicted) * scale);
                    delta = math.clamp(delta, short.MinValue, short.MaxValue);
                    unit.DX = (short)delta.x;
                    unit.DY = (short)delta.y;
                    anchor = state.LastPredicted + (float2)delta / scale;
                    cost = SpikeMessages.CorrectionDeltaUnitSize(unit.Id, previous, delta.x, delta.y, resume);
                }
                if (Config.SendSpeed) cost++;

                if (spent + cost > available)
                {
                    RecordError(inView, error, candidate.Weight);
                    continue;
                }
                spent += cost;
                previous = unit.Id;
                B.Corrections.Add(unit);

                state.Anchored = 1;
                state.Anchor = anchor;
                state.AnchorTime = now;
                state.Index = math.max(0, resume - 1);
                state.LastPredicted = anchor;
                if (Config.SendSpeed) state.Speed = candidate.Speed / 64f * Config.Speed;
                B.States[candidate.Id] = state;
                RecordError(inView, math.distance(candidate.Actual, anchor), candidate.Weight);
            }
            if (budget > 0)
            {
                scalars.Tokens -= spent;
                // The flush wants ascending ids for the delta coding; the budget sort broke that order.
                B.Corrections.Sort(new CorrectionOrder());
            }
        }

        private void RecordError(bool inView, float error, int weight)
        {
            int bucket = math.min(ErrorHistogram.Buckets - 1, (int)(error * ErrorHistogram.BucketsPerTile));
            B.Errors[bucket] += weight;
            if (inView) B.Errors[ErrorHistogram.Buckets + bucket] += weight;
        }

        /// <summary>
        /// The client's position for a unit: the anchored first leg straight out of a corrected point,
        /// then the route itself through <see cref="PathFollower.Evaluate"/>. The anchor becomes an
        /// ordinary leg once the unit has walked past the resumed waypoint, so the steady state is a
        /// plain route evaluation - bit for bit the client's, because it is the same call.
        /// </summary>
        private float2 Predict(ref PredictState state, int routeFirst, int routeCount, float now, out Prediction leg)
        {
            leg = default;
            if (routeCount <= 0) return state.Anchor;
            if (state.Anchored != 0)
            {
                int target = state.Index + 1;
                if (target >= routeCount)
                {
                    // The route ended: the client holds the corrected point.
                    leg = new Prediction { First = routeFirst, Count = 1, StartTime = state.AnchorTime, Anchored = 1 };
                    return state.Anchor;
                }
                float2 to = Waypoints[routeFirst + target];
                float2 segment = to - state.Anchor;
                float length = math.length(segment);
                float remaining = math.max(0f, state.Speed * (now - state.AnchorTime));
                if (length <= 0f || remaining <= length)
                {
                    leg = new Prediction { First = routeFirst + target, Count = 1, StartTime = state.AnchorTime, Anchored = 1 };
                    return length > 0f ? state.Anchor + segment * (remaining / length) : state.Anchor;
                }

                state.Anchored = 0;
                state.Index = target;
                state.StartTime = state.AnchorTime + length / state.Speed;
            }

            int index = math.clamp(state.Index, 0, routeCount - 1);
            leg = new Prediction
            {
                First = routeFirst + index,
                Count = math.max(1, routeCount - index),
                StartTime = state.StartTime,
                Anchored = 0,
            };
            PathFollower.Evaluate(Waypoints, routeFirst + index, routeCount - index, state.Speed,
                state.StartTime, now, out float2 position, out float _);
            return position;
        }

        /// <summary>Appends a unit's route (tiles) to a message's waypoint list, returning its count.</summary>
        private int AppendRoute(int id, NativeList<int2> into)
        {
            int first = First(id), count = Count(id);
            if (count <= 0)
            {
                // No route at all (a unit the maintenance pass has not seen): the client still needs a
                // point to hold at, so send the unit's own tile.
                int slot = SlotOf(id);
                into.Add(slot >= 0 ? (int2)math.floor(Positions[slot]) : int2.zero);
                return 1;
            }
            for (int i = 0; i < count; i++) into.Add((int2)math.floor(Waypoints[first + i]));
            return count;
        }

        private int SlotOf(int id) => (uint)id < (uint)IdCapacity ? IndexOfId[id] : -1;
        private int First(int id) => (uint)id < (uint)RouteStart.Length ? RouteStart[id] : -1;
        private int Count(int id) => (uint)id < (uint)RouteCount.Length ? RouteCount[id] : 0;
        private int Generation(int id) => (uint)id < (uint)RouteGeneration.Length ? RouteGeneration[id] : 0;

        /// <summary>
        /// The waypoint to resume at: the end of the route segment nearest <paramref name="actual"/>,
        /// searched a few legs either side of the prediction's own leg.
        /// </summary>
        private int ProjectedResume(int first, int count, int index, float2 actual)
        {
            if (count <= 1) return 0;
            int from = math.max(0, index - 2), to = math.min(count - 2, index + 4);
            int best = math.clamp(index, 0, count - 2);
            float bestDistance = float.MaxValue;
            for (int i = from; i <= to; i++)
            {
                float2 a = Waypoints[first + i], b = Waypoints[first + i + 1];
                float2 ab = b - a;
                float t = math.saturate(math.dot(actual - a, ab) / math.max(1e-6f, math.lengthsq(ab)));
                float d = math.distancesq(actual, a + ab * t);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best + 1;
        }

        /// <summary>A speed in tiles a second as the wire's 1/64-of-full-speed byte.</summary>
        private byte SpeedByte(float speed) =>
            (byte)math.clamp((int)math.round(speed / Config.Speed * 64f), 0, 255);

        private byte HealthByte(float value) =>
            (byte)math.round(math.saturate(value / Config.MaxHealth) * 100f);

        private void Add(SpikeMessageType type, int bytes, int count, bool reliable = true)
        {
            B.Totals[(int)type] += bytes;
            B.Totals[ReplicationEncoder.TypeCount + 1 + (int)type] += count;
            if (reliable) B.ReliableSizes.Add(bytes);
            else B.UnreliableSizes.Add(bytes);
        }

        private void FlushEnters()
        {
            if (B.Enters.Length == 0) return;
            NativeArray<int2> waypoints = B.EnterTiles.AsArray();
            var sink = new NativeSink(B.Reliable);
            int index = 0;
            while (index < B.Enters.Length)
            {
                int start = index;
                int size = 1 + 3; // type byte and the count varint (at most three bytes at this scale)
                int previous = 0;
                while (index < B.Enters.Length)
                {
                    EnterUnit entry = B.Enters[index];
                    int cost = SpikeMessages.EnterUnitSize(waypoints, entry.First, entry.Count, entry.Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = entry.Id;
                    index++;
                }
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new EnterMessage { Waypoints = waypoints, Units = B.Enters.AsArray().GetSubArray(start, index - start) });
                Add(SpikeMessageType.Enter, sink.Position - before, index - start);
            }
        }

        private void FlushMoveOrders()
        {
            if (B.MoveOrders.Length == 0) return;
            NativeArray<int2> waypoints = B.MoveOrderTiles.AsArray();
            var sink = new NativeSink(B.Reliable);
            int index = 0;
            while (index < B.MoveOrders.Length)
            {
                int start = index;
                int size = 1 + VarInt.Size((uint)Tick) + 1 + 3; // type, tick, speed class, count
                int previous = 0;
                while (index < B.MoveOrders.Length)
                {
                    RouteUnit unit = B.MoveOrders[index];
                    int cost = SpikeMessages.MoveOrderUnitSize(waypoints, unit.First, unit.Count, unit.Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = unit.Id;
                    index++;
                }
                int count = index - start;
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new MoveOrderMessage
                {
                    Tick = Tick,
                    SpeedClass = 0,
                    Waypoints = waypoints,
                    Units = B.MoveOrders.AsArray().GetSubArray(start, count),
                });
                Add(SpikeMessageType.MoveOrder, sink.Position - before, count);
            }
        }

        private void FlushLeaves()
        {
            if (B.Leaves.Length == 0) return;
            var sink = new NativeSink(B.Reliable);
            int index = 0;
            while (index < B.Leaves.Length)
            {
                int start = index;
                int size = 1 + 3;
                int previous = 0;
                while (index < B.Leaves.Length)
                {
                    int cost = SpikeMessages.LeaveUnitSize(B.Leaves[index].Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = B.Leaves[index].Id;
                    index++;
                }
                int count = index - start;
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new LeaveMessage { Units = B.Leaves.AsArray().GetSubArray(start, count) });
                Add(SpikeMessageType.Leave, sink.Position - before, count);
            }
        }

        private void FlushHealths()
        {
            if (B.Healths.Length == 0) return;
            var sink = new NativeSink(B.Reliable);
            int index = 0;
            while (index < B.Healths.Length)
            {
                int start = index;
                int size = 1 + 3;
                int previous = 0;
                while (index < B.Healths.Length)
                {
                    int cost = SpikeMessages.HealthUnitSize(B.Healths[index].Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = B.Healths[index].Id;
                    index++;
                }
                int count = index - start;
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new HealthMessage { Units = B.Healths.AsArray().GetSubArray(start, count) });
                Add(SpikeMessageType.Health, sink.Position - before, count);
            }
        }

        private void FlushExplosions()
        {
            if (B.ExplosionX.Length == 0) return;
            var sink = new NativeSink(B.Reliable);
            int index = 0;
            while (index < B.ExplosionX.Length)
            {
                int start = index;
                int size = 1 + 3;
                while (index < B.ExplosionX.Length)
                {
                    if (index > start && size + 4 > SpikeMessages.ReliableChunk) break;
                    size += 4;
                    index++;
                }
                int count = index - start;
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new ExplosionMessage
                {
                    X = B.ExplosionX.AsArray().GetSubArray(start, count),
                    Y = B.ExplosionY.AsArray().GetSubArray(start, count),
                });
                Add(SpikeMessageType.Explosion, sink.Position - before, count);
            }
        }

        private void FlushFogDelta()
        {
            if (FogCells.Length == 0) return;
            var sink = new NativeSink(B.Reliable);
            int index = 0;
            while (index < FogCells.Length)
            {
                int start = index;
                int size = 1 + 3;
                int previous = 0;
                while (index < FogCells.Length)
                {
                    int cost = VarInt.Size((uint)(FogCells[index] - previous));
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = FogCells[index];
                    index++;
                }
                int count = index - start;
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new FogDeltaMessage { Cells = FogCells.GetSubArray(start, count) });
                Add(SpikeMessageType.FogDelta, sink.Position - before, count);
            }
        }

        /// <summary>
        /// Corrections are the unreliable channel's only message, and each one must fit
        /// <see cref="SpikeWire.CorrectionMessageLimit"/>, so the packer closes a message before the
        /// next entry would push it over.
        /// </summary>
        private void FlushCorrections()
        {
            if (B.Corrections.Length == 0) return;
            var sink = new NativeSink(B.Unreliable);
            int index = 0;
            while (index < B.Corrections.Length)
            {
                int start = index;
                int size = 1 + VarInt.Size((uint)Tick) + 1 + 3; // type, tick, delta scale, count
                int previous = 0;
                while (index < B.Corrections.Length)
                {
                    CorrectionUnit unit = B.Corrections[index];
                    int cost = Config.DeltaScale == 0
                        ? SpikeMessages.CorrectionUnitSize(unit.Id, previous, unit.Resume)
                        : SpikeMessages.CorrectionDeltaUnitSize(unit.Id, previous, unit.DX, unit.DY, unit.Resume);
                    if (Config.SendSpeed) cost++;
                    if (index > start && size + cost > SpikeWire.CorrectionMessageLimit) break;
                    size += cost;
                    previous = unit.Id;
                    index++;
                }
                int count = index - start;
                int before = sink.Position;
                SpikeMessages.Encode(ref sink, new CorrectionMessage
                {
                    Tick = Tick,
                    DeltaScale = Config.DeltaScale,
                    HasSpeed = Config.SendSpeed,
                    Units = B.Corrections.AsArray().GetSubArray(start, count),
                });
                Add(SpikeMessageType.Correction, sink.Position - before, count, reliable: false);
            }
        }

        /// <summary>In-view first (non-negative), then by error, largest first.</summary>
        private struct CandidateOrder : System.Collections.Generic.IComparer<Candidate>
        {
            public int Compare(Candidate a, Candidate b)
            {
                bool av = a.Error >= 0f, bv = b.Error >= 0f;
                if (av != bv) return av ? -1 : 1;
                return math.abs(b.Error).CompareTo(math.abs(a.Error));
            }
        }

        private struct CorrectionOrder : System.Collections.Generic.IComparer<CorrectionUnit>
        {
            public int Compare(CorrectionUnit a, CorrectionUnit b) => a.Id.CompareTo(b.Id);
        }
    }

    /// <summary>
    /// Position errors in 1/64-tile buckets up to 32 tiles (the last bucket holds everything above),
    /// weighted, so the percentiles of millions of samples cost nothing to keep.
    /// </summary>
    public sealed class ErrorHistogram
    {
        public const float BucketsPerTile = 64f;
        public const int Buckets = 32 * 64 + 1;
        private readonly long[] counts = new long[Buckets];
        private long total;

        public long Total => total;

        public void Add(float error, int weight)
        {
            int bucket = math.min(Buckets - 1, (int)(error * BucketsPerTile));
            counts[bucket] += weight;
            total += weight;
        }

        /// <summary>Adds a native histogram's counts (one client's) to this one.</summary>
        public void AddCounts(NativeArray<long> source, int offset, int length)
        {
            for (int i = 0; i < length; i++)
            {
                counts[i] += source[offset + i];
                total += source[offset + i];
            }
        }

        /// <summary>The error at percentile <paramref name="p"/> (0-100), as the bucket's upper edge, in tiles.</summary>
        public double Percentile(double p)
        {
            if (total == 0) return double.NaN;
            long rank = (long)Math.Ceiling(p / 100.0 * total);
            long seen = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                seen += counts[i];
                if (seen >= rank && counts[i] > 0) return (i + 1) / BucketsPerTile;
            }
            return counts.Length / BucketsPerTile;
        }

        public double Mean
        {
            get
            {
                if (total == 0) return double.NaN;
                double sum = 0;
                for (int i = 0; i < counts.Length; i++) sum += counts[i] * (i + 0.5) / BucketsPerTile;
                return sum / total;
            }
        }
    }
}
