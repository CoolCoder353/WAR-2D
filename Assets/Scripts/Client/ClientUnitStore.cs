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
        private readonly List<RouteUnit> moves = new List<RouteUnit>();
        private readonly List<CorrectionUnit> corrections = new List<CorrectionUnit>();
        private readonly List<HealthUnit> healths = new List<HealthUnit>();
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
        /// <summary>Raised when a unit the client didn't know becomes known: (index, where it is). Own units are always known, so for them it means a new unit.</summary>
        public event Action<int, float2> Appeared;

        public ClientUnitStore(int idCapacity, float tickSeconds, Allocator allocator = Allocator.Persistent)
        {
            dt = tickSeconds;
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
            leads = new NativeArray<OrderLead>(idCapacity, allocator);
            routeUnits = new NativeList<RouteUnit>(64, allocator);
            routeTiles = new NativeList<int2>(1024, allocator);
            counts = new NativeArray<int>(2, allocator);
        }

        /// <summary>Units known.</summary>
        public int Count => known.Length;
        /// <summary>The id index of the i-th known unit.</summary>
        public int IndexAt(int i) => known[i];
        /// <summary>Known indices, densely packed (parallel to <see cref="Predicted"/>).</summary>
        public NativeArray<int> Known => known.AsArray();
        /// <summary>Positions from the last <see cref="PredictAll"/>, parallel to <see cref="Known"/>.</summary>
        public NativeArray<float2> Predicted => predicted.AsArray();
        /// <summary>
        /// Where to draw each unit, parallel to <see cref="Known"/>: <see cref="Predicted"/> plus the fading
        /// remainder of its last correction, so units glide instead of stepping. Never use it for gameplay.
        /// </summary>
        public NativeArray<float2> Drawn => drawn.AsArray();

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
        }

        // Profiler markers (nearly free in a release player): where a batch's time goes.
        private static readonly Unity.Profiling.ProfilerMarker DecodeMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.DecodeMoveOrder"),
            RoutesMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.ApplyRoutes"), OtherMarker = new Unity.Profiling.ProfilerMarker("ClientUnitStore.ApplyOther");

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
                    enters.Clear(); waypoints.Clear();
                    Messages.DecodeEnter(reader, enters, waypoints);
                    foreach (EnterUnit u in enters) Enter(u, batchTick);
                    break;
                case MessageType.Leave:
                    leaves.Clear();
                    Messages.DecodeLeave(reader, leaves);
                    foreach (LeaveUnit u in leaves) Leave(u, batchTick);
                    break;
                case MessageType.MoveOrder:
                {
                    moves.Clear(); waypoints.Clear();
                    int tick;
                    using (DecodeMarker.Auto()) tick = Messages.DecodeMoveOrder(reader, moves, waypoints);
                    using var applying = RoutesMarker.Auto();
                    ApplyRoutes(tick);
                    break;
                }
                case MessageType.Correction:
                {
                    corrections.Clear();
                    int tick = Messages.DecodeCorrection(reader, corrections, out byte scale);
                    foreach (CorrectionUnit c in corrections)
                    {
                        if (!IsKnown(c.Index)) continue;
                        float2 before = PredictOne(c.Index, Now);
                        PredictState s = states[c.Index];
                        MovementPrediction.ApplyCorrection(ref s, arena.AsArray(), routeStart[c.Index], routeCount[c.Index], tick * dt, c, scale);
                        states[c.Index] = s;
                        Blend(c.Index, before);
                        if (c.Speed == 0 && leads[c.Index].Kind == OrderLead.Halted) EndLead(c.Index, Now);
                    }
                    break;
                }
                case MessageType.Health:
                    healths.Clear();
                    Messages.DecodeHealth(reader, healths);
                    foreach (HealthUnit h in healths) if (IsKnown(h.Index)) health[h.Index] = (byte)math.min((int)h.Health, 100);
                    break;
                case MessageType.Attack:
                    attacks.Clear();
                    Messages.DecodeAttack(reader, attacks);
                    foreach (AttackEvent a in attacks)
                    {
                        // A building's attack (v0.13) names the building by id; nothing draws it yet (Task 5, after the art approval).
                        if (a.AttackerKind == AttackerKinds.Building) BuildingAttack?.Invoke(a.AttackerId, a.TargetId);
                        else if (IsKnown(a.AttackerIndex)) Attack?.Invoke(a.AttackerIndex, a.TargetId);
                    }
                    break;
                default:
                    throw new InvalidDataException("unknown replication message");
            }
        }

        /// <summary>
        /// A MoveOrder's routes, in a Burst job (profiled 2026-11-07: the managed loop took 3 to 6 ms of a frame when an order's
        /// 2,500 routes arrived at 10,000 units). Per known unit, as before: its new route, its prediction restarted on it at the
        /// message's tick, the jump blended out of the drawn position, and a shown order ended. The live-route cap is checked for the
        /// whole message first, so a refused one changes nothing.
        /// </summary>
        private void ApplyRoutes(int tick)
        {
            long adding = 0;
            foreach (RouteUnit u in moves) adding += u.Count;
            if (liveWaypoints + adding > (long)knownSlot.Length * RouteMaintenance.MaxWaypoints)
                throw new InvalidDataException("the routes are longer than any server sends");
            // Copy the decoded lists across (both memcpy).
            if (routeUnitBuffer.Length < moves.Count) routeUnitBuffer = new RouteUnit[math.ceilpow2(moves.Count)];
            if (routeTileBuffer.Length < waypoints.Count) routeTileBuffer = new int2[math.ceilpow2(waypoints.Count)];
            moves.CopyTo(routeUnitBuffer);
            waypoints.CopyTo(routeTileBuffer);
            routeUnits.ResizeUninitialized(moves.Count);
            routeTiles.ResizeUninitialized(waypoints.Count);
            NativeArray<RouteUnit>.Copy(routeUnitBuffer, routeUnits.AsArray(), moves.Count);
            NativeArray<int2>.Copy(routeTileBuffer, routeTiles.AsArray(), waypoints.Count);
            counts[0] = liveWaypoints;
            counts[1] = leadCount;
            new ApplyRoutesJob
            {
                Moves = routeUnits.AsArray(), Waypoints = routeTiles.AsArray(), KnownSlot = knownSlot, States = states, RouteStart = routeStart,
                RouteCount = routeCount, Arena = arena, Blend = blend, Leads = leads, Counts = counts, Now = Now, StartTime = tick * dt,
            }.Run();
            liveWaypoints = counts[0];
            leadCount = counts[1];
            if (arena.Length > 2 * liveWaypoints + 65536) Compact();
        }

        private NativeList<RouteUnit> routeUnits;
        private NativeList<int2> routeTiles;
        private NativeArray<int> counts; // the job's live waypoints and running leads
        private RouteUnit[] routeUnitBuffer = new RouteUnit[64];
        private int2[] routeTileBuffer = new int2[1024];

        [BurstCompile(FloatMode = FloatMode.Deterministic)]
        private struct ApplyRoutesJob : IJob
        {
            [ReadOnly] public NativeArray<RouteUnit> Moves;
            [ReadOnly] public NativeArray<int2> Waypoints; // RouteWire half tiles
            [ReadOnly] public NativeArray<int> KnownSlot;
            public NativeArray<PredictState> States;
            public NativeArray<int> RouteStart, RouteCount;
            public NativeList<float2> Arena;
            public NativeArray<float2> Blend;
            public NativeArray<OrderLead> Leads;
            public NativeArray<int> Counts;
            public float Now, StartTime;

            public void Execute()
            {
                for (int m = 0; m < Moves.Length; m++)
                {
                    RouteUnit u = Moves[m];
                    int index = u.Index;
                    if ((uint)index >= (uint)KnownSlot.Length || KnownSlot[index] < 0) continue;
                    float2 before = Predict(index);
                    Counts[0] += u.Count - RouteCount[index];
                    RouteStart[index] = Arena.Length;
                    RouteCount[index] = u.Count;
                    for (int i = 0; i < u.Count; i++) Arena.Add(RouteWire.Decode(Waypoints[u.First + i]));
                    PredictState s = States[index];
                    MovementPrediction.StartRoute(ref s, Arena[RouteStart[index]], StartTime);
                    States[index] = s;
                    if (Now > 0f) Keep(index, Blend[index] + before - Predict(index)); // Blend
                    if (Leads[index].Kind != 0) // EndLead
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
                return MovementPrediction.Predict(ref s, Arena.AsArray(), RouteStart[index], RouteCount[index], Now);
            }

            private void Keep(int index, float2 offset) => Blend[index] = math.lengthsq(offset) > MaxBlendTiles * MaxBlendTiles ? float2.zero : offset;
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
            }
            owner[index] = u.OwnerId;
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
            if (u.Reason == LeaveReason.Died) Died?.Invoke(u.Index, PredictOne(u.Index, tick * dt));
            int slot = knownSlot[u.Index];
            int last = known[known.Length - 1];
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
        /// after the order's round trip to the server. Drawing only: <see cref="Predicted"/> stays bit-identical to the server's copy, the
        /// server's route ends this, and the unit glides onto it.
        /// </summary>
        public void ShowMove(int index, float2 goal, float stopTiles) => StartLead(index, OrderLead.Going, goal, stopTiles);

        /// <summary>Draws a known unit stopped at once, until the server's correction says it has stopped. Drawing only.</summary>
        public void ShowHalt(int index) => StartLead(index, OrderLead.Halted, default, 0f);

        private void StartLead(int index, byte kind, float2 goal, float stopTiles)
        {
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
            if (arena.Length > 2 * liveWaypoints + 65536) Compact();
        }

        private void Compact()
        {
            var compacted = new NativeList<float2>(liveWaypoints + 1024, Allocator.Temp);
            for (int i = 0; i < known.Length; i++)
            {
                int index = known[i];
                int first = compacted.Length;
                for (int k = 0; k < routeCount[index]; k++) compacted.Add(arena[routeStart[index] + k]);
                routeStart[index] = first;
            }
            arena.Clear();
            arena.AddRange(compacted.AsArray());
            compacted.Dispose();
        }

        /// <summary>Predicts every known unit at <paramref name="now"/> in a Burst job (stored states unchanged).</summary>
        public void PredictAll(float now)
        {
            predicted.ResizeUninitialized(known.Length);
            drawn.ResizeUninitialized(known.Length);
            float elapsed = lastPredictTime < 0f ? 0f : math.max(0f, now - lastPredictTime);
            lastPredictTime = now;
            AdvanceLeads(now, elapsed);
            new PredictJob
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
                Drawn = drawn.AsArray(),
            }.Schedule(known.Length, 256).Complete();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            states.Dispose(); routeStart.Dispose(); routeCount.Dispose(); owner.Dispose(); type.Dispose(); health.Dispose();
            arena.Dispose(); known.Dispose(); knownSlot.Dispose(); predicted.Dispose(); blend.Dispose(); drawn.Dispose(); leads.Dispose();
            routeUnits.Dispose(); routeTiles.Dispose(); counts.Dispose();
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

            public void Execute(int i)
            {
                int index = Known[i];
                PredictState s = States[index];
                float2 p = MovementPrediction.Predict(ref s, Waypoints, RouteStart[index], RouteCount[index], Now);
                float2 b = Blend[index];
                float length = math.length(b);
                if (length > 0f)
                {
                    float fade = length * (1f - BlendKeep), walk = math.max(s.FullSpeed * CatchUpShare, length / CatchUpSeconds) * Elapsed;
                    b *= (length - math.min(fade, walk)) / length;
                    if (math.lengthsq(b) < 1e-8f) b = float2.zero;
                }
                Blend[index] = b;
                Positions[i] = p;
                Drawn[i] = Leads[index].Kind != 0 ? Leads[index].At : p + b;
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
