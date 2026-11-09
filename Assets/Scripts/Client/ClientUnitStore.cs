using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Net.Replication;
using ReadOnly = Unity.Collections.ReadOnlyAttribute;

namespace WAR2D.Client
{
    /// <summary>
    /// The client's copy of the units it knows: decoded from replication payloads and predicted with
    /// the same <see cref="MovementPrediction"/> the server runs. Plain class (no Unity objects) so the
    /// EditMode tests can drive it against a server encoder. <see cref="ClientWorld"/> wraps it.
    /// </summary>
    public sealed class ClientUnitStore : IDisposable
    {
        private readonly float dt;
        private NativeArray<PredictState> states;
        private NativeArray<int> routeStart, routeCount, owner;
        private NativeArray<byte> type, health;
        private NativeList<float2> arena;
        private int liveWaypoints;
        // Dense list of known indices, for iteration and the prediction job.
        private NativeList<int> known;
        private NativeArray<int> knownSlot; // index -> position in `known`, or -1
        private NativeList<float2> predicted;
        // Drawing only: the jump each correction or new route made, faded out over BlendSeconds.
        private NativeArray<float2> blend;
        private NativeList<float2> drawn;
        private NativeList<float2> steer; // parallel to known: the route position, or the shown order's (no blend)
        // Parallel to known: the index a slot's positions belong to (-1: none yet), when they were predicted, and 1 when this frame's job did.
        private NativeList<int> slotIndex;
        private NativeList<float> slotAt;
        private NativeList<byte> refreshed;
        private int refreshCursor;
        // Units added to the known list's end or moved into a gap since it was last in index order.
        private int unsorted;
        // Parallel to known: the drawn position before an airborne raise (the last refresh's), and how fast it moves.
        private NativeList<float2> slotDrawn, velocity;
        // Drawing only, by index: how far along its route leg the last prediction found the unit (PredictJob.Draw).
        private NativeArray<RouteCursor> cursors;
        // Drawing only: own orders shown before the server answers them, by index.
        private NativeArray<OrderLead> leads;
        private int leadCount;
        private float lastPredictTime = -1f;

        /// <summary>How long a correction's jump takes to fade out of the drawn position.</summary>
        public const float BlendSeconds = 0.1f;
        /// <summary>Jumps longer than this (tiles) are drawn as they are: the unit really was somewhere else.</summary>
        public const float MaxBlendTiles = 2f;
        /// <summary>A long jump fades no faster than this share of the unit's speed, so a moving unit never draws backward.</summary>
        public const float CatchUpShare = 0.5f;
        /// <summary>The slowest a jump may fade, however slow the unit (its time constant in seconds), so jumps can't pile up.</summary>
        public const float CatchUpSeconds = 0.5f;
        /// <summary>The share of its speed a unit is drawn at while its order waits for the server. Below full speed, so it has less to give back.</summary>
        public const float LeadShare = 0.6f;
        /// <summary>The farthest an order is drawn ahead of the server's answer (tiles).</summary>
        public const float MaxLeadTiles = 1.5f;
        /// <summary>How long a shown order waits for the server. With no answer (it refused) the unit glides back to where the server has it.</summary>
        public const float LeadSeconds = 1f;

        /// <summary>
        /// The client's clock, which the drawn-position blend measures jumps at. Prediction itself never
        /// reads it (corrections apply at their own tick, so the server's copy stays bit-identical).
        /// Zero turns the blend off.
        /// </summary>
        public float Now { get; set; }

        /// <summary>Whether a shown order may draw a unit at a point (open floor); null allows every point.</summary>
        public Func<float2, bool> LeadOpen { get; set; }

        // Decode scratch, reused.
        private readonly List<EnterUnit> enters = new List<EnterUnit>();
        private readonly List<LeaveUnit> leaves = new List<LeaveUnit>();
        private readonly List<int2> waypoints = new List<int2>();
        private readonly List<AttackEvent> attacks = new List<AttackEvent>();
        private bool disposed;

        /// <summary>Raised for each attack event: (attacker id index, target id).</summary>
        public event Action<int, int> Attack;

        /// <summary>A building attacked (v0.13): (building id, target id or 0). Raised for every one the server sent, which only a client that may know the building gets.</summary>
        public event Action<int, int> BuildingAttack;
        /// <summary>Raised for each unit that left because it died: (index, last predicted position).</summary>
        public event Action<int, float2> Died;
        /// <summary>v0.20: why the unit <see cref="Died"/> is reporting died (its cause), valid during that event.</summary>
        public LeaveReason LastDeathReason { get; private set; }
        /// <summary>v0.20: a unit left the client's knowledge for any reason (its id index).</summary>
        public event Action<int> Forgotten;
        /// <summary>Raised when a unit the client didn't know becomes known: (index, where it is). Own units are always known, so for them it means a new unit.</summary>
        public event Action<int, float2> Appeared;

        public ClientUnitStore(int idCapacity, float tickSeconds, Allocator allocator = Allocator.Persistent)
        {
            dt = tickSeconds;
            this.allocator = allocator;
            states = new NativeArray<PredictState>(idCapacity, allocator);
            routeStart = new NativeArray<int>(idCapacity, allocator);
            routeCount = new NativeArray<int>(idCapacity, allocator);
            owner = new NativeArray<int>(idCapacity, allocator);
            type = new NativeArray<byte>(idCapacity, allocator);
            health = new NativeArray<byte>(idCapacity, allocator);
            arena = new NativeList<float2>(16 * 1024, allocator);
            known = new NativeList<int>(1024, allocator);
            knownSlot = new NativeArray<int>(idCapacity, allocator);
            for (int i = 0; i < idCapacity; i++) knownSlot[i] = -1;
            predicted = new NativeList<float2>(1024, allocator);
            blend = new NativeArray<float2>(idCapacity, allocator);
            drawn = new NativeList<float2>(1024, allocator);
            steer = new NativeList<float2>(1024, allocator);
            slotIndex = new NativeList<int>(1024, allocator);
            slotAt = new NativeList<float>(1024, allocator);
            refreshed = new NativeList<byte>(1024, allocator);
            slotDrawn = new NativeList<float2>(1024, allocator);
            velocity = new NativeList<float2>(1024, allocator);
            leads = new NativeArray<OrderLead>(idCapacity, allocator);
            cursors = new NativeArray<RouteCursor>(idCapacity, allocator);
            routeUnits = new NativeList<RouteUnit>(64, allocator);
            correctionUnits = new NativeList<CorrectionUnit>(64, allocator);
            healthUnits = new NativeList<HealthUnit>(64, allocator);
            healthTouched = new NativeList<int>(64, allocator);
            routeTiles = new NativeList<int2>(1024, allocator);
            counts = new NativeArray<int>(2, allocator);
            routeChanges = new NativeList<int2>(64, allocator);
            payloadBytes = new NativeList<byte>(4096, allocator);
            decoded = new NativeArray<int>(4, allocator);
        }

