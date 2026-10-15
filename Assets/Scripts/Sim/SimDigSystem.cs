using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Diggers at work. Each Digger with a Dig target looks along the 4-connected line to it for the first
    /// solid tile (<see cref="DigRules.Next"/>): next to it, it digs (rock and gems take <c>DigTime</c> and
    /// queue a <see cref="DigEvent"/> for the boundary; player walls take attack damage on the Digger's
    /// cooldown); away from it, it asks the boundary for a move to the standing tile. A finished or blocked
    /// line queues the Digger on <see cref="SimData.WorkFinished"/>.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimConstructionSystem))]
    [UpdateBefore(typeof(SimVisionSystem))]
    public partial struct SimDigSystem : ISystem
    {
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            state.Dependency = new DigJob
            {
                Count = clock.UnitCount,
                Width = data.Width, Height = data.Height,
                Dt = data.Dt, DigTime = data.DigTime,
                Digger = (byte)UnitType.Digger,
                Tiles = data.Tiles,
                Positions = data.Positions, Radius = data.Radius, Health = data.Health, Type = data.Type, OwnerId = data.OwnerId, IdOf = data.IdOf,
                WorkKind = data.WorkKind, WorkTile = data.WorkTile, OrderSlot = data.OrderSlot, OrderLive = data.OrderLive, Arrived = data.Arrived,
                DamageByType = data.DamageByType, CooldownByType = data.CooldownByType, DamageTable = data.DamageTable,
                Cooldown = data.Cooldown, DigProgress = data.DigProgress, DigAt = data.DigAt,
                Finished = data.WorkFinished, Done = data.DigDone, Moves = data.DigMoves, WallDamage = data.WallDamage,
            }.Schedule(state.Dependency);
            state.Dependency = new WriteBackDigJob
            {
                Count = clock.UnitCount,
                Cooldown = data.Cooldown, DigProgress = data.DigProgress, DigAt = data.DigAt,
            }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>One pass over the units; Diggers are few.</summary>
    [BurstCompile]
    public struct DigJob : IJob
    {
        public int Count, Width, Height;
        public float Dt, DigTime;
        public byte Digger;
        [ReadOnly] public NativeArray<byte> Tiles, Type, WorkKind, OrderLive, Arrived;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Radius, Health, DamageByType, CooldownByType, DamageTable;
        [ReadOnly] public NativeArray<int> OwnerId, IdOf, WorkTile, OrderSlot;
        public NativeArray<float> Cooldown, DigProgress;
        public NativeArray<int> DigAt;
        public NativeQueue<int> Finished;
        public NativeQueue<DigEvent> Done;
        public NativeQueue<int3> Moves;
        public NativeQueue<TileWork> WallDamage;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Type[i] != Digger || Health[i] <= 0f || WorkKind[i] != WorkKinds.Dig) continue;
                int2 target = new int2(WorkTile[i] % Width, WorkTile[i] / Width);
                int2 position = (int2)math.floor(Positions[i]);
                DigStep step = DigRules.Next(Tiles, Width, Height, position, target, out int2 dig, out int2 stand);
                if (step != DigStep.Dig)
                {
                    DigProgress[i] = 0f;
                    DigAt[i] = 0;
                    Finished.Enqueue(IdOf[i]);
                    continue;
                }
                int tile = dig.y * Width + dig.x;
                float2 lo = dig, hi = dig + 1;
                float2 gap = math.max(math.max(lo - Positions[i], Positions[i] - hi), 0f);
                if (math.length(gap) <= Radius[i] + 0.6f)
                {
                    if ((TileType)Tiles[tile] == TileType.PlayerWall)
                    {
                        if (Cooldown[i] > 0f) continue;
                        float damage = DamageByType[Digger] * WAR2D.Rules.DamageTable.Get(DamageTable, Digger, TargetClass.Wall);
                        WallDamage.Enqueue(new TileWork { Tile = tile, Amount = damage, OwnerId = OwnerId[i] });
                        Cooldown[i] = CooldownByType[Digger];
                        continue;
                    }
                    if (DigAt[i] != tile) { DigAt[i] = tile; DigProgress[i] = 0f; }
                    DigProgress[i] += Dt;
                    if (DigProgress[i] >= DigTime)
                    {
                        Done.Enqueue(new DigEvent { Tile = tile, OwnerId = OwnerId[i] });
                        DigProgress[i] = 0f;
                        DigAt[i] = 0;
                    }
                    continue;
                }
                int order = OrderSlot[i];
                bool live = (uint)order < (uint)OrderLive.Length && OrderLive[order] != 0 && Arrived[i] == 0;
                if (!live) Moves.Enqueue(new int3(IdOf[i], stand.x, stand.y));
            }
        }
    }

    /// <summary>Stores the dig progress and the (possibly reset) attack cooldown back to each unit.</summary>
    [BurstCompile]
    public partial struct WriteBackDigJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float> Cooldown, DigProgress;
        [ReadOnly] public NativeArray<int> DigAt;
        public int Count;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Count) return;
            unit.Cooldown = Cooldown[index];
            unit.DigProgress = DigProgress[index];
            unit.DigAt = DigAt[index];
        }
    }
}
