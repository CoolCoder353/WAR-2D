using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Sim;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// Unit replication for one all-seeing client (the match recorder), off the boundary's critical path: at a
    /// boundary it copies the settled unit arrays, routes and attacks into buffers of its own (the arrays and routes on
    /// a worker thread, see <see cref="FinishCopy"/>) and schedules its interest and encode jobs, which run during the
    /// next tick; the next boundary collects the output. The client gets each tick's units one tick late, which only a
    /// recording can afford.
    /// </summary>
    internal sealed class DeferredLane : IDisposable
    {
        private readonly InterestSets interests;
        private readonly ReplicationEncoder encoder;
        private readonly RouteStore routes;
        private NativeArray<float2> positions;
        private NativeArray<float> health, maxHealth, speedByType;
        private NativeArray<int> ownerId, idOf, indexOfId;
        private NativeArray<byte> type, noFog, lastHitKind;
        private NativeArray<int2> attacks;
        private NativeArray<AttackTarget> targets;
        private NativeArray<BuildingAttack> buildingAttacks;
        private JobHandle handle, copied;
        private bool disposed;

        /// <summary>The tick whose output is still being encoded, or -1.</summary>
        public int PendingTick { get; private set; } = -1;

        public DeferredLane(int ownerId, in EncoderConfig config, int idCapacity, int maxEntersPerBuild)
        {
            interests = new InterestSets(1, idCapacity, Allocator.Persistent) { MaxEntersPerBuild = maxEntersPerBuild };
            interests.SetClient(0, ownerId, SimData.SeesAll);
            routes = new RouteStore(idCapacity, Allocator.Persistent);
            encoder = new ReplicationEncoder(config, interests, routes, idCapacity, Allocator.Persistent);
            // An empty view box: every unit on the offscreen correction tier (the recording's coarse fidelity).
            encoder.SetView(0, new int2(int.MinValue / 2), new int2(int.MinValue / 2));
            noFog = new NativeArray<byte>(1, Allocator.Persistent);
        }

        /// <summary>Waits for the pending encode and returns its output (valid until the next <see cref="Schedule"/>).</summary>
        public (NativeList<byte> reliable, NativeList<int> reliableSizes, NativeList<byte> unreliable, NativeList<int> unreliableSizes) Collect()
        {
            handle.Complete();
            return encoder.Output(0);
        }

        /// <summary>
        /// Starts the copy of the settled world and schedules this tick's interest build and encode after it (not
        /// completed). The caller must call <see cref="FinishCopy"/> before anything writes the settled arrays or routes.
        /// </summary>
        public void Schedule(SimData data, int tick, int count, RouteStore live, NativeArray<int2> tickAttacks, NativeArray<AttackTarget> tickTargets, NativeArray<BuildingAttack> tickBuildingAttacks = default)
        {
            handle.Complete();
            Fit(data.Positions, ref positions);
            Fit(data.Health, ref health);
            Fit(data.MaxHealth, ref maxHealth);
            Fit(data.OwnerId, ref ownerId);
            Fit(data.Type, ref type);
            Fit(data.IdOf, ref idOf);
            Fit(data.IndexOfId, ref indexOfId);
            Fit(data.LastHitKind, ref lastHitKind);
            Fit(data.SpeedByType, ref speedByType);
            routes.Arena.ResizeUninitialized(live.Arena.Length);
            copied = new CopyJob
            {
                Count = count,
                Positions = data.Positions, Health = data.Health, MaxHealth = data.MaxHealth, OwnerId = data.OwnerId, Type = data.Type,
                IdOf = data.IdOf, IndexOfId = data.IndexOfId, LastHitKind = data.LastHitKind, SpeedByType = data.SpeedByType,
                Arena = live.Arena.AsArray(), Start = live.Start, RouteCount = live.Count, Generation = live.Generation,
                PositionsOut = positions, HealthOut = health, MaxHealthOut = maxHealth, OwnerIdOut = ownerId, TypeOut = type,
                IdOfOut = idOf, IndexOfIdOut = indexOfId, LastHitKindOut = lastHitKind, SpeedByTypeOut = speedByType,
                ArenaOut = routes.Arena.AsArray(), StartOut = routes.Start, RouteCountOut = routes.Count, GenerationOut = routes.Generation,
            }.Schedule();
            if (attacks.IsCreated) attacks.Dispose();
            if (targets.IsCreated) targets.Dispose();
            attacks = new NativeArray<int2>(tickAttacks, Allocator.Persistent);
            targets = new NativeArray<AttackTarget>(tickTargets, Allocator.Persistent);
            if (buildingAttacks.IsCreated) buildingAttacks.Dispose();
            buildingAttacks = tickBuildingAttacks.IsCreated ? new NativeArray<BuildingAttack>(tickBuildingAttacks, Allocator.Persistent) : new NativeArray<BuildingAttack>(0, Allocator.Persistent);

            var input = new ReplicationInput
            {
                Count = count, Positions = positions, Health = health, MaxHealth = maxHealth, OwnerId = ownerId, Type = type,
                IdOf = idOf, IndexOfId = indexOfId, SpeedByType = speedByType, LastHitKind = lastHitKind,
                Visible = noFog, FogCellSize = math.max(1, data.FogCellSize), FogW = 1, FogH = 1, FogCells = 1, MapWidth = data.Width,
            };
            JobHandle built = interests.Schedule(input, copied);
            handle = built; // completed by Dispose even if the encoder refuses to schedule
            handle = encoder.Schedule(tick, input, attacks, targets, buildingAttacks, null, built);
            JobHandle.ScheduleBatchedJobs();
            PendingTick = tick;
        }

        /// <summary>Waits for this tick's copy of the settled arrays and routes (not for the encode that follows it).</summary>
        public void FinishCopy() => copied.Complete();

        private static void Fit<T>(NativeArray<T> source, ref NativeArray<T> into) where T : struct
        {
            if (into.IsCreated && into.Length >= source.Length) return;
            if (into.IsCreated) into.Dispose();
            into = new NativeArray<T>(source.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// The settled unit arrays and the live routes into the lane's buffers: the first <see cref="Count"/> units of each
        /// per-unit array, the other arrays whole. About 9 MB a tick at 100,000 units, which cost the boundary's main thread
        /// about 1 ms (100,000-unit benchmark, 2026-11-11); as a job it runs while the main thread sends.
        /// </summary>
        [BurstCompile]
        private struct CopyJob : IJob
        {
            public int Count;
            [ReadOnly] public NativeArray<float2> Positions, Arena;
            [ReadOnly] public NativeArray<float> Health, MaxHealth, SpeedByType;
            [ReadOnly] public NativeArray<int> OwnerId, IdOf, IndexOfId, Start, RouteCount, Generation;
            [ReadOnly] public NativeArray<byte> Type, LastHitKind;
            [WriteOnly] public NativeArray<float2> PositionsOut, ArenaOut;
            [WriteOnly] public NativeArray<float> HealthOut, MaxHealthOut, SpeedByTypeOut;
            [WriteOnly] public NativeArray<int> OwnerIdOut, IdOfOut, IndexOfIdOut, StartOut, RouteCountOut, GenerationOut;
            [WriteOnly] public NativeArray<byte> TypeOut, LastHitKindOut;

            public void Execute()
            {
                NativeArray<float2>.Copy(Positions, PositionsOut, math.min(Count, Positions.Length));
                NativeArray<float>.Copy(Health, HealthOut, math.min(Count, Health.Length));
                NativeArray<float>.Copy(MaxHealth, MaxHealthOut, math.min(Count, MaxHealth.Length));
                NativeArray<int>.Copy(OwnerId, OwnerIdOut, math.min(Count, OwnerId.Length));
                NativeArray<byte>.Copy(Type, TypeOut, math.min(Count, Type.Length));
                NativeArray<int>.Copy(IdOf, IdOfOut, math.min(Count, IdOf.Length));
                NativeArray<int>.Copy(IndexOfId, IndexOfIdOut, IndexOfId.Length);
                NativeArray<byte>.Copy(LastHitKind, LastHitKindOut, LastHitKind.Length);
                NativeArray<float>.Copy(SpeedByType, SpeedByTypeOut, SpeedByType.Length);
                NativeArray<float2>.Copy(Arena, ArenaOut, Arena.Length);
                NativeArray<int>.Copy(Start, StartOut, Start.Length);
                NativeArray<int>.Copy(RouteCount, RouteCountOut, RouteCount.Length);
                NativeArray<int>.Copy(Generation, GenerationOut, Generation.Length);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            copied.Complete(); // on its own: Schedule can throw after starting the copy and before handle depends on it
            handle.Complete();
            encoder.Dispose();
            interests.Dispose();
            routes.Dispose();
            if (positions.IsCreated) positions.Dispose();
            if (health.IsCreated) health.Dispose();
            if (maxHealth.IsCreated) maxHealth.Dispose();
            if (speedByType.IsCreated) speedByType.Dispose();
            if (ownerId.IsCreated) ownerId.Dispose();
            if (idOf.IsCreated) idOf.Dispose();
            if (indexOfId.IsCreated) indexOfId.Dispose();
            if (lastHitKind.IsCreated) lastHitKind.Dispose();
            if (type.IsCreated) type.Dispose();
            if (attacks.IsCreated) attacks.Dispose();
            if (targets.IsCreated) targets.Dispose();
            if (buildingAttacks.IsCreated) buildingAttacks.Dispose();
            noFog.Dispose();
        }
    }
}
