using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Stage 4: movement. Builds each unit's desired velocity from its order's flow direction (or
    /// zero when it has an enemy in range, so it stops to fight) plus the separation push away from
    /// hash neighbours it overlaps, integrates it, and slides along blocked tiles by testing the two
    /// axes separately.
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpikeTickGroup))]
    [UpdateAfter(typeof(SpikeCombatSystem))]
    public partial struct SpikeMovementSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SpikeWorldData world = SystemAPI.GetSingleton<SpikeWorldData>();
            int tick = (int)math.round(SystemAPI.Time.ElapsedTime / world.Dt);

            state.Dependency = new DesiredVelocityJob
            {
                Capacity = world.Capacity,
                Positions = world.Positions,
                Health = world.Health,
                Radius = world.Radius,
                Target = world.Target,
                FieldGoal = world.FieldGoal,
                Arrived = world.Arrived,
                Directions = world.Directions,
                OrderBase = world.OrderBase,
                OrderCount = world.OrderCount,
                Width = world.Width,
                Height = world.Height,
                Speed = world.Speed,
                Strength = world.SeparationStrength,
                Iterations = world.SeparationIterations,
                SeparationInterval = world.SeparationInterval,
                Tick = tick,
                Dt = world.Dt,
                CellStart = world.CellStart,
                Sorted = world.Sorted,
                CellsX = world.CellsX,
                CellsY = world.CellsY,
                InvCellSize = 1f / world.CellSize,
                Velocity = world.Velocity,
            }.Schedule(world.Capacity, 256, state.Dependency);

            state.Dependency = new IntegrateJob
            {
                Capacity = world.Capacity,
                Positions = world.Positions,
                Velocity = world.Velocity,
                Health = world.Health,
                Radius = world.Radius,
                SizeClass = world.SizeClass,
                Tiles = world.Tiles,
                LargeGrid = world.LargeGrid,
                Width = world.Width,
                Height = world.Height,
                Dt = world.Dt,
                Halves = world.MoveHalves,
                Tick = tick,
            }.Schedule(world.Capacity, 256, state.Dependency);

            state.Dependency = new WriteBackMovementJob
            {
                Positions = world.Positions,
                Velocity = world.Velocity,
                Arrived = world.Arrived,
            }.ScheduleParallel(state.Dependency);
        }
    }

    /// <summary>
    /// Desired velocity per unit: the flow step of the order slot it follows, zero while it has an
    /// enemy to fight, plus the separation push from the hash neighbours it overlaps. Unit i takes the
    /// share <c>r[j]^2 / (r[i]^2 + r[j]^2)</c> of the push, so small units make way for large ones.
    /// </summary>
    [BurstCompile]
    public struct DesiredVelocityJob : IJobParallelFor
    {
        public int Capacity;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<int> FieldGoal;
        public NativeArray<byte> Arrived;
        [ReadOnly] public NativeArray<byte> Directions;
        [ReadOnly] public NativeArray<int> OrderBase;
        public int OrderCount;
        public int Width, Height;
        public float Speed;
        public float Strength;
        public int Iterations;
        public int SeparationInterval, Tick;
        public float Dt;

        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> Sorted;
        public int CellsX, CellsY;
        public float InvCellSize;
        public NativeArray<float2> Velocity;

        public void Execute(int i)
        {
            if (i >= Capacity) return;
            Arrived[i] = 0;
            if (Health[i] <= 0f)
            {
                Velocity[i] = float2.zero;
                return;
            }

            float2 position = Positions[i];
            float2 velocity = float2.zero;

            // The order: follow the flow field unless there is an enemy in range (then hold position).
            int slot = FieldGoal[i];
            if (Target[i] < 0 && (uint)slot < (uint)OrderCount)
            {
                int2 cell = math.clamp((int2)math.floor(position), 0, new int2(Width - 1, Height - 1));
                byte direction = Directions[OrderBase[slot] + cell.y * Width + cell.x];
                if (direction == FlowDirections.AtGoal) Arrived[i] = 1; // the order is complete: stop here
                else if (FlowDirections.IsStep(direction))
                    velocity = Speed * math.normalize((float2)FlowDirections.Step(direction));
            }

            // The ladder's step 2: with an interval above one, each unit separates every
            // SeparationInterval ticks, spread so that 1 / interval of the units is served each tick.
            if (SeparationInterval <= 1 || (i + Tick) % SeparationInterval == 0)
                velocity += Separation(i, position) / Dt;

            // The push is a per-tick correction, so a crowd cannot move a unit more than half a tile
            // a tick no matter how many bodies press on it; the flow's own speed is well inside this.
            float maxSpeed = Speed * 2f;
            float speedSq = math.lengthsq(velocity);
            Velocity[i] = speedSq > maxSpeed * maxSpeed ? velocity * (maxSpeed * math.rsqrt(speedSq)) : velocity;
        }

        /// <summary>
        /// The separation displacement of unit <paramref name="i"/> this tick, in tiles: for every
        /// overlapping neighbour, the push away scaled by k and by i's share of it. A second pass
        /// measures the overlaps again from the position the first pass would reach, which is what
        /// makes two iterations worth running.
        /// </summary>
        private float2 Separation(int i, float2 position)
        {
            float radius = Radius[i];
            float2 push = NeighbourPush(i, position, radius);
            if (Iterations <= 1 || push.Equals(float2.zero)) return push;
            return push + NeighbourPush(i, position + push, radius);
        }

        /// <summary>
        /// The push from every neighbour the unit overlaps. Only the cells the unit can reach are
        /// scanned: with a cell of CellSize tiles and an interaction distance of at most
        /// radius + SpikeSimRules.SeparationMargin, a unit in its cell's interior cannot touch a
        /// diagonal neighbour, and one near an edge only reaches the cell on that side. That is the
        /// same neighbour set as the full 3 x 3 block, for about a quarter of the candidates.
        /// </summary>
        private float2 NeighbourPush(int i, float2 position, float radius)
        {
            int2 c = math.clamp((int2)math.floor(position * InvCellSize), 0, new int2(CellsX - 1, CellsY - 1));
            float2 local = position - (float2)c * (1f / InvCellSize);
            float margin = radius + SpikeSimRules.SeparationMargin;
            int2 lo = c, hi = c;
            if (local.x < margin) lo.x = math.max(c.x - 1, 0);
            else if (local.x > (1f / InvCellSize) - margin) hi.x = math.min(c.x + 1, CellsX - 1);
            if (local.y < margin) lo.y = math.max(c.y - 1, 0);
            else if (local.y > (1f / InvCellSize) - margin) hi.y = math.min(c.y + 1, CellsY - 1);
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
                    float sum = radius + Radius[j];
                    float distanceSq = math.lengthsq(delta);
                    if (distanceSq >= sum * sum) continue;

                    float distance = math.sqrt(distanceSq);
                    float2 away = distance > 1e-4f
                        ? delta / distance
                        : new float2(1f, 0f); // exactly stacked: push apart along +x, deterministically
                    float rj = Radius[j];
                    float share = (rj * rj) / (radius * radius + rj * rj);
                    push += Strength * share * ((sum - distance) * away);
                }
            }

            return push;
        }
    }

    /// <summary>
    /// Integrates each live unit's velocity and slides along blocked tiles: the x and y moves are
    /// tested separately against the tiles the unit's body would overlap, so a blocked axis costs only
    /// that axis. A unit additionally may not come to rest on a tile its size class cannot path from
    /// (a large unit next to a wall would have no flow direction there), which is what keeps
    /// separation from pushing a large unit into a spot it can never leave. With <see cref="Halves"/>
    /// set, each unit integrates every other tick and takes the doubled step then (the plan's ladder
    /// step 3).
    /// </summary>
    [BurstCompile]
    public struct IntegrateJob : IJobParallelFor
    {
        public int Capacity;
        public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> SizeClass;
        [ReadOnly] public NativeArray<byte> Tiles;
        [ReadOnly] public NativeArray<byte> LargeGrid;
        public int Width, Height;
        public float Dt;
        public bool Halves;
        public int Tick;

        public void Execute(int i)
        {
            if (i >= Capacity || Health[i] <= 0f) return;
            if (Halves && (i + Tick) % 2 != 0) return;

            float2 position = Positions[i];
            float2 step = Velocity[i] * (Halves ? Dt * 2f : Dt);
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

        /// <summary>
        /// True when the unit's body at <paramref name="position"/> overlaps a blocked tile, or when
        /// the tile the body's centre lands on is one the unit's size class cannot stand on.
        /// </summary>
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
                // Off-grid reads as blocked; the generator's border ring blocks too.
                if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return true;
                if (Tiles[y * Width + x] != SpikeMap.Floor) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Writes the integrated positions and velocities back into the unit components, and completes an
    /// order whose unit has reached a goal cell: a unit that stopped at its destination must not be
    /// pulled back into it by the flow every time a dense crowd pushes it out.
    /// </summary>
    [BurstCompile]
    public partial struct WriteBackMovementJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<byte> Arrived;

        private void Execute(ref SpikeUnit unit, [EntityIndexInQuery] int index)
        {
            unit.Position = Positions[index];
            unit.Velocity = Velocity[index];
            if (Arrived[index] != 0) unit.FieldGoal = -1;
        }
    }
}
