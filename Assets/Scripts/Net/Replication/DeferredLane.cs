using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Sim;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// Unit replication for one all-seeing client (the match recorder), off the boundary's critical path: at a
    /// boundary it copies the settled unit arrays, routes and attacks into buffers of its own and schedules its
    /// interest and encode jobs, which run during the next tick; the next boundary collects the output. The
    /// client gets each tick's units one tick late, which only a recording can afford.
    /// </summary>
    internal sealed class DeferredLane : IDisposable
    {
        private readonly InterestSets interests;
        private readonly ReplicationEncoder encoder;
        private readonly RouteStore routes;
        private NativeArray<float2> positions;
        private NativeArray<float> health, maxHealth, speedByType;
        private NativeArray<int> ownerId, idOf, indexOfId;
        private NativeArray<byte> type, noFog;
        private NativeArray<int2> attacks;
        private NativeArray<AttackTarget> targets;
        private NativeArray<BuildingAttack> buildingAttacks;
        private JobHandle handle;
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

        /// <summary>Copies the settled world and schedules this tick's interest build and encode (not completed).</summary>
        public void Schedule(SimData data, int tick, int count, RouteStore live, NativeArray<int2> tickAttacks, NativeArray<AttackTarget> tickTargets, NativeArray<BuildingAttack> tickBuildingAttacks = default)
        {
            handle.Complete();
            Copy(data.Positions, ref positions, count);
            Copy(data.Health, ref health, count);
            Copy(data.MaxHealth, ref maxHealth, count);
            Copy(data.OwnerId, ref ownerId, count);
            Copy(data.Type, ref type, count);
            Copy(data.IdOf, ref idOf, count);
            Copy(data.IndexOfId, ref indexOfId, data.IndexOfId.Length);
            Copy(data.SpeedByType, ref speedByType, data.SpeedByType.Length);
            routes.Arena.ResizeUninitialized(live.Arena.Length);
            NativeArray<float2>.Copy(live.Arena.AsArray(), routes.Arena.AsArray(), live.Arena.Length);
            NativeArray<int>.Copy(live.Start, routes.Start);
            NativeArray<int>.Copy(live.Count, routes.Count);
            NativeArray<int>.Copy(live.Generation, routes.Generation);
            if (attacks.IsCreated) attacks.Dispose();
            if (targets.IsCreated) targets.Dispose();
            attacks = new NativeArray<int2>(tickAttacks, Allocator.Persistent);
            targets = new NativeArray<AttackTarget>(tickTargets, Allocator.Persistent);
            if (buildingAttacks.IsCreated) buildingAttacks.Dispose();
            buildingAttacks = tickBuildingAttacks.IsCreated ? new NativeArray<BuildingAttack>(tickBuildingAttacks, Allocator.Persistent) : new NativeArray<BuildingAttack>(0, Allocator.Persistent);

            var input = new ReplicationInput
            {
                Count = count, Positions = positions, Health = health, MaxHealth = maxHealth, OwnerId = ownerId, Type = type,
                IdOf = idOf, IndexOfId = indexOfId, SpeedByType = speedByType,
                Visible = noFog, FogCellSize = math.max(1, data.FogCellSize), FogW = 1, FogH = 1, FogCells = 1,
            };
            JobHandle built = interests.Schedule(input, default);
            handle = built; // completed by Dispose even if the encoder refuses to schedule
            handle = encoder.Schedule(tick, input, attacks, targets, buildingAttacks, null, built);
            JobHandle.ScheduleBatchedJobs();
            PendingTick = tick;
        }

        private static void Copy<T>(NativeArray<T> source, ref NativeArray<T> into, int count) where T : struct
        {
            if (!into.IsCreated || into.Length < source.Length)
            {
                if (into.IsCreated) into.Dispose();
                into = new NativeArray<T>(source.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            NativeArray<T>.Copy(source, into, math.min(count, source.Length));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
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
            if (type.IsCreated) type.Dispose();
            if (attacks.IsCreated) attacks.Dispose();
            if (targets.IsCreated) targets.Dispose();
            if (buildingAttacks.IsCreated) buildingAttacks.Dispose();
            noFog.Dispose();
        }
    }
}
