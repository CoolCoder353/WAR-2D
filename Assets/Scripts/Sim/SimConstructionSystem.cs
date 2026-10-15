using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>Repair work on a finished building: <see cref="Work"/> health points, paid by <see cref="Payer"/>.</summary>
    public struct RepairWork
    {
        public int Slot, Payer;
        public float Work;
    }

    /// <summary>
    /// Builders at work: each Builder with a Build or Repair target within <see cref="WorkReach"/> tiles of
    /// the target's footprint adds <c>BuildPower × dt</c> to it. Work on blueprints accumulates in
    /// <see cref="SimData.BuildingWork"/> for the boundary to apply, work on finished buildings in
    /// <see cref="SimData.RepairWork"/> (the repairer pays); a Builder whose target is gone or finished is
    /// queued on <see cref="SimData.WorkFinished"/> so the boundary clears its work.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimMovementSystem))]
    [UpdateBefore(typeof(SimVisionSystem))]
    public partial struct SimConstructionSystem : ISystem
    {
        /// <summary>How far (tiles, from the body's edge) a Builder reaches.</summary>
        public const float WorkReach = 1.0f;

        public void OnCreate(ref SystemState state) => state.RequireForUpdate<SimData>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            state.Dependency = new BuildWorkJob
            {
                Count = clock.UnitCount,
                Dt = data.Dt,
                Power = data.BuildPower,
                Builder = (byte)UnitType.Builder,
                Width = data.Width,
                Positions = data.Positions, Radius = data.Radius, Health = data.Health, Type = data.Type, OwnerId = data.OwnerId,
                IdOf = data.IdOf, WorkTile = data.WorkTile, WorkKind = data.WorkKind,
                BuildingCount = math.min(data.BuildingCount[0], data.BuildingCapacity),
                BuildingAnchor = data.BuildingAnchor, BuildingSize = data.BuildingSize, BuildingFlag = data.BuildingFlag,
                BuildingHealth = data.BuildingHealth, BuildingMaxHealth = data.BuildingMaxHealth,
                BuildingWork = data.BuildingWork,
                Repairs = data.RepairWork,
                Finished = data.WorkFinished,
            }.Schedule(state.Dependency);
        }
    }

    /// <summary>One pass over the units; Builders are few, so a single job is cheaper than a parallel one with atomics.</summary>
    [BurstCompile]
    public struct BuildWorkJob : IJob
    {
        public int Count, Width, BuildingCount;
        public float Dt, Power;
        public byte Builder;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Radius, Health, BuildingHealth, BuildingMaxHealth;
        [ReadOnly] public NativeArray<byte> Type, WorkKind, BuildingFlag;
        [ReadOnly] public NativeArray<int> IdOf, WorkTile, OwnerId;
        [ReadOnly] public NativeArray<int2> BuildingAnchor, BuildingSize;
        public NativeArray<float> BuildingWork;
        public NativeQueue<RepairWork> Repairs;
        public NativeQueue<int> Finished;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                byte kind = WorkKind[i];
                if (Type[i] != Builder || Health[i] <= 0f || (kind != WorkKinds.Build && kind != WorkKinds.Repair)) continue;
                int2 tile = new int2(WorkTile[i] % Width, WorkTile[i] / Width);
                int b = BuildingCovering(tile);
                if (b < 0 || BuildingHealth[b] <= 0f) { Finished.Enqueue(IdOf[i]); continue; }
                bool blueprint = (BuildingFlag[b] & global::BuildingFlags.UnderConstruction) != 0;
                if (kind == WorkKinds.Build && !blueprint) { Finished.Enqueue(IdOf[i]); continue; }
                if (kind == WorkKinds.Repair && !blueprint && BuildingHealth[b] >= BuildingMaxHealth[b]) { Finished.Enqueue(IdOf[i]); continue; }
                if (DistanceToRect(Positions[i], BuildingAnchor[b], BuildingSize[b]) > SimConstructionSystem.WorkReach + Radius[i]) continue;
                if (blueprint) BuildingWork[b] += Power * Dt;
                else Repairs.Enqueue(new RepairWork { Slot = b, Payer = OwnerId[i], Work = Power * Dt });
            }
        }

        private int BuildingCovering(int2 tile)
        {
            for (int b = 0; b < BuildingCount; b++)
            {
                int2 lo = Footprint.Start(BuildingAnchor[b], BuildingSize[b]), hi = lo + BuildingSize[b];
                if (tile.x >= lo.x && tile.y >= lo.y && tile.x < hi.x && tile.y < hi.y) return b;
            }
            return -1;
        }

        private static float DistanceToRect(float2 p, int2 anchor, int2 size)
        {
            float2 lo = Footprint.Start(anchor, size), hi = lo + size;
            float2 d = math.max(math.max(lo - p, p - hi), 0f);
            return math.length(d);
        }
    }
}
