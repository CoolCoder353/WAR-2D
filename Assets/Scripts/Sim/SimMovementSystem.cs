using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Pathing;

namespace WAR2D.Sim
{
    /// <summary>
    /// Movement: each unit with an order follows its order's flow field (read from the
    /// <see cref="OrderFieldTable"/>) unless it has a target in range, separates from its neighbours,
    /// and integrates without entering blocked tiles. A unit standing in a sector its route does not
    /// cover steers straight at the goal and reports the sector, so the route is extended.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimCombatSystem))]
    public partial struct SimMovementSystem : ISystem
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
            int count = clock.UnitCount;

            state.Dependency = new DesiredVelocityJob
            {
                Positions = data.Positions,
                Health = data.Health,
                Radius = data.Radius,
                Type = data.Type,
                Target = data.Target,
                OrderSlot = data.OrderSlot,
                Stance = data.Stance,
                Arrived = data.Arrived,
                BlockOf = data.Orders.BlockOf,
                Blocks = data.Orders.Blocks.AsArray(),
                SectorCount = data.Orders.SectorCount,
                SectorsX = data.Orders.SectorsX,
                CellsPerSector = data.Orders.CellsPerSector,
                FieldCellSize = data.Orders.CellSize,
                OrderGoal = data.OrderGoal,
                OrderLive = data.OrderLive,
                OrderReady = data.OrderReady,
                OrderFollowers = data.OrderFollowers,
                RouteMisses = data.RouteMisses.AsParallelWriter(),
                SpeedByType = data.SpeedByType,
                Width = data.Width,
                Height = data.Height,
                Strength = data.SeparationStrength,
                Margin = data.SeparationMargin,
                SeparationInterval = data.SeparationInterval,
                Slice = data.Slice,
                Tick = clock.Tick,
                Dt = data.Dt,
                CellStart = data.CellStart,
                Sorted = data.Sorted,
                CellsX = data.CellsX,
                CellsY = data.CellsY,
                CellSize = data.CellSize,
                Velocity = data.Velocity,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new IntegrateJob
            {
                Positions = data.Positions,
                Velocity = data.Velocity,
                Health = data.Health,
                Radius = data.Radius,
                SizeClass = data.SizeClass,
                Tiles = data.Tiles,
                Used = data.Used,
                LargeGrid = data.LargeGrid,
                Width = data.Width,
                Height = data.Height,
                Dt = data.Dt,
            }.Schedule(count, 256, state.Dependency);

            state.Dependency = new CountFollowersJob
            {
                OrderSlot = data.OrderSlot,
                Arrived = data.Arrived,
                Health = data.Health,
                Count = count,
                Followers = data.OrderFollowers,
            }.Schedule(state.Dependency);

            state.Dependency = new WriteBackMovementJob
            {
                Positions = data.Positions,
                Velocity = data.Velocity,
                Arrived = data.Arrived,
                Count = count,
            }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>Flow following (or holding to fight), plus the separation push.</summary>
    [BurstCompile]
    public struct DesiredVelocityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<byte> Stance;
        public NativeArray<byte> Arrived;
        [ReadOnly] public NativeArray<int> BlockOf;
        [ReadOnly] public NativeArray<byte> Blocks;
        /// <summary>The order table's shape (see <see cref="OrderFieldTable"/>).</summary>
        public int SectorCount, SectorsX, CellsPerSector, FieldCellSize;
        [ReadOnly] public NativeArray<float2> OrderGoal;
        [ReadOnly] public NativeArray<byte> OrderLive;
        [ReadOnly] public NativeArray<byte> OrderReady;
        /// <summary>Last tick's follower count per order: a bigger group stops farther from the goal.</summary>
        [ReadOnly] public NativeArray<int> OrderFollowers;
        public NativeQueue<int2>.ParallelWriter RouteMisses;
        [ReadOnly] public NativeArray<float> SpeedByType;
        public int Width, Height;
        public float Strength, Margin;
        public int SeparationInterval, Slice, Tick;
        public float Dt;
        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> Sorted;
        public int CellsX, CellsY, CellSize;
        public NativeArray<float2> Velocity;

        public void Execute(int i)
        {
            Arrived[i] = 0;
            if (Health[i] <= 0f || Stance[i] == Stances.Hold)
            {
                // Holding units stand still: no flow and no separation push of their own.
                Velocity[i] = float2.zero;
                return;
            }
            float2 position = Positions[i];
            float speed = Type[i] < SpeedByType.Length ? SpeedByType[Type[i]] : 0f;
            float2 velocity = float2.zero;

            int slot = OrderSlot[i];
            // Moving units ignore their target; the others hold to fight it.
            if ((Target[i] < 0 || Stance[i] == Stances.Move) && (uint)slot < (uint)OrderLive.Length && OrderLive[slot] != 0)
            {
                byte direction = Direction(slot, position);
                float2 toGoal = OrderGoal[slot] - position;
                float distance = math.length(toGoal);
                // A group packs around the goal: the stop radius grows with the square root of its size.
                float arriveRadius = 0.5f + 0.4f * math.sqrt(math.max(1, OrderFollowers[slot]));
                if (direction == FlowDirections.AtGoal)
                {
                    // On a goal cell: close in on the goal itself, then stop.
                    if (distance <= arriveRadius) Arrived[i] = 1;
                    else velocity = speed * toGoal / distance;
                }
                else if (FlowDirections.IsStep(direction))
                {
                    // Aim at the centre of the next cell rather than along the raw step, so units
                    // line up with corridors and gaps the cell grid says are open.
                    int2 cell = (int2)math.floor(position / FieldCellSize);
                    float2 next = ((float2)(cell + FlowDirections.Step(direction)) + 0.5f) * FieldCellSize;
                    velocity = speed * math.normalizesafe(next - position, math.normalize((float2)FlowDirections.Step(direction)));
                }
                else
                {
                    // Off the route (or the route is not built yet): head straight for the goal, and
                    // once the route exists, report the sector so the route grows to cover it.
                    if (distance <= arriveRadius) Arrived[i] = 1;
                    else velocity = speed * toGoal / distance;
                    if (OrderReady[slot] != 0 && (i + Tick) % Slice == 0)
                        RouteMisses.Enqueue(new int2(slot, SectorOf((int2)math.floor(position))));
                }
            }

            if (SeparationInterval <= 1 || (i + Tick) % SeparationInterval == 0)
                velocity += NeighbourPush(i, position, Radius[i]) / Dt;

            // The push is a per-tick correction; cap it so a crowd cannot fling a unit.
            float maxSpeed = math.max(speed, 1f) * 2f;
            float speedSq = math.lengthsq(velocity);
            Velocity[i] = speedSq > maxSpeed * maxSpeed ? velocity * (maxSpeed * math.rsqrt(speedSq)) : velocity;
        }

        private int SectorOf(int2 tile) => tile.y / SectorGraph.SectorSize * SectorsX + tile.x / SectorGraph.SectorSize;

        private byte Direction(int handle, float2 position)
        {
            int2 tile = (int2)math.floor(position);
            if (tile.x < 0 || tile.y < 0 || tile.x >= Width || tile.y >= Height) return FlowDirections.None;
            int sx = tile.x / SectorGraph.SectorSize, sy = tile.y / SectorGraph.SectorSize;
            int block = BlockOf[handle * SectorCount + sy * SectorsX + sx];
            if (block < 0) return FlowDirections.None;
            int side = CellsPerSector;
            int cx = tile.x % SectorGraph.SectorSize / FieldCellSize, cy = tile.y % SectorGraph.SectorSize / FieldCellSize;
            return Blocks[block * side * side + cy * side + cx];
        }

        private float2 NeighbourPush(int i, float2 position, float radius)
        {
            float size = CellSize;
            int2 c = math.clamp((int2)math.floor(position / size), 0, new int2(CellsX - 1, CellsY - 1));
            float2 local = position - (float2)c * size;
            float margin = radius + Margin;
            int2 lo = c, hi = c;
            if (local.x < margin) lo.x = math.max(c.x - 1, 0);
            else if (local.x > size - margin) hi.x = math.min(c.x + 1, CellsX - 1);
            if (local.y < margin) lo.y = math.max(c.y - 1, 0);
            else if (local.y > size - margin) hi.y = math.min(c.y + 1, CellsY - 1);

            float2 push = float2.zero;
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = CellStart[cell]; k < CellStart[cell + 1]; k++)
                {
                    int j = Sorted[k];
                    if (j == i || Health[j] <= 0f) continue;
                    float2 delta = position - Positions[j];
                    float rj = Radius[j];
                    float sum = radius + rj;
                    float distanceSq = math.lengthsq(delta);
                    if (distanceSq >= sum * sum) continue;
                    float distance = math.sqrt(distanceSq);
                    // Exactly stacked: push apart along a direction derived from the pair, deterministically.
                    float2 away = distance > 1e-4f ? delta / distance : (i < j ? new float2(1f, 0f) : new float2(-1f, 0f));
                    float share = (rj * rj) / (radius * radius + rj * rj);
                    push += Strength * share * ((sum - distance) * away);
                }
            }
            return push;
        }
    }

    /// <summary>Moves each unit by its velocity, axis by axis, refusing steps into blocked tiles.</summary>
    [BurstCompile]
    public struct IntegrateJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> SizeClass;
        [ReadOnly] public NativeArray<byte> Tiles;
        [ReadOnly] public NativeArray<byte> Used;
        [ReadOnly] public NativeArray<byte> LargeGrid;
        public int Width, Height;
        public float Dt;

        public void Execute(int i)
        {
            if (Health[i] <= 0f) return;
            float2 position = Positions[i];
            float2 step = Velocity[i] * Dt;
            float radius = Radius[i];
            byte sizeClass = SizeClass[i];
            float2 moved = position;
            if (step.x != 0f)
            {
                float2 candidate = new float2(position.x + step.x, position.y);
                if (!Blocked(candidate, radius, sizeClass)) moved.x = candidate.x;
            }
            if (step.y != 0f)
            {
                float2 candidate = new float2(moved.x, position.y + step.y);
                if (!Blocked(candidate, radius, sizeClass)) moved.y = candidate.y;
            }
            Positions[i] = moved;
        }

        private bool Blocked(float2 position, float radius, byte sizeClass)
        {
            int2 centre = (int2)math.floor(position);
            if ((uint)centre.x >= (uint)Width || (uint)centre.y >= (uint)Height) return true;
            if (sizeClass == 1 && LargeGrid[centre.y * Width + centre.x] != 0) return true;
            int2 lo = (int2)math.floor(position - radius);
            int2 hi = (int2)math.floor(position + radius);
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return true;
                int t = y * Width + x;
                if (Tiles[t] != (byte)TileType.Ground || Used[t] != 0) return true;
            }
            return false;
        }
    }

    /// <summary>Counts each order's followers (units still on it after this tick) for the order book.</summary>
    [BurstCompile]
    public struct CountFollowersJob : IJob
    {
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<byte> Arrived;
        [ReadOnly] public NativeArray<float> Health;
        public int Count;
        public NativeArray<int> Followers;

        public void Execute()
        {
            for (int h = 0; h < Followers.Length; h++) Followers[h] = 0;
            for (int i = 0; i < Count; i++)
            {
                int slot = OrderSlot[i];
                if ((uint)slot < (uint)Followers.Length && Arrived[i] == 0 && Health[i] > 0f) Followers[slot]++;
            }
        }
    }

    /// <summary>Writes position and velocity back, and clears the order (and moving stance) of a unit that arrived.</summary>
    [BurstCompile]
    public partial struct WriteBackMovementJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<byte> Arrived;
        public int Count;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Count) return;
            unit.Position = Positions[index];
            unit.Velocity = Velocity[index];
            if (Arrived[index] != 0)
            {
                unit.OrderSlot = -1;
                if (unit.Stance == Stances.Move || unit.Stance == Stances.AttackMove) unit.Stance = Stances.Idle;
            }
        }
    }
}