        /// <summary>Units known.</summary>
        public int Count => known.Length;

        /// <summary>Counts the payloads applied: a draw packed before the last change shows an older store.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// The indices whose health a payload set since the draw last took them (<see cref="ClientWorld"/> clears it): hits on units the
        /// draw left for a later turn are found the frame they land.
        /// </summary>
        internal NativeList<int> HealthTouched => healthTouched;
        private NativeList<int> healthTouched;

        /// <summary>The known slot by index (-1 when unknown), the route arena and each index's route (an off-view unit's place, exactly).</summary>
        internal NativeArray<int> KnownSlotArray => knownSlot;
        internal NativeArray<float2> Waypoints => arena.AsArray();
        internal NativeArray<int> RouteStartArray => routeStart;
        internal NativeArray<int> RouteCountArray => routeCount;

        /// <summary>Every owner the store has had units of (a handful), in the order first seen.</summary>
        public IReadOnlyList<int> Owners => owners;
        private readonly List<int> owners = new List<int>();
        private readonly HashSet<int> ownersSeen = new HashSet<int>();
        /// <summary>The id index of the i-th known unit.</summary>
        public int IndexAt(int i) => known[i];
        /// <summary>Known indices, densely packed (parallel to <see cref="Predicted"/>).</summary>
        public NativeArray<int> Known => known.AsArray();
        /// <summary>Positions from the last <see cref="PredictAll"/>, parallel to <see cref="Known"/>.</summary>
        public NativeArray<float2> Predicted { get { CompletePrediction(); return predicted.AsArray(); } }
        /// <summary>
        /// Where to draw each unit, parallel to <see cref="Known"/>: <see cref="Predicted"/> plus the fading
        /// remainder of its last correction, so units glide instead of stepping. Never use it for gameplay.
        /// </summary>
        public NativeArray<float2> Drawn { get { CompletePrediction(); return drawn.AsArray(); } }

        /// <summary>
        /// The drawn positions while <see cref="PredictAll"/>'s job may still be running: only for a job scheduled after
        /// <see cref="Prediction"/>. Their length is already the known count.
        /// </summary>
        internal NativeArray<float2> DrawnAfterPrediction => drawn.AsArray();

        /// <summary>
        /// Parallel to the known list, like <see cref="DrawnAfterPrediction"/>: where each unit is going without the correction glide (its
        /// route position, or the shown order's while one is), so its facing follows its way and not a glide back.
        /// </summary>
        internal NativeArray<float2> SteerAfterPrediction => steer.AsArray();

        /// <summary>
        /// Parallel to the known list, like <see cref="DrawnAfterPrediction"/>: 1 where the last <see cref="PredictAll"/> refreshed the
        /// slot, 0 where it kept the slot's earlier positions (a unit away from the view between its turns).
        /// </summary>
        internal NativeArray<byte> RefreshedAfterPrediction => refreshed.AsArray();

        /// <summary>
        /// Parallel to the known list, like <see cref="DrawnAfterPrediction"/>: how fast each drawn position moved since the slot's last
        /// refresh (tiles a second; zero for a jump or a new slot), so a draw can carry it on between refreshes.
        /// </summary>
        internal NativeArray<float2> VelocityAfterPrediction => velocity.AsArray();

        /// <summary>The prediction job <see cref="PredictAll"/> scheduled (default once complete).</summary>
        internal JobHandle Prediction => predicting;
        private JobHandle predicting;

        /// <summary>
        /// Waits for <see cref="PredictAll"/>'s job. Every read of the predicted or drawn positions does, and so does every change to the
        /// store, so the job overlaps only the frame's other work (2026-11-07: the main thread spent 0.6 ms a frame on it at 100,000 units).
        /// </summary>
        public void CompletePrediction()
        {
            predicting.Complete();
            predicting = default;
        }

        /// <summary>Owner id by index (for packing jobs).</summary>
        internal NativeArray<int> OwnerArray => owner;
        /// <summary>Health percent by index (for packing jobs).</summary>
        internal NativeArray<byte> HealthArray => health;

        /// <summary>Unit types by index (the draw job).</summary>
        internal NativeArray<byte> TypeArray => type;

        /// <summary>Prediction states by index; <c>Id</c> is the id known there (the draw job).</summary>
        internal NativeArray<PredictState> StateArray => states;
        /// <summary>Id capacity (indices are below this).</summary>
        public int Capacity => states.Length;

        /// <summary>Copies a known unit's remaining route (dev overlay); false when unknown.</summary>
        public bool CopyRoute(int index, List<float2> into)
        {
            into.Clear();
            if (!IsKnown(index)) return false;
            for (int i = 0; i < routeCount[index]; i++) into.Add(arena[routeStart[index] + i]);
            return true;
        }

        /// <summary>True while a known unit holds a route (it may already stand at the route's end; see <see cref="IsMoving"/>).</summary>
        public bool HasRoute(int index) => IsKnown(index) && routeCount[index] > 0;

        /// <summary>
        /// True while a known unit is still on its way: it has a route and its prediction still advances (a quarter second on
        /// from <paramref name="now"/>). A unit that stopped (a fresh spawn, one that arrived, one held at a corrected point)
        /// keeps its route on the client, so <see cref="HasRoute"/> alone counted it as moving (playtest 2026-11-02: "idle
        /// Builders" was always 0).
        /// </summary>
        public bool IsMoving(int index, float now)
        {
            if (!HasRoute(index)) return false;
            return math.distancesq(PredictOne(index, now), PredictOne(index, now + MovingLookAhead)) > 0.02f * 0.02f;
        }

        /// <summary>Seconds ahead <see cref="IsMoving"/> looks for the prediction to move.</summary>
        private const float MovingLookAhead = 0.25f;

        public bool IsKnown(int index) => (uint)index < (uint)knownSlot.Length && knownSlot[index] >= 0;
        /// <summary>The unit's place in the known list (and so in <see cref="Predicted"/>), or -1 when it isn't known.</summary>
        public int SlotOf(int index) => IsKnown(index) ? knownSlot[index] : -1;
        public int IdOf(int index) => IsKnown(index) ? states[index].Id : 0;
        public int OwnerOf(int index) => owner[index];
        public byte TypeOf(int index) => type[index];
        /// <summary>Health as a 0..1 fraction.</summary>
        public float Health01(int index) => health[index] / 100f;
        public PredictState StateOf(int index) => states[index];

