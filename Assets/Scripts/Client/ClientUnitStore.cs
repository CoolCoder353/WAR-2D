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
        /// <summary>Raised for each unit that left because it died: (index, last predicted position).</summary>
        public event Action<int, float2> Died;

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
        }

        /// <summary>Units known.</summary>
        public int Count => known.Length;
        /// <summary>The id index of the i-th known unit.</summary>
        public int IndexAt(int i) => known[i];
        /// <summary>Known indices, densely packed (parallel to <see cref="Predicted"/>).</summary>
        public NativeArray<int> Known => known.AsArray();
        /// <summary>Positions from the last <see cref="PredictAll"/>, parallel to <see cref="Known"/>.</summary>
        public NativeArray<float2> Predicted => predicted.AsArray();

        /// <summary>Owner id by index (for packing jobs).</summary>
        internal NativeArray<int> OwnerArray => owner;
        /// <summary>Health percent by index (for packing jobs).</summary>
        internal NativeArray<byte> HealthArray => health;
        /// <summary>Id capacity (indices are below this).</summary>
        public int Capacity => states.Length;

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

        private void ApplyOne(NetworkReader reader, int batchTick)
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
                    int tick = Messages.DecodeMoveOrder(reader, moves, waypoints);
                    foreach (RouteUnit u in moves)
                    {
                        if (!IsKnown(u.Index)) continue;
                        SetRoute(u.Index, u.First, u.Count);
                        PredictState s = states[u.Index];
                        MovementPrediction.StartRoute(ref s, arena[routeStart[u.Index]], tick * dt);
                        states[u.Index] = s;
                    }
                    break;
                }
                case MessageType.Correction:
                {
                    corrections.Clear();
                    int tick = Messages.DecodeCorrection(reader, corrections, out byte scale);
                    foreach (CorrectionUnit c in corrections)
                    {
                        if (!IsKnown(c.Index)) continue;
                        PredictState s = states[c.Index];
                        MovementPrediction.ApplyCorrection(ref s, arena.AsArray(), routeStart[c.Index], routeCount[c.Index], tick * dt, c, scale);
                        states[c.Index] = s;
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
                    foreach (AttackEvent a in attacks) if (IsKnown(a.AttackerIndex)) Attack?.Invoke(a.AttackerIndex, a.TargetId);
                    break;
                default:
                    throw new InvalidDataException("unknown replication message");
            }
        }

        private void Enter(in EnterUnit u, int tick)
        {
            int index = NetIdAllocator.IndexOf(u.Id);
            if (index >= states.Length) throw new InvalidDataException("unit index beyond the client's capacity");
            if (!IsKnown(index))
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
        }

        private void SetRoute(int index, int first, int count)
        {
            liveWaypoints += count - routeCount[index];
            routeStart[index] = arena.Length;
            routeCount[index] = count;
            for (int i = 0; i < count; i++) arena.Add(waypoints[first + i]);
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
            new PredictJob
            {
                Known = known.AsArray(),
                States = states,
                RouteStart = routeStart,
                RouteCount = routeCount,
                Waypoints = arena.AsArray(),
                Now = now,
                Positions = predicted.AsArray(),
            }.Schedule(known.Length, 256).Complete();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            states.Dispose(); routeStart.Dispose(); routeCount.Dispose(); owner.Dispose(); type.Dispose(); health.Dispose();
            arena.Dispose(); known.Dispose(); knownSlot.Dispose(); predicted.Dispose();
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

            public void Execute(int i)
            {
                int index = Known[i];
                PredictState s = States[index];
                Positions[i] = MovementPrediction.Predict(ref s, Waypoints, RouteStart[index], RouteCount[index], Now);
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
