using System;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;

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

        /// <summary>The plan's defaults: 0.25 tiles, every other tick, 5 tiles/s, 100 health.</summary>
        public static EncoderConfig Defaults => new EncoderConfig
        {
            CorrectionThreshold = 0.25f,
            CorrectionInterval = 2,
            Speed = SpikeSimRules.Speed,
            MaxHealth = 100f,
        };
    }

    /// <summary>
    /// The server half of the v0.5 hybrid movement model, and the spike's byte counter. For one
    /// client it turns the tick's state into the seven message types and writes them into Mirror's
    /// reliable and unreliable writers.
    ///
    /// <para><b>What it can see.</b> The encoder's unit loop is driven entirely by the client's
    /// <see cref="InterestSets"/> allowed bitset: an id that is not in it is never looked up, never
    /// encoded, and cannot leak. The only world data it reads is the SoA it was bound to, through the
    /// sim's id-to-slot map of an allowed id. The v0.5 leak tests have their prototype here.</para>
    ///
    /// <para><b>Prediction and corrections.</b> A unit the client knows carries a route (the traced
    /// flow path) and a leg to walk. The client evaluates
    /// <see cref="PathFollower.Evaluate"/> over the route at speed; the server does exactly the same
    /// for the same unit, and when the simulation's position is more than
    /// <see cref="EncoderConfig.CorrectionThreshold"/> tiles off the prediction, it sends the corrected
    /// point and the waypoint to resume from, at most once every
    /// <see cref="EncoderConfig.CorrectionInterval"/> ticks per unit. After a correction the client
    /// predicts straight from the corrected point toward the resumed waypoint, and so does the
    /// server.</para>
    ///
    /// <para><b>Cadence.</b> The encoder runs on the main thread after the tick's jobs complete,
    /// because Mirror's writers are managed. Every method is single-threaded.</para>
    /// </summary>
    public sealed class ReplicationEncoder : IDisposable
    {
        /// <summary>
        /// What one client believes about one unit: its prediction and the last byte sent. Keyed by the
        /// unit's id, never by its ECS slot - the simulation's query slots churn by a quarter of the
        /// army a tick, so a slot's occupant is a different unit on the next tick.
        /// </summary>
        private struct PredictState
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
        }

        /// <summary>The last prediction evaluated for a client, kept so tests and diagnostics can see its inputs.</summary>
        private struct Prediction
        {
            public int Id;
            public int First;
            public int Count;
            public float StartTime;
            public byte Anchored;
        }

        private const int TypeCount = 7;

        private readonly EncoderConfig config;
        private readonly InterestSets interests;
        private readonly RouteStore routes;
        private readonly int clients, idCapacity, capacity, words;

        private readonly NativeArray<PredictState>[] states;
        private readonly NativeArray<byte>[] lastHealth;
        private readonly NativeArray<int>[] knownGeneration;
        private readonly long[,] byteTotals;   // [client, message type]
        private readonly long[,] unitTotals;   // [client, message type]
        private readonly float[] lastNow;
        private readonly Prediction[] lastPredictions;

        // Per-call scratch, reused across clients.
        private readonly NativeList<RouteUnit> moveOrders;
        private readonly NativeList<int2> moveOrderTiles;
        private readonly NativeList<EnterUnit> enters;
        private readonly NativeList<int2> enterTiles;
        private readonly NativeList<LeaveUnit> leaves;
        private readonly NativeList<HealthUnit> healths;
        private readonly NativeList<CorrectionUnit> corrections;
        private readonly NativeList<ushort> explosionX, explosionY;
        private readonly NativeList<int> fogCells;
        private readonly NativeList<int> reliableSizes, unreliableSizes;

        private NativeArray<float2> positions;
        private NativeArray<float> health;
        private NativeArray<byte> player;
        private NativeArray<int> ids;
        private NativeArray<int> indexOfId;

        private FogSystem fog;
        private int accountingClient;
        private bool disposed;

        /// <summary>
        /// Builds an encoder for <paramref name="interests"/>' clients, reading routes from
        /// <paramref name="routes"/>. The per-client prediction and "last byte sent" tables are
        /// allocated once, indexed by unit slot.
        /// </summary>
        public ReplicationEncoder(in EncoderConfig config, InterestSets interests, RouteStore routes,
            int capacity, int idCapacity, Allocator allocator)
        {
            this.config = config;
            this.interests = interests;
            this.routes = routes;
            this.capacity = math.max(1, capacity);
            this.idCapacity = math.max(1, idCapacity);
            clients = interests.Clients;
            words = BitSet.Words(this.idCapacity);

            states = new NativeArray<PredictState>[clients];
            lastHealth = new NativeArray<byte>[clients];
            knownGeneration = new NativeArray<int>[clients];
            for (int client = 0; client < clients; client++)
            {
                states[client] = new NativeArray<PredictState>(this.idCapacity, allocator);
                lastHealth[client] = new NativeArray<byte>(this.idCapacity, allocator);
                knownGeneration[client] = new NativeArray<int>(this.idCapacity, allocator);
            }
            byteTotals = new long[clients, TypeCount + 1];
            unitTotals = new long[clients, TypeCount + 1];
            lastNow = new float[clients];
            lastPredictions = new Prediction[clients];

            moveOrders = new NativeList<RouteUnit>(256, allocator);
            moveOrderTiles = new NativeList<int2>(1024, allocator);
            enters = new NativeList<EnterUnit>(256, allocator);
            enterTiles = new NativeList<int2>(1024, allocator);
            leaves = new NativeList<LeaveUnit>(64, allocator);
            healths = new NativeList<HealthUnit>(256, allocator);
            corrections = new NativeList<CorrectionUnit>(1024, allocator);
            explosionX = new NativeList<ushort>(64, allocator);
            explosionY = new NativeList<ushort>(64, allocator);
            fogCells = new NativeList<int>(1024, allocator);
            reliableSizes = new NativeList<int>(64, allocator);
            unreliableSizes = new NativeList<int>(64, allocator);
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

        /// <summary>Payload bytes the client was sent for one message type over the whole run.</summary>
        public long Bytes(int client, SpikeMessageType type) => byteTotals[client, (int)type];

        /// <summary>Units (or explosions, or cells) one message type carried for the client.</summary>
        public long Count(int client, SpikeMessageType type) => unitTotals[client, (int)type];

        /// <summary>Times a slot changed hands underneath a client's prediction (diagnostic only).</summary>
        public long Reseeds { get; private set; }

        /// <summary>
        /// The reliable messages the last <see cref="Encode"/> wrote, by payload size, in order. The
        /// bench feeds these to <see cref="SpikeWire.WireBytes"/> so Mirror's batching and KCP's
        /// segmentation are counted message by message rather than guessed from the total.
        /// </summary>
        public NativeArray<int> ReliableMessageSizes => reliableSizes.AsArray();

        /// <summary>The unreliable messages the last <see cref="Encode"/> wrote, by payload size.</summary>
        public NativeArray<int> UnreliableMessageSizes => unreliableSizes.AsArray();

        /// <summary>The clock the client's last encode used, in seconds.</summary>
        public float LastNow(int client) => lastNow[client];

        /// <summary>The position the client predicts for a unit after the last <see cref="Encode"/>.</summary>
        public float2 PredictedPosition(int client, int id) => states[client][id].LastPredicted;

        /// <summary>True when the client currently predicts from a corrected point rather than a waypoint.</summary>
        public bool PredictsFromCorrection(int client, int id) => states[client][id].Anchored != 0;

        /// <summary>
        /// The route leg the client's last prediction walked: the slice it evaluates and the time it
        /// started from. False when the last prediction came from a correction anchor, which is not a
        /// plain route evaluation. This is what the prediction test feeds
        /// <see cref="PathFollowerJob"/> with.
        /// </summary>
        public bool PredictedLeg(int client, int id, out int first, out int count, out float startTime)
        {
            Prediction prediction = lastPredictions[client];
            first = prediction.First;
            count = prediction.Count;
            startTime = prediction.StartTime;
            return prediction.Id == id && prediction.Anchored == 0;
        }

        /// <summary>
        /// Encodes one tick for one client. The reliable writer takes the stateful messages, the
        /// unreliable one the corrections; both are appended to, and the caller resets them. The
        /// tick's <see cref="InterestSets.Build"/> must have run first.
        /// </summary>
        public void Encode(int client, int tick, NetworkWriter reliable, NetworkWriter unreliable)
        {
            ThrowIfDisposed();
            float now = tick * SpikeSimRules.TickSeconds;
            lastNow[client] = now;
            accountingClient = client;
            moveOrders.Clear();
            moveOrderTiles.Clear();
            enters.Clear();
            enterTiles.Clear();
            leaves.Clear();
            healths.Clear();
            corrections.Clear();
            explosionX.Clear();
            explosionY.Clear();
            fogCells.Clear();
            reliableSizes.Clear();
            unreliableSizes.Clear();

            // Departures first. The interest build has already reseeded the client's knowledge, so a
            // unit that died or left vision has no other way to be reported.
            NativeList<int> left = interests.Left(client);
            for (int i = 0; i < left.Length; i++)
            {
                int id = left[i];
                int slot = SlotOf(id);
                bool valid = (uint)slot < (uint)capacity && ids[slot] == id;
                PredictState state = states[client][id];
                bool died = valid && health[slot] <= 0f;
                float2 at = state.Known != 0 ? state.LastPredicted : valid ? positions[slot] : float2.zero;

                leaves.Add(new LeaveUnit { Id = id, Reason = died ? LeaveReason.Died : LeaveReason.LeftVision });
                if (died)
                {
                    explosionX.Add(SpikeQuantise.Encode(at.x));
                    explosionY.Add(SpikeQuantise.Encode(at.y));
                }
                state.Known = 0;
                states[client][id] = state;
                knownGeneration[client][id] = 0;
            }

            // Arrivals: a fresh unit, with its route.
            NativeList<int> entered = interests.Entered(client);
            for (int i = 0; i < entered.Length; i++)
            {
                int id = entered[i];
                int slot = SlotOf(id);
                if ((uint)slot >= (uint)capacity || ids[slot] != id) continue;

                PredictState previous = states[client][id];
                if (previous.Known != 0) Reseeds++;

                var entry = new EnterUnit
                {
                    Id = id,
                    Type = 0,
                    Owner = player[slot],
                    X = SpikeQuantise.Encode(positions[slot].x),
                    Y = SpikeQuantise.Encode(positions[slot].y),
                    Health = HealthByte(health[slot]),
                    First = enterTiles.Length,
                };
                entry.Count = AppendRoute(id, enterTiles);
                enters.Add(entry);

                states[client][id] = new PredictState
                {
                    Anchor = positions[slot],
                    LastPredicted = positions[slot],
                    AnchorTime = now,
                    StartTime = now,
                    Index = 0,
                    Known = 1,
                    Fresh = 1,
                };
                lastHealth[client][id] = entry.Health;
                knownGeneration[client][id] = routes.Generation(id);
            }

            // Everything the client is allowed to know: health changes, new routes and corrections.
            NativeArray<ulong> allowed = interests.Allowed(client);
            int interval = math.max(1, config.CorrectionInterval);
            for (int w = 0; w < words; w++)
            {
                ulong bits = allowed[w];
                while (bits != 0)
                {
                    int bit = math.tzcnt(bits);
                    bits &= bits - 1;
                    int id = w * 64 + bit;
                    if (id >= idCapacity) continue;
                    int slot = SlotOf(id);
                    if ((uint)slot >= (uint)capacity || ids[slot] != id) continue;

                    PredictState state = states[client][id];
                    if (state.Fresh != 0 || state.Known == 0) continue;

                    byte healthByte = HealthByte(health[slot]);
                    if (healthByte != lastHealth[client][id])
                    {
                        healths.Add(new HealthUnit { Id = id, Health = healthByte });
                        lastHealth[client][id] = healthByte;
                    }

                    int generation = routes.Generation(id);
                    if (generation != knownGeneration[client][id])
                    {
                        moveOrders.Add(new RouteUnit
                        {
                            Id = id,
                            First = moveOrderTiles.Length,
                            Count = AppendRoute(id, moveOrderTiles),
                        });
                        knownGeneration[client][id] = generation;
                        // A new route restarts the client's prediction at its first waypoint.
                        state.Index = 0;
                        state.Anchored = 0;
                        state.StartTime = now;
                        state.Anchor = positions[slot];
                    }

                    if ((tick + id) % interval == 0)
                        CheckCorrection(client, slot, id, ref state, now);

                    states[client][id] = state;
                }
            }

            // The fog's changed cells go to the team whose grid just flipped.
            if (fog != null)
            {
                int team = interests.TeamOfClient(client);
                for (int i = 0; i < fog.LastTickTeams; i++)
                {
                    if (fog.LastTickTeam(i) != team) continue;
                    NativeList<int> changed = fog.Changed(team);
                    for (int c = 0; c < changed.Length; c++) fogCells.Add(changed[c]);
                    break;
                }
            }

            FlushEnters(reliable);
            FlushMoveOrders(reliable, tick);
            FlushLeaves(reliable);
            FlushHealths(reliable);
            FlushExplosions(reliable);
            FlushFogDelta(reliable);
            FlushCorrections(unreliable, tick);

            // The tick's fresh units are ordinary units from the next tick on.
            for (int i = 0; i < enters.Length; i++)
            {
                int id = enters[i].Id;
                PredictState state = states[client][id];
                if (state.Fresh == 0) continue;
                state.Fresh = 0;
                states[client][id] = state;
            }
        }

        /// <summary>
        /// The correction rule: the client's predicted position against the simulation's, checked once
        /// every <see cref="EncoderConfig.CorrectionInterval"/> ticks per unit. A correction carries the
        /// real position and the waypoint to steer toward next, and restarts the client's prediction
        /// from there.
        /// </summary>
        private void CheckCorrection(int client, int slot, int id, ref PredictState state, float now)
        {
            float2 actual = positions[slot];
            int routeFirst = routes.First(id);
            int routeCount = routes.Count(id);
            float2 predicted = Predict(ref state, routeFirst, routeCount, now, out Prediction leg);

            leg.Id = id;
            lastPredictions[client] = leg;
            state.LastPredicted = predicted;

            if (math.distance(actual, predicted) <= config.CorrectionThreshold) return;

            // Resume at the waypoint the client was steering toward, so both sides walk the same leg.
            int resume = math.clamp(state.Index + 1, 0, math.max(0, routeCount - 1));
            corrections.Add(new CorrectionUnit
            {
                Id = id,
                X = SpikeQuantise.Encode(actual.x),
                Y = SpikeQuantise.Encode(actual.y),
                Resume = resume,
            });
            state.Anchored = 1;
            state.Anchor = actual;
            state.AnchorTime = now;
            state.Index = math.max(0, resume - 1);
            state.LastPredicted = actual;
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
                float2 to = routes.Waypoints[routeFirst + target];
                float2 segment = to - state.Anchor;
                float length = math.length(segment);
                float remaining = math.max(0f, config.Speed * (now - state.AnchorTime));
                if (length <= 0f || remaining <= length)
                {
                    leg = new Prediction { First = routeFirst + target, Count = 1, StartTime = state.AnchorTime, Anchored = 1 };
                    return length > 0f ? state.Anchor + segment * (remaining / length) : state.Anchor;
                }

                state.Anchored = 0;
                state.Index = target;
                state.StartTime = state.AnchorTime + length / config.Speed;
            }

            int index = math.clamp(state.Index, 0, routeCount - 1);
            leg = new Prediction
            {
                First = routeFirst + index,
                Count = math.max(1, routeCount - index),
                StartTime = state.StartTime,
                Anchored = 0,
            };
            PathFollower.Evaluate(routes.Waypoints, routeFirst + index, routeCount - index, config.Speed,
                state.StartTime, now, out float2 position, out float _);
            return position;
        }

        /// <summary>Appends a unit's route (tiles) to a message's waypoint list, returning its count.</summary>
        private int AppendRoute(int id, NativeList<int2> into)
        {
            int first = routes.First(id), count = routes.Count(id);
            if (count <= 0)
            {
                // No route at all (a unit the maintenance pass has not seen): the client still needs a
                // point to hold at, so send the unit's own tile.
                int slot = SlotOf(id);
                into.Add(slot >= 0 ? (int2)math.floor(positions[slot]) : int2.zero);
                return 1;
            }
            for (int i = 0; i < count; i++) into.Add((int2)math.floor(routes.Waypoints[first + i]));
            return count;
        }

        private int SlotOf(int id) => (uint)id < (uint)idCapacity ? indexOfId[id] : -1;

        private byte HealthByte(float value) =>
            (byte)math.round(math.saturate(value / config.MaxHealth) * 100f);

        private void EncodeEnterChunk(NetworkWriter writer, NativeArray<int2> waypoints, NativeArray<EnterUnit> units)
        {
            int before = writer.Position;
            SpikeMessages.Encode(writer, new EnterMessage { Waypoints = waypoints, Units = units });
            Add(SpikeMessageType.Enter, writer.Position - before, units.Length);
        }

        private void FlushEnters(NetworkWriter writer)
        {
            if (enters.Length == 0) return;
            NativeArray<int2> waypoints = enterTiles.AsArray();
            int index = 0;
            while (index < enters.Length)
            {
                int start = index;
                int size = 1 + 3; // type byte and the count varint (at most three bytes at this scale)
                int previous = 0;
                while (index < enters.Length)
                {
                    EnterUnit entry = enters[index];
                    int cost = SpikeMessages.EnterUnitSize(waypoints, entry.First, entry.Count, entry.Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = entry.Id;
                    index++;
                }
                EncodeEnterChunk(writer, waypoints, enters.AsArray().GetSubArray(start, index - start));
            }
        }

        private void FlushMoveOrders(NetworkWriter writer, int tick)
        {
            if (moveOrders.Length == 0) return;
            NativeArray<int2> waypoints = moveOrderTiles.AsArray();
            int index = 0;
            while (index < moveOrders.Length)
            {
                int start = index;
                int size = 1 + VarInt.Size((uint)tick) + 1 + 3; // type, tick, speed class, count
                int previous = 0;
                while (index < moveOrders.Length)
                {
                    RouteUnit unit = moveOrders[index];
                    int cost = SpikeMessages.MoveOrderUnitSize(waypoints, unit.First, unit.Count, unit.Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = unit.Id;
                    index++;
                }
                int count = index - start;
                int before = writer.Position;
                SpikeMessages.Encode(writer, new MoveOrderMessage
                {
                    Tick = tick,
                    SpeedClass = 0,
                    Waypoints = waypoints,
                    Units = moveOrders.AsArray().GetSubArray(start, count),
                });
                Add(SpikeMessageType.MoveOrder, writer.Position - before, count);
            }
        }

        private void FlushLeaves(NetworkWriter writer)
        {
            if (leaves.Length == 0) return;
            int index = 0;
            while (index < leaves.Length)
            {
                int start = index;
                int size = 1 + 3;
                int previous = 0;
                while (index < leaves.Length)
                {
                    int cost = SpikeMessages.LeaveUnitSize(leaves[index].Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = leaves[index].Id;
                    index++;
                }
                int count = index - start;
                int before = writer.Position;
                SpikeMessages.Encode(writer, new LeaveMessage { Units = leaves.AsArray().GetSubArray(start, count) });
                Add(SpikeMessageType.Leave, writer.Position - before, count);
            }
        }

        private void FlushHealths(NetworkWriter writer)
        {
            if (healths.Length == 0) return;
            int index = 0;
            while (index < healths.Length)
            {
                int start = index;
                int size = 1 + 3;
                int previous = 0;
                while (index < healths.Length)
                {
                    int cost = SpikeMessages.HealthUnitSize(healths[index].Id, previous);
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = healths[index].Id;
                    index++;
                }
                int count = index - start;
                int before = writer.Position;
                SpikeMessages.Encode(writer, new HealthMessage { Units = healths.AsArray().GetSubArray(start, count) });
                Add(SpikeMessageType.Health, writer.Position - before, count);
            }
        }

        private void FlushExplosions(NetworkWriter writer)
        {
            if (explosionX.Length == 0) return;
            int index = 0;
            while (index < explosionX.Length)
            {
                int start = index;
                int size = 1 + 3;
                while (index < explosionX.Length)
                {
                    if (index > start && size + 4 > SpikeMessages.ReliableChunk) break;
                    size += 4;
                    index++;
                }
                int count = index - start;
                int before = writer.Position;
                SpikeMessages.Encode(writer, new ExplosionMessage
                {
                    X = explosionX.AsArray().GetSubArray(start, count),
                    Y = explosionY.AsArray().GetSubArray(start, count),
                });
                Add(SpikeMessageType.Explosion, writer.Position - before, count);
            }
        }

        private void FlushFogDelta(NetworkWriter writer)
        {
            if (fogCells.Length == 0) return;
            int index = 0;
            while (index < fogCells.Length)
            {
                int start = index;
                int size = 1 + 3;
                int previous = 0;
                while (index < fogCells.Length)
                {
                    int cost = VarInt.Size((uint)(fogCells[index] - previous));
                    if (index > start && size + cost > SpikeMessages.ReliableChunk) break;
                    size += cost;
                    previous = fogCells[index];
                    index++;
                }
                int count = index - start;
                int before = writer.Position;
                SpikeMessages.Encode(writer, new FogDeltaMessage { Cells = fogCells.AsArray().GetSubArray(start, count) });
                Add(SpikeMessageType.FogDelta, writer.Position - before, count);
            }
        }

        /// <summary>
        /// Corrections are the unreliable channel's only message, and each one must fit the MTU's
        /// 1,194-byte message limit, so the packer closes a message before the next entry would push
        /// it over.
        /// </summary>
        private void FlushCorrections(NetworkWriter writer, int tick)
        {
            if (corrections.Length == 0) return;
            int index = 0;
            while (index < corrections.Length)
            {
                int start = index;
                int size = 1 + VarInt.Size((uint)tick) + 3; // type, tick, count
                int previous = 0;
                while (index < corrections.Length)
                {
                    CorrectionUnit unit = corrections[index];
                    int cost = SpikeMessages.CorrectionUnitSize(unit.Id, previous, unit.Resume);
                    if (index > start && size + cost > SpikeWire.UnreliableMaxMessageSize) break;
                    size += cost;
                    previous = unit.Id;
                    index++;
                }
                int count = index - start;
                int before = writer.Position;
                SpikeMessages.Encode(writer, new CorrectionMessage
                {
                    Tick = tick,
                    Units = corrections.AsArray().GetSubArray(start, count),
                });
                Add(SpikeMessageType.Correction, writer.Position - before, count, reliable: false);
            }
        }

        private void Add(SpikeMessageType type, int bytes, int count, bool reliable = true)
        {
            byteTotals[accountingClient, (int)type] += bytes;
            unitTotals[accountingClient, (int)type] += count;
            if (reliable) reliableSizes.Add(bytes);
            else unreliableSizes.Add(bytes);
        }

        /// <summary>Frees every per-client table and scratch list. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int client = 0; client < clients; client++)
            {
                states[client].Dispose();
                lastHealth[client].Dispose();
                knownGeneration[client].Dispose();
            }
            moveOrders.Dispose();
            moveOrderTiles.Dispose();
            enters.Dispose();
            enterTiles.Dispose();
            leaves.Dispose();
            healths.Dispose();
            corrections.Dispose();
            explosionX.Dispose();
            explosionY.Dispose();
            fogCells.Dispose();
            reliableSizes.Dispose();
            unreliableSizes.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ReplicationEncoder));
        }
    }
}