        /// <summary>Predicts one unit at <paramref name="now"/> without changing its stored state.</summary>
        public float2 PredictOne(int index, float now)
        {
            PredictState s = states[index];
            return MovementPrediction.Predict(ref s, arena.AsArray(), routeStart[index], routeCount[index], now);
        }

        /// <summary>
        /// Decodes and applies one payload. Returns false (and leaves the remaining messages unapplied)
        /// when it is malformed; never throws.
        /// </summary>
        public bool Apply(ArraySegment<byte> payload, int tick)
        {
            Version++;
            CompletePrediction();
            FinishCompaction(wait: true);
            this.payload = payload;
            payloadCopied = false;
            using var reader = NetworkReaderPool.Get(payload);
            try
            {
                while (reader.Remaining > 0) ApplyOne(reader, tick);
                return true;
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is IndexOutOfRangeException || e is ArgumentException)
            {
                return false;
            }
            finally
            {
                if (arena.Length > 2 * liveWaypoints + 65536) StartCompaction();
            }
        }

        // Profiler markers (nearly free in a release player): where a batch's time goes.
        private static readonly Unity.Profiling.ProfilerMarker DecodeMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.DecodeMoveOrder"),
            RoutesMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.ApplyRoutes"), OtherMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.ApplyOther"),
            EnterMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.Enter"), LeaveMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.Leave"),
            CorrectionMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.Correction"), HealthMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.Health"),
            AttackMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.Attack");

        private void ApplyOne(NetworkReader reader, int batchTick)
        {
            MessageType kind = Messages.PeekType(reader);
            if (kind == MessageType.MoveOrder) { ApplyMessage(reader, batchTick); return; }
            using (OtherMarker.Auto()) ApplyMessage(reader, batchTick);
        }

        private void ApplyMessage(NetworkReader reader, int batchTick)
        {
            switch (Messages.PeekType(reader))
            {
                case MessageType.Enter:
                {
                    using var marker = EnterMarker.Auto();
                    enters.Clear(); waypoints.Clear();
                    Messages.DecodeEnter(reader, enters, waypoints);
                    foreach (EnterUnit u in enters) Enter(u, batchTick);
                    break;
                }
                case MessageType.Leave:
                {
                    using var marker = LeaveMarker.Auto();
                    leaves.Clear();
                    Messages.DecodeLeave(reader, leaves);
                    foreach (LeaveUnit u in leaves) Leave(u, batchTick);
                    break;
                }
                case MessageType.MoveOrder:
                {
                    int tick;
                    using (DecodeMarker.Auto()) tick = DecodeMoveOrder(reader);
                    using var applying = RoutesMarker.Auto();
                    ApplyRoutes(tick);
                    break;
                }
                case MessageType.Correction:
                {
                    using var marker = CorrectionMarker.Auto();
                    int tick = DecodeCorrection(reader, out byte scale);
                    ApplyCorrections(tick, scale);
                    break;
                }
                case MessageType.Health:
                {
                    using var marker = HealthMarker.Auto();
                    // Decoded and applied in a Burst job, as the corrections are.
                    CopyPayload();
                    new ApplyHealthJob { Bytes = payloadBytes.AsArray(), Start = reader.Position, Units = healthUnits, KnownSlot = knownSlot, Health = health, Result = decoded, Touched = healthTouched }.Run();
                    if (decoded[0] == 0) throw new InvalidDataException("malformed Health");
                    reader.Position = decoded[1];
                    break;
                }
                case MessageType.Attack:
                {
                    using var marker = AttackMarker.Auto();
                    attacks.Clear();
                    Messages.DecodeAttack(reader, attacks);
                    foreach (AttackEvent a in attacks)
                    {
                        // A building's attack (v0.13) names the building by id; nothing draws it yet (Task 5, after the art approval).
                        if (a.AttackerKind == AttackerKinds.Building) BuildingAttack?.Invoke(a.AttackerId, a.TargetId);
                        else if (IsKnown(a.AttackerIndex)) Attack?.Invoke(a.AttackerIndex, a.TargetId);
                    }
                    break;
                }
                default:
                    throw new InvalidDataException("unknown replication message");
            }
        }

        /// <summary>
        /// Decodes a MoveOrder straight into <see cref="routeUnits"/> and <see cref="routeTiles"/> in a Burst job
        /// (<see cref="Messages.TryDecodeMoveOrder"/>), moves the reader past it and returns its tick.
        /// </summary>
        private int DecodeMoveOrder(NetworkReader reader)
        {
            CopyPayload();
            routeUnits.Clear();
            routeTiles.Clear();
            new DecodeMoveOrderJob { Bytes = payloadBytes.AsArray(), Start = reader.Position, Units = routeUnits, Tiles = routeTiles, Result = decoded }.Run();
            if (decoded[0] == 0) throw new InvalidDataException("malformed MoveOrder");
            reader.Position = decoded[1];
            return decoded[2];
        }

        /// <summary>
        /// Decodes a Correction straight into <see cref="correctionUnits"/> in a Burst job (<see cref="Messages.TryDecodeCorrection"/>),
        /// moves the reader past it and returns its tick.
        /// </summary>
        private int DecodeCorrection(NetworkReader reader, out byte scale)
        {
            CopyPayload();
            correctionUnits.Clear();
            new DecodeCorrectionJob { Bytes = payloadBytes.AsArray(), Start = reader.Position, Units = correctionUnits, Result = decoded }.Run();
            if (decoded[0] == 0) throw new InvalidDataException("malformed Correction");
            reader.Position = decoded[1];
            scale = (byte)decoded[3];
            return decoded[2];
        }

        /// <summary>The batch's bytes in native memory for the Burst decoders, copied once a batch.</summary>
        private void CopyPayload()
        {
            if (payloadCopied) return;
            payloadBytes.ResizeUninitialized(payload.Count);
            NativeArray<byte>.Copy(payload.Array, payload.Offset, payloadBytes.AsArray(), 0, payload.Count);
            payloadCopied = true;
        }

        private NativeList<HealthUnit> healthUnits;

        [BurstCompile]
        private struct ApplyHealthJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Bytes;
            public int Start;
            public NativeList<HealthUnit> Units;
            [ReadOnly] public NativeArray<int> KnownSlot;
            public NativeArray<byte> Health;
            public NativeArray<int> Result;
            public NativeList<int> Touched;

            public void Execute()
            {
                int position = Start;
                Units.Clear();
                Result[0] = Messages.TryDecodeHealth(Bytes, ref position, Units) ? 1 : 0;
                Result[1] = position;
                if (Result[0] == 0) return;
                for (int i = 0; i < Units.Length; i++)
                {
                    HealthUnit h = Units[i];
                    if ((uint)h.Index >= (uint)KnownSlot.Length || KnownSlot[h.Index] < 0) continue;
                    Health[h.Index] = (byte)math.min((int)h.Health, 100);
                    Touched.Add(h.Index);
                }
            }
        }

        [BurstCompile]
        private struct DecodeCorrectionJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Bytes;
            public int Start;
            public NativeList<CorrectionUnit> Units;
            public NativeArray<int> Result;

            public void Execute()
            {
                int position = Start;
                bool ok = Messages.TryDecodeCorrection(Bytes, ref position, Units, out int tick, out byte scale);
                Result[0] = ok ? 1 : 0;
                Result[1] = position;
                Result[2] = tick;
                Result[3] = scale;
            }
        }

        private ArraySegment<byte> payload;
        private bool payloadCopied;
        private NativeList<byte> payloadBytes;
        private NativeArray<int> decoded; // ok, the position after, the tick, then 1 when a unit is named twice (MoveOrder) or the delta scale (Correction)

        [BurstCompile]
        private struct DecodeMoveOrderJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Bytes;
            public int Start;
            public NativeList<RouteUnit> Units;
            public NativeList<int2> Tiles;
            public NativeArray<int> Result;

            public void Execute()
            {
                int position = Start;
                bool ok = Messages.TryDecodeMoveOrder(Bytes, ref position, Units, Tiles, out int tick, out bool repeats);
                Result[0] = ok ? 1 : 0;
                Result[1] = position;
                Result[2] = tick;
                Result[3] = repeats ? 1 : 0;
            }
        }

        /// <summary>
        /// A MoveOrder's routes, over the job threads (profiled 2026-11-07: one thread took 6 to 7 ms of a frame when an order wave's
        /// 25,000 routes arrived at 100,000 known units). Every waypoint goes to the end of the arena, then per known unit, as before: its
        /// new route, its prediction restarted on it at the message's tick, the jump blended out of the drawn position, and a shown order
        /// ended. Each unit's job writes only its own unit; a message that names a unit twice runs in one batch, in order, so its last
        /// route wins as before. The live-route cap is checked for the whole message first, so a refused one changes nothing.
        /// </summary>
        private void ApplyRoutes(int tick)
        {
            int moveCount = routeUnits.Length, tileCount = routeTiles.Length;
            if (liveWaypoints + (long)tileCount > (long)knownSlot.Length * RouteMaintenance.MaxWaypoints)
                throw new InvalidDataException("the routes are longer than any server sends");
            int arenaBase = arena.Length;
            arena.ResizeUninitialized(arenaBase + tileCount);
            routeChanges.ResizeUninitialized(moveCount);
            JobHandle copied = new CopyWaypointsJob { Tiles = routeTiles.AsArray(), Arena = arena.AsArray(), Base = arenaBase }.Schedule(tileCount, 1024);
            JobHandle applied = new ApplyRoutesJob
            {
                Moves = routeUnits.AsArray(), ArenaBase = arenaBase, KnownSlot = knownSlot, States = states, RouteStart = routeStart,
                RouteCount = routeCount, Arena = arena.AsArray(), Blend = blend, Leads = leads, Changes = routeChanges.AsArray(), Now = Now,
                StartTime = tick * dt,
            }.Schedule(moveCount, decoded[3] != 0 ? math.max(1, moveCount) : 64, copied);
            counts[0] = liveWaypoints;
            counts[1] = leadCount;
            new SumRouteChangesJob { Changes = routeChanges.AsArray(), Counts = counts }.Schedule(applied).Complete();
            liveWaypoints = counts[0];
            leadCount = counts[1];
        }

        /// <summary>
        /// A Correction's units, in a Burst job (profiled 2026-11-07: the managed loop, two predictions per unit, took 1.5 to 3 ms of a
        /// frame at 100,000 known units). Per known unit, as before: its prediction corrected at the message's tick, the jump blended
        /// out of the drawn position, and a shown halt ended once the server has it stopped.
        /// </summary>
        private void ApplyCorrections(int tick, byte scale)
        {
            counts[1] = leadCount;
            new ApplyCorrectionsJob
            {
                Corrections = correctionUnits.AsArray(), KnownSlot = knownSlot, States = states, RouteStart = routeStart, RouteCount = routeCount,
                Arena = arena.AsArray(), Blend = blend, Leads = leads, Counts = counts, Now = Now, At = tick * dt, Scale = scale,
            }.Run();
            leadCount = counts[1];
        }

        private NativeList<CorrectionUnit> correctionUnits;

        [BurstCompile(FloatMode = FloatMode.Deterministic)]
        private struct ApplyCorrectionsJob : IJob
        {
            [ReadOnly] public NativeArray<CorrectionUnit> Corrections;
            [ReadOnly] public NativeArray<int> KnownSlot;
            public NativeArray<PredictState> States;
            [ReadOnly] public NativeArray<int> RouteStart, RouteCount;
            [ReadOnly] public NativeArray<float2> Arena;
            public NativeArray<float2> Blend;
            public NativeArray<OrderLead> Leads;
            public NativeArray<int> Counts;
            public float Now, At;
            public byte Scale;

            public void Execute()
            {
                for (int k = 0; k < Corrections.Length; k++)
                {
                    CorrectionUnit c = Corrections[k];
                    int index = c.Index;
                    if ((uint)index >= (uint)KnownSlot.Length || KnownSlot[index] < 0) continue;
                    float2 before = Predict(index);
                    PredictState s = States[index];
                    MovementPrediction.ApplyCorrection(ref s, Arena, RouteStart[index], RouteCount[index], At, c, Scale);
                    States[index] = s;
                    if (Now > 0f) Keep(index, Blend[index] + before - Predict(index)); // Blend
                    if (c.Speed == 0 && Leads[index].Kind == OrderLead.Halted) // EndLead
                    {
                        Keep(index, Leads[index].At - Predict(index));
                        Counts[1]--;
                        Leads[index] = default;
                    }
                }
            }

            private float2 Predict(int index)
            {
                PredictState s = States[index];
                return MovementPrediction.Predict(ref s, Arena, RouteStart[index], RouteCount[index], Now);
            }

            private void Keep(int index, float2 offset) => Blend[index] = math.lengthsq(offset) > MaxBlendTiles * MaxBlendTiles ? float2.zero : offset;
        }

        private NativeList<RouteUnit> routeUnits;
        private NativeList<int2> routeTiles;
        private NativeList<int2> routeChanges; // per move: the live waypoints added, and 1 when a shown order ended
        private NativeArray<int> counts; // the live waypoints and running leads

        [BurstCompile]
        private struct CopyWaypointsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int2> Tiles; // RouteWire half tiles
            [NativeDisableParallelForRestriction] public NativeArray<float2> Arena;
            public int Base;

            public void Execute(int k) => Arena[Base + k] = RouteWire.Decode(Tiles[k]);
        }

        [BurstCompile(FloatMode = FloatMode.Deterministic)]
        private struct ApplyRoutesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RouteUnit> Moves;
            public int ArenaBase;
            [ReadOnly] public NativeArray<int> KnownSlot;
            [NativeDisableParallelForRestriction] public NativeArray<PredictState> States;
            [NativeDisableParallelForRestriction] public NativeArray<int> RouteStart, RouteCount;
            [ReadOnly] public NativeArray<float2> Arena;
            [NativeDisableParallelForRestriction] public NativeArray<float2> Blend;
            [NativeDisableParallelForRestriction] public NativeArray<OrderLead> Leads;
            public NativeArray<int2> Changes;
            public float Now, StartTime;

            public void Execute(int m)
            {
                RouteUnit u = Moves[m];
                int index = u.Index;
                Changes[m] = int2.zero;
                if ((uint)index >= (uint)KnownSlot.Length || KnownSlot[index] < 0) return;
                float2 before = Predict(index);
                int added = u.Count - RouteCount[index], ended = 0;
                RouteStart[index] = ArenaBase + u.First;
                RouteCount[index] = u.Count;
                PredictState s = States[index];
                MovementPrediction.StartRoute(ref s, Arena[RouteStart[index]], StartTime);
                States[index] = s;
                if (Now > 0f) Keep(index, Blend[index] + before - Predict(index)); // Blend
                if (Leads[index].Kind != 0) // EndLead
                {
                    Keep(index, Leads[index].At - Predict(index));
                    ended = 1;
                    Leads[index] = default;
                }
                Changes[m] = new int2(added, ended);
            }

            private float2 Predict(int index)
            {
                PredictState s = States[index];
                return MovementPrediction.Predict(ref s, Arena, RouteStart[index], RouteCount[index], Now);
            }

            private void Keep(int index, float2 offset) => Blend[index] = math.lengthsq(offset) > MaxBlendTiles * MaxBlendTiles ? float2.zero : offset;
        }

        [BurstCompile]
        private struct SumRouteChangesJob : IJob
        {
            [ReadOnly] public NativeArray<int2> Changes;
            public NativeArray<int> Counts;

            public void Execute()
            {
                int2 sum = 0;
                for (int m = 0; m < Changes.Length; m++) sum += Changes[m];
                Counts[0] += sum.x;
                Counts[1] -= sum.y;
            }
        }

        /// <summary>Adds the jump between where the unit was drawn from (<paramref name="before"/>) and its new prediction to its blend.</summary>
        private void Blend(int index, float2 before)
        {
            if (Now <= 0f) return;
            float2 offset = blend[index] + before - PredictOne(index, Now);
            blend[index] = math.lengthsq(offset) > MaxBlendTiles * MaxBlendTiles ? float2.zero : offset;
        }

        private void Enter(in EnterUnit u, int tick)
        {
            int index = NetIdAllocator.IndexOf(u.Id);
            if (index >= states.Length) throw new InvalidDataException("unit index beyond the client's capacity");
            CheckRouteRoom(index, u.Count); // before anything changes: a refused Enter leaves no half-known unit
            blend[index] = float2.zero;
            ClearLead(index);
            bool appeared = !IsKnown(index);
            if (appeared)
            {
                knownSlot[index] = known.Length;
                known.Add(index);
                unsorted++;
            }
            if (knownSlot[index] < slotIndex.Length) slotIndex[knownSlot[index]] = -1; // predicted afresh, wherever it was drawn
            owner[index] = u.OwnerId;
            if (ownersSeen.Add(u.OwnerId)) owners.Add(u.OwnerId);
            type[index] = u.Type;
            health[index] = (byte)math.min((int)u.Health, 100);
            SetRoute(index, u.First, u.Count);
            float fullSpeed = UnitSpeeds.Of(u.Type);
            float2 anchor = new float2(Quantise.Decode(u.X), Quantise.Decode(u.Y));
            int resume = MovementPrediction.ProjectedResume(arena.AsArray(), routeStart[index], routeCount[index], 0, anchor);
            states[index] = new PredictState
            {
                Id = u.Id,
                Anchored = 1,
                Anchor = anchor,
                AnchorTime = tick * dt,
                Index = math.max(0, resume - 1),
                LastPredicted = anchor,
                FullSpeed = fullSpeed,
                Speed = fullSpeed,
                LastHealth = u.Health,
            };
            if (appeared) Appeared?.Invoke(index, anchor);
        }

        private void Leave(in LeaveUnit u, int tick)
        {
            if (!IsKnown(u.Index)) return;
            if (LeaveReasons.IsDeath(u.Reason))
            {
                LastDeathReason = u.Reason;
                Died?.Invoke(u.Index, PredictOne(u.Index, tick * dt));
            }
            Forgotten?.Invoke(u.Index);
            int slot = knownSlot[u.Index];
            int last = known[known.Length - 1];
            unsorted++;
            known[slot] = last;
            knownSlot[last] = slot;
            known.RemoveAt(known.Length - 1);
            knownSlot[u.Index] = -1;
            liveWaypoints -= routeCount[u.Index];
            routeCount[u.Index] = 0;
            states[u.Index] = default;
            blend[u.Index] = float2.zero;
            ClearLead(u.Index);
        }

        /// <summary>
        /// Draws a known unit setting off for <paramref name="goal"/> at once, until it is within <paramref name="stopTiles"/> of it, instead of
        /// after the order's round trip to the server. Drawing only: <see cref="Predicted"/> stays on the server's route, the server's route
        /// ends this, and the unit glides onto it.
        /// </summary>
        public void ShowMove(int index, float2 goal, float stopTiles) => StartLead(index, OrderLead.Going, goal, stopTiles);

        /// <summary>Draws a known unit stopped at once, until the server's correction says it has stopped. Drawing only.</summary>
        public void ShowHalt(int index) => StartLead(index, OrderLead.Halted, default, 0f);

        private void StartLead(int index, byte kind, float2 goal, float stopTiles)
        {
            CompletePrediction();
            if (!IsKnown(index)) return;
            float now = math.max(0f, lastPredictTime);
            bool running = leads[index].Kind != 0;
            float2 at = running ? leads[index].At : PredictOne(index, now) + blend[index];
            leads[index] = new OrderLead { At = at, Goal = goal, StopTiles = stopTiles, Left = MaxLeadTiles, Until = now + LeadSeconds, Kind = kind };
            blend[index] = float2.zero;
            if (!running) leadCount++;
        }

        /// <summary>Ends a shown order: the unit glides from where it is drawn onto its prediction.</summary>
        private void EndLead(int index, float now)
        {
            if (leads[index].Kind == 0) return;
            float2 offset = leads[index].At - PredictOne(index, now);
            blend[index] = math.lengthsq(offset) > MaxBlendTiles * MaxBlendTiles ? float2.zero : offset;
            ClearLead(index);
        }

        private void ClearLead(int index)
        {
            if (leads[index].Kind != 0) leadCount--;
            leads[index] = default;
        }

        private void AdvanceLeads(float now, float elapsed)
        {
            if (leadCount == 0) return;
            for (int i = 0; i < known.Length; i++)
            {
                int index = known[i];
                OrderLead lead = leads[index];
                if (lead.Kind == 0) continue;
                if (now >= lead.Until) { EndLead(index, now); continue; }
                if (lead.Kind != OrderLead.Going) continue;
                float2 toGoal = lead.Goal - lead.At;
                float distance = math.length(toGoal);
                float step = math.min(states[index].FullSpeed * LeadShare * elapsed, math.min(lead.Left, distance - lead.StopTiles));
                if (step <= 0f) continue;
                float2 next = lead.At + toGoal / distance * step;
                if (LeadOpen != null && !LeadOpen(next)) continue;
                lead.At = next;
                lead.Left -= step;
                leads[index] = lead;
            }
        }

        /// <summary>
        /// Copy steps make long routes cheap on the wire: the live total stays at most what a real server can send (every id at its
        /// longest route), so a hostile host or recording can't make the client allocate more. Throws before anything changes.
        /// </summary>
        private void CheckRouteRoom(int index, int count)
        {
            if (liveWaypoints + count - routeCount[index] > (long)knownSlot.Length * RouteMaintenance.MaxWaypoints)
                throw new InvalidDataException("the routes are longer than any server sends");
        }

        private void SetRoute(int index, int first, int count)
        {
            CheckRouteRoom(index, count);
            liveWaypoints += count - routeCount[index];
            routeStart[index] = arena.Length;
            routeCount[index] = count;
            for (int i = 0; i < count; i++) arena.Add(RouteWire.Decode(waypoints[first + i]));
        }

        /// <summary>Waypoints in the route arena, live or not (tests: it shrinks when a compaction is swapped in).</summary>
        internal int ArenaLength => arena.Length;

        private readonly Allocator allocator;
        private NativeList<float2> compacted;
        private NativeArray<int> compactedStart;
        private JobHandle compacting;
        private bool compactionRunning;

        /// <summary>
        /// Starts copying the known units' routes to the front of a new arena over the job threads, once a batch has left the arena more
        /// than twice its live waypoints (it ran on the main thread: 13 to 17 ms of a frame every few order waves at 100,000 units, the
        /// slowest frames of the 100k benchmark). The new starts are a prefix sum here (a tenth of a millisecond); the copy runs in small
        /// pieces, so no thread holds one long job. The prediction keeps reading the old arena meanwhile, as the job does (both only read
        /// it); <see cref="FinishCompaction"/> swaps the new one in before anything changes a route.
        /// </summary>
        private void StartCompaction()
        {
            if (compactionRunning) return;
            compactedStart = new NativeArray<int>(routeStart.Length, allocator, NativeArrayOptions.UninitializedMemory);
            new CompactStartsJob { Known = known.AsArray(), RouteStart = routeStart, RouteCount = routeCount, NewStart = compactedStart, Total = counts }.Run();
            int total = counts[0];
            compacted = new NativeList<float2>(math.max(math.max(arena.Capacity, total), 2 * liveWaypoints + 131072), allocator);
            compacted.ResizeUninitialized(total);
            compacting = new CompactJob
            {
                Known = known.AsArray(), RouteStart = routeStart, RouteCount = routeCount, NewStart = compactedStart, Arena = arena.AsArray(), Compacted = compacted.AsArray(),
            }.Schedule(known.Length, 256);
            JobHandle.ScheduleBatchedJobs();
            compactionRunning = true;
        }

        /// <summary>
        /// Swaps in the compacted arena and route starts: waiting for the job before a batch changes routes, else only once it is done.
        /// The old arena is freed on a job thread (freeing it took a millisecond of the main thread).
        /// </summary>
        private void FinishCompaction(bool wait)
        {
            if (!compactionRunning || (!wait && !compacting.IsCompleted)) return;
            compacting.Complete();
            compactionRunning = false;
            arena.Dispose(default(JobHandle));
            arena = compacted;
            (routeStart, compactedStart) = (compactedStart, routeStart);
            compactedStart.Dispose();
        }

        /// <summary>Each known unit's route start in the compacted arena, in known order (others keep theirs), and the total.</summary>
        [BurstCompile]
        private struct CompactStartsJob : IJob
        {
            [ReadOnly] public NativeArray<int> Known, RouteStart, RouteCount;
            public NativeArray<int> NewStart;
            public NativeArray<int> Total;

            public void Execute()
            {
                NewStart.CopyFrom(RouteStart);
                int total = 0;
                for (int i = 0; i < Known.Length; i++)
                {
                    int index = Known[i];
                    NewStart[index] = total;
                    total += RouteCount[index];
                }
                Total[0] = total;
            }
        }

        /// <summary>Copies each known unit's route to its new start (Burst, over the job threads: each unit writes only its own range).</summary>
        [BurstCompile]
        private struct CompactJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Known, RouteStart, RouteCount, NewStart;
            [ReadOnly] public NativeArray<float2> Arena;
            [NativeDisableParallelForRestriction] public NativeArray<float2> Compacted;

            public void Execute(int i)
            {
                int index = Known[i];
                NativeArray<float2>.Copy(Arena, RouteStart[index], Compacted, NewStart[index], RouteCount[index]);
            }
        }

        /// <summary>Known units from which the list is kept in index order.</summary>
        private const int SortFrom = 4096;

        /// <summary>
        /// Puts the known list back in index order, its slots' positions with it (2026-11-08: the draw jobs read a dozen arrays by index for
        /// each unit, and in the order units arrived and left those reads were all cache misses; in index order they run forward).
        /// </summary>
        private void SortKnown()
        {
            int n = known.Length;
            var order = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var pairs = new NativeArray<float2>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var ints = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var floats = new NativeArray<float>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var bytes = new NativeArray<byte>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new SortKnownJob
            {
                Known = known.AsArray(), KnownSlot = knownSlot, Order = order,
                Predicted = predicted.AsArray(), Drawn = drawn.AsArray(), Steer = steer.AsArray(), SlotDrawn = slotDrawn.AsArray(), Velocity = velocity.AsArray(),
                SlotIndex = slotIndex.AsArray(), SlotAt = slotAt.AsArray(), Refreshed = refreshed.AsArray(),
                Pairs = pairs, Ints = ints, Floats = floats, Bytes = bytes,
            }.Run();
            order.Dispose(); pairs.Dispose(); ints.Dispose(); floats.Dispose(); bytes.Dispose();
            unsorted = 0;
        }

        [BurstCompile]
        private struct SortKnownJob : IJob
        {
            public NativeArray<int> Known, KnownSlot, Order;
            public NativeArray<float2> Predicted, Drawn, Steer, SlotDrawn, Velocity;
            public NativeArray<int> SlotIndex;
            public NativeArray<float> SlotAt;
            public NativeArray<byte> Refreshed;
            // Scratch, one per element type.
            public NativeArray<float2> Pairs;
            public NativeArray<int> Ints;
            public NativeArray<float> Floats;
            public NativeArray<byte> Bytes;

            public void Execute()
            {
                Known.Sort();
                for (int j = 0; j < Known.Length; j++) Order[j] = KnownSlot[Known[j]]; // the slot each unit had
                Move(Predicted, Pairs); Move(Drawn, Pairs); Move(Steer, Pairs); Move(SlotDrawn, Pairs); Move(Velocity, Pairs);
                Move(SlotIndex, Ints); Move(SlotAt, Floats); Move(Refreshed, Bytes);
                for (int j = 0; j < Known.Length; j++) KnownSlot[Known[j]] = j;
            }

            private void Move<T>(NativeArray<T> slots, NativeArray<T> scratch) where T : unmanaged
            {
                for (int j = 0; j < Order.Length; j++) scratch[j] = slots[Order[j]];
                NativeArray<T>.Copy(scratch, slots, Order.Length);
            }
        }

        /// <summary>
        /// Predicts every known unit at <paramref name="now"/> in a Burst job over the job threads (stored states unchanged), left running:
        /// see <see cref="CompletePrediction"/>.
        /// </summary>
        public void PredictAll(float now) => PredictAll(now, new float4(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue), 0f, 0f);

        /// <summary>
        /// Like <see cref="PredictAll(float)"/>, but a unit last drawn outside <paramref name="cull"/> (xMin, yMin, xMax, yMax: the view
        /// with a margin) keeps its slot's positions until its turn: a share of the known list each frame, so each unit is refreshed at
        /// least every <paramref name="refreshSeconds"/>. 2026-11-08: at 100,000 known units about 14,000 are on screen, and predicting
        /// the rest every frame cost a client on three cores more than any other work. A unit drawn inside <paramref name="cull"/> also gets
        /// the velocity that takes its drawn position to where it will be <paramref name="aheadSeconds"/> on (<see cref="VelocityAfterPrediction"/>).
        /// </summary>
        public void PredictAll(float now, float4 cull, float refreshSeconds, float aheadSeconds)
        {
            CompletePrediction();
            FinishCompaction(wait: false);
            int n = known.Length, had = slotIndex.Length;
            predicted.ResizeUninitialized(n);
            drawn.ResizeUninitialized(n);
            steer.ResizeUninitialized(n);
            slotIndex.ResizeUninitialized(n);
            slotAt.ResizeUninitialized(n);
            refreshed.ResizeUninitialized(n);
            slotDrawn.ResizeUninitialized(n);
            velocity.ResizeUninitialized(n);
            for (int i = had; i < n; i++) slotIndex[i] = -1;
            if (unsorted > n / 8 && n >= SortFrom)
            {
                FinishCompaction(wait: true); // the compaction job reads the known list
                SortKnown();
            }
            float elapsed = lastPredictTime < 0f ? 0f : math.max(0f, now - lastPredictTime);
            lastPredictTime = now;
            AdvanceLeads(now, elapsed);
            int turn = refreshSeconds > 0f ? math.min(n, (int)math.ceil(n * elapsed / refreshSeconds)) : n;
            int first = n > 0 ? refreshCursor % n : 0;
            refreshCursor = n > 0 ? (first + turn) % n : 0;
            predicting = new PredictJob
            {
                Known = known.AsArray(),
                States = states,
                RouteStart = routeStart,
                RouteCount = routeCount,
                Waypoints = arena.AsArray(),
                Now = now,
                Positions = predicted.AsArray(),
                Blend = blend,
                BlendKeep = math.exp(-elapsed / BlendSeconds),
                Elapsed = elapsed,
                Leads = leads,
                AnyLeads = leadCount > 0,
                Drawn = drawn.AsArray(),
                Steer = steer.AsArray(),
                Cull = cull,
                TurnFirst = first,
                TurnCount = turn,
                SlotIndex = slotIndex.AsArray(),
                SlotAt = slotAt.AsArray(),
                Refreshed = refreshed.AsArray(),
                SlotDrawn = slotDrawn.AsArray(),
                Velocity = velocity.AsArray(),
                Ahead = aheadSeconds,
                Cursors = cursors,
            }.Schedule(n, 256);
            JobHandle.ScheduleBatchedJobs();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CompletePrediction();
            FinishCompaction(wait: true);
            states.Dispose(); routeStart.Dispose(); routeCount.Dispose(); owner.Dispose(); type.Dispose(); health.Dispose();
            arena.Dispose(); known.Dispose(); knownSlot.Dispose(); predicted.Dispose(); blend.Dispose(); drawn.Dispose(); steer.Dispose(); leads.Dispose();
            slotIndex.Dispose(); slotAt.Dispose(); refreshed.Dispose(); slotDrawn.Dispose(); velocity.Dispose(); cursors.Dispose();
            routeUnits.Dispose(); routeTiles.Dispose(); counts.Dispose(); correctionUnits.Dispose(); healthUnits.Dispose(); healthTouched.Dispose();
            routeChanges.Dispose(); payloadBytes.Dispose(); decoded.Dispose();
        }

        /// <summary>A unit's place on its route leg at its last draw prediction: the waypoint it had passed and the distance to it.</summary>
        private struct RouteCursor
        {
            public float StartTime, Before;
            public int First, Count, Leg, Waypoint;
        }

        /// <summary>An own order drawn before the server answers it.</summary>
        private struct OrderLead
        {
            public const byte Going = 1, Halted = 2;
            public float2 At, Goal;
            /// <summary>How near the goal it stops, how many tiles it may still go ahead, and when it gives up waiting.</summary>
            public float StopTiles, Left, Until;
            /// <summary>0 when no order is shown.</summary>
            public byte Kind;
        }

        [BurstCompile(FloatMode = FloatMode.Deterministic)]
        private struct PredictJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Known;
            [ReadOnly] public NativeArray<PredictState> States;
            [ReadOnly] public NativeArray<int> RouteStart, RouteCount;
            [ReadOnly] public NativeArray<float2> Waypoints;
            public float Now;
            public NativeArray<float2> Positions;
            [NativeDisableParallelForRestriction] public NativeArray<float2> Blend; // by index; each index is in one slot
            public float BlendKeep, Elapsed;
            [ReadOnly] public NativeArray<OrderLead> Leads;
            public NativeArray<float2> Drawn;
            public NativeArray<float2> Steer;
            public float4 Cull;
            /// <summary>The slots refreshed this frame wherever they are: <see cref="TurnCount"/> from <see cref="TurnFirst"/>, wrapping.</summary>
            public int TurnFirst, TurnCount;
            public NativeArray<int> SlotIndex;
            public NativeArray<float> SlotAt;
            public bool AnyLeads;
            public NativeArray<byte> Refreshed;
            public NativeArray<float2> SlotDrawn, Velocity;
            /// <summary>Seconds to the next refresh the velocity aims at (0: no velocity but a shown order's).</summary>
            public float Ahead;
            [NativeDisableParallelForRestriction] public NativeArray<RouteCursor> Cursors; // by index; each index is in one slot

            public void Execute(int i)
            {
                // The wait test reads only arrays in slot order: a unit's state is a cache miss, and most units wait (2026-11-08).
                int index = Known[i];
                bool same = SlotIndex[i] == index;
                if (same && (i - TurnFirst + Known.Length) % Known.Length >= TurnCount && !(AnyLeads && Leads[index].Kind != 0))
                {
                    float2 last = Drawn[i];
                    if (last.x < Cull.x || last.y < Cull.y || last.x >= Cull.z || last.y >= Cull.w)
                    {
                        Refreshed[i] = 0;
                        return;
                    }
                }
                // A slot left waiting fades its glide by the time since its last turn.
                float elapsed = same ? math.max(0f, Now - SlotAt[i]) : Elapsed;
                float keep = elapsed == Elapsed ? BlendKeep : math.exp(-elapsed / BlendSeconds);
                PredictState s = States[index];
                SlotIndex[i] = index;
                SlotAt[i] = Now;
                Refreshed[i] = 1;
                float2 p = Draw(index, s, Now, true);
                float2 b = Blend[index];
                float length = math.length(b);
                if (length > 0f)
                {
                    float fade = length * (1f - keep), walk = math.max(s.FullSpeed * CatchUpShare, length / CatchUpSeconds) * elapsed;
                    b *= (length - math.min(fade, walk)) / length;
                    if (math.lengthsq(b) < 1e-8f) b = float2.zero;
                }
                Blend[index] = b;
                Positions[i] = p;
                bool lead = Leads[index].Kind != 0;
                float2 drawn = lead ? Leads[index].At : p + b;
                Drawn[i] = drawn;
                Steer[i] = lead ? Leads[index].At : p;
                float2 velocity = float2.zero;
                if (lead)
                {
                    // A shown order moves frame by frame: its speed since the slot's last refresh (more than a tile is a jump).
                    float2 moved = drawn - SlotDrawn[i];
                    if (same && elapsed > 0f && math.lengthsq(moved) <= 1f) velocity = moved / elapsed;
                }
                else if (Ahead > 0f && drawn.x >= Cull.x && drawn.y >= Cull.y && drawn.x < Cull.z && drawn.y < Cull.w)
                {
                    // Toward where the unit will be drawn at the next refresh, so the draws between refreshes follow its route.
                    float2 later = Draw(index, s, Now + Ahead, false);
                    if (length > 0f) later += b * math.exp(-Ahead / BlendSeconds);
                    velocity = (later - drawn) / Ahead;
                }
                Velocity[i] = velocity;
                SlotDrawn[i] = drawn;
            }

            /// <summary>
            /// <see cref="MovementPrediction.Predict"/> for drawing: the same route position, but walked on from the waypoint the unit's last
            /// draw passed instead of from its leg's start (2026-11-08: a unit long on its way walked every waypoint behind it each frame).
            /// The sums differ from the server's in the last bits, which no draw shows. <paramref name="keep"/> stores the place reached.
            /// </summary>
            private float2 Draw(int index, PredictState s, float now, bool keep)
            {
                int first = RouteStart[index], count = RouteCount[index];
                if (count <= 0) return s.Anchor;
                if (s.Anchored != 0)
                {
                    // The straight leg out of a corrected point, as Predict has it.
                    int target = s.Index + 1;
                    if (target >= count) return s.Anchor;
                    float2 to = Waypoints[first + target], segment = to - s.Anchor;
                    float length = math.length(segment), remaining = math.max(0f, s.Speed * (now - s.AnchorTime));
                    if (length <= 0f || remaining <= length || s.Speed <= 0f)
                        return length > 0f ? s.Anchor + segment * (math.min(remaining, length) / length) : s.Anchor;
                    s.Index = target;
                    s.StartTime = s.AnchorTime + length / s.Speed;
                }
                int leg = math.clamp(s.Index, 0, count - 1);
                float travelled = math.max(0f, s.Speed * (now - s.StartTime));
                RouteCursor c = Cursors[index];
                bool resume = c.StartTime == s.StartTime && c.First == first && c.Count == count && c.Leg == leg && c.Waypoint >= leg && c.Waypoint < count
                    && c.Before <= travelled;
                int k = resume ? c.Waypoint : leg;
                float before = resume ? c.Before : 0f;
                float2 from = Waypoints[first + k];
                float span = 0f;
                for (; k + 1 < count; k++)
                {
                    span = math.length(Waypoints[first + k + 1] - from);
                    if (travelled - before <= span) break;
                    before += span;
                    from = Waypoints[first + k + 1];
                }
                if (keep) Cursors[index] = new RouteCursor { StartTime = s.StartTime, Before = before, First = first, Count = count, Leg = leg, Waypoint = k };
                if (k + 1 >= count) return from;
                return span > 0f ? from + (Waypoints[first + k + 1] - from) * ((travelled - before) / span) : from;
            }
        }
    }

    /// <summary>Full speed per unit type on the client (the speed byte's scale), from config.</summary>
    public static class UnitSpeeds
    {
        private static float[] speeds;

        public static float Of(byte type)
        {
            if (speeds == null)
            {
                Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
                speeds = new float[Enum.GetValues(typeof(UnitType)).Length];
                foreach (var pair in config.Units) if ((int)pair.Key < speeds.Length) speeds[(int)pair.Key] = pair.Value.MoveSpeed;
            }
            return type < speeds.Length ? speeds[type] : 0f;
        }

        /// <summary>Drops the cached table (tests that change the config).</summary>
        public static void Reset() => speeds = null;
    }
}
