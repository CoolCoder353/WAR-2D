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
    /// and integrates without entering blocked tiles. A unit standing where its route gives no direction
    /// steers straight at the goal and reports the tile, so the route is extended to its pocket of floor.
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
                TargetKind = data.TargetKind,
                BuildingPositions = data.BuildingPositions,
                BlastRadiusByType = data.BlastRadiusByType,
                Combat = data.Combat,
                Airborne = data.Airborne,
                IdOf = data.IdOf,
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
                OrderFollowers = data.OrderPeakFollowers, // the group's size: its stop radius doesn't shrink as it arrives
                FormationOffset = data.FormationOffset,
                FormationSpeed = data.FormationSpeed,
                OrderLead = data.OrderLead,
                FormationLead = data.FormationLead,
                Loose = data.FormationLoose,
                SlotTiles = data.FormationSlot,
                Lost = data.FormationLost,
                Tiles = data.Tiles,
                Used = data.Used,
                SizeClass = data.SizeClass,
                LargeGrid = data.LargeGrid,
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
                IdOf = data.IdOf,
                Type = data.Type,
                BlastRadiusByType = data.BlastRadiusByType,
                Combat = data.Combat,
                Airborne = data.Airborne,
                Tick = clock.Tick,
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
                Positions = data.Positions,
                OrderGoal = data.OrderGoal,
                FormationSpeed = data.FormationSpeed,
                FormationLead = data.FormationLead,
                Count = count,
                Followers = data.OrderFollowers,
                Peak = data.OrderPeakFollowers,
                Lead = data.OrderLead,
            }.Schedule(state.Dependency);

            state.Dependency = new WriteBackMovementJob
            {
                Positions = data.Positions,
                Velocity = data.Velocity,
                Arrived = data.Arrived,
                Arrivals = data.Arrivals.AsParallelWriter(),
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
        [ReadOnly] public NativeArray<byte> TargetKind;
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<float> BlastRadiusByType;
        /// <summary>v0.20: a Bomber's run-in point and leap, by id index, and who is in the air this tick, by slot.</summary>
        [ReadOnly] public NativeArray<UnitCombat> Combat;
        [ReadOnly] public NativeArray<byte> Airborne;
        [ReadOnly] public NativeArray<int> IdOf;
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
        /// <summary>Each order's most followers so far (its group's size): a bigger group stops farther from the goal.</summary>
        [ReadOnly] public NativeArray<int> OrderFollowers;
        /// <summary>v0.16 formations: each unit's slot and group speed, each order's leader distance, and the radii.</summary>
        [ReadOnly] public NativeArray<float2> FormationOffset;
        [ReadOnly] public NativeArray<float> FormationSpeed;
        [ReadOnly] public NativeArray<float> OrderLead;
        [ReadOnly] public NativeArray<int> FormationLead;
        public float Loose, SlotTiles, Lost;
        /// <summary>The map's tiles and footprints: a unit steers straight at its slot only over clear floor.</summary>
        [ReadOnly] public NativeArray<byte> Tiles;
        [ReadOnly] public NativeArray<byte> Used;
        [ReadOnly] public NativeArray<byte> SizeClass;
        [ReadOnly] public NativeArray<byte> LargeGrid;
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
            bool bomber = Type[i] < BlastRadiusByType.Length && BlastRadiusByType[Type[i]] > 0f; // only Bombers leap: no other record is read
            UnitCombat leap = bomber ? Combat[NetIdAllocator.IndexOf(IdOf[i])] : default;
            if (bomber && leap.AirborneAt(IdOf[i], Tick))
            {
                // v0.20: a leaping Bomber flies its line (IntegrateJob places it, from the tick it leapt); nothing steers or pushes it.
                Velocity[i] = (leap.LeapTo - leap.LeapFrom) / (math.max(1, leap.LeapEnd - leap.LeapStart) * Dt);
                return;
            }
            if (Health[i] <= 0f || Stance[i] == Stances.Hold)
            {
                // Holding units stand still: no flow and no separation push of their own.
                Velocity[i] = float2.zero;
                return;
            }
            float2 position = Positions[i];
            float speed = Type[i] < SpeedByType.Length ? SpeedByType[Type[i]] : 0f;
            float2 velocity = float2.zero;
            bool lining = false;

            int slot = OrderSlot[i];
            // Moving units ignore their target; the others hold to fight it.
            if ((Target[i] < 0 || Stance[i] == Stances.Move) && (uint)slot < (uint)OrderLive.Length && OrderLive[slot] != 0)
            {
                bool steered = false;
                float groupSpeed = FormationSpeed[i];
                if (groupSpeed > 0f)
                {
                    // v0.16 formation: hold to the group's speed until near its own slot or lost behind the leader,
                    // and close to the goal or the slot walk straight to the slot (slots are on open floor; the integrator slides on
                    // walls). A big group's slots lie beyond SlotTiles of the goal: nearness to the slot keeps its unit from turning back.
                    float2 goal = OrderGoal[slot];
                    bool seated = FormationRules.HasSeat(FormationOffset[i]); // a member with no slot stops around the goal
                    float2 seat = seated ? goal + FormationOffset[i] : goal;
                    float toSeat = math.distance(position, seat), fromGoal = math.distance(position, goal);
                    int lead = FormationLead[i];
                    bool lost = (uint)lead < (uint)OrderLead.Length && fromGoal - OrderLead[lead] > Lost;
                    if (!lost && toSeat > Loose) speed = math.min(speed, groupSpeed);
                    if (seated && (fromGoal <= SlotTiles || toSeat <= SlotTiles) && ClearLine(position, seat, Radius[i], SizeClass[i]))
                    {
                        steered = true;
                        if (toSeat <= 0.25f) Arrived[i] = 1;
                        else velocity = speed * (seat - position) / toSeat;
                    }
                }
                if (!steered)
                {
                    byte direction = Direction(slot, position);
                    float2 toGoal = OrderGoal[slot] - position;
                    float distance = math.length(toGoal);
                    // A group packs around the goal: the stop radius grows with the square root of its size. An attacker
                    // closes until its target is in range (the check above stops it): the group's radius could stop it short for good.
                    bool attacking = Stance[i] == Stances.Attack;
                    float arriveRadius = attacking ? 0.5f : 0.5f + 0.4f * math.sqrt(math.max(1, OrderFollowers[slot]));
                    if (direction == FlowDirections.AtGoal)
                    {
                        // On a goal cell: close in on the goal itself, then stop. A goal cell can lie behind a short wall (or one
                        // built since), and walking straight would press on it for good, so a unit with no clear line stops here.
                        if (distance <= arriveRadius || (!attacking && !ClearLine(position, OrderGoal[slot], Radius[i], SizeClass[i]))) Arrived[i] = 1;
                        else velocity = speed * toGoal / distance;
                    }
                    else if (direction == FlowDirections.Stop)
                    {
                        // The goal is walled off from here, and this is as near as the unit can get, so the order ends. An attack-move
                        // shooting a wall never gets here (it holds to fight above), so it stays on its order while the wall stands and
                        // goes on through when it falls; one with nothing to shoot ends, freeing its order slot and the retries for a way.
                        Arrived[i] = 1;
                    }
                    else if (FlowDirections.IsStep(direction))
                    {
                        lining = EntersGap(i, position, direction);
                        // Aim at the centre of the next cell rather than along the raw step, so units
                        // line up with corridors and gaps the cell grid says are open.
                        int2 cell = (int2)math.floor(position / FieldCellSize);
                        float2 next = ((float2)(cell + FlowDirections.Step(direction)) + 0.5f) * FieldCellSize;
                        velocity = speed * math.normalizesafe(next - position, math.normalize((float2)FlowDirections.Step(direction)));
                    }
                    else if (NearestFieldCell(slot, position, out float2 rejoin))
                    {
                        // On a floor tile in a field cell that holds wall (the field runs on 2×2-tile cells, so
                        // cave edges are full of these): step back onto the field instead of heading straight
                        // at the goal, which would pin the unit against the wall.
                        velocity = speed * math.normalizesafe(rejoin - position);
                    }
                    else
                    {
                        // Off the route. Until the route is built the unit waits for it (owner, 2026-11-02: heading
                        // straight at the goal meanwhile ground units into walls; bug-review third scan). Once it
                        // exists, head for the goal and report the sector so the route grows to cover it.
                        if (distance <= arriveRadius) Arrived[i] = 1;
                        else if (OrderReady[slot] != 0)
                        {
                            velocity = speed * toGoal / distance;
                            int2 tile = (int2)math.floor(position);
                            if ((i + Tick) % Slice == 0) RouteMisses.Enqueue(new int2(slot, tile.y * Width + tile.x));
                        }
                    }
                }
            }

            // A Bomber with a target runs straight at its run-in point (v0.20: the army's centre, or a building side's middle; it leaps
            // or detonates in combat).
            int target = Target[i];
            if (target >= 0 && Stance[i] != Stances.Move && Type[i] < BlastRadiusByType.Length && BlastRadiusByType[Type[i]] > 0f)
            {
                byte kind = TargetKind[i];
                UnitCombat run = Combat[NetIdAllocator.IndexOf(IdOf[i])];
                float2 at = run.Id == IdOf[i] && run.HasRunPoint != 0 ? run.RunPoint : kind == TargetKinds.Unit ? Positions[target] : kind == TargetKinds.Wall ? new float2(target % Width + 0.5f, target / Width + 0.5f) : BuildingPositions[target];
                velocity = speed * math.normalizesafe(at - position);
            }

            if (!lining && (SeparationInterval <= 1 || (i + Tick) % SeparationInterval == 0))
                velocity += NeighbourPush(i, position, Radius[i]) / Dt;

            // The push is a per-tick correction; cap it so a crowd cannot fling a unit.
            float maxSpeed = math.max(speed, 1f) * 2f;
            float speedSq = math.lengthsq(velocity);
            Velocity[i] = speedSq > maxSpeed * maxSpeed ? velocity * (maxSpeed * math.rsqrt(speedSq)) : velocity;
        }

        /// <summary>
        /// The centre of the nearest of the eight neighbouring field cells that has a direction, when the
        /// unit's own cell has none. False when none does (the unit really is off the route).
        /// </summary>
        private bool NearestFieldCell(int handle, float2 position, out float2 centre)
        {
            centre = default;
            int2 cell = (int2)math.floor(position / FieldCellSize);
            float best = float.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                float2 c = ((float2)(cell + new int2(dx, dy)) + 0.5f) * FieldCellSize;
                byte direction = Direction(handle, c);
                if (direction != FlowDirections.AtGoal && direction != FlowDirections.Stop && !FlowDirections.IsStep(direction)) continue;
                float d = math.distancesq(c, position);
                if (d >= best) continue;
                best = d;
                centre = c;
            }
            return best < float.MaxValue;
        }

        /// <summary>
        /// True when a body of <paramref name="radius"/> can slide straight from <paramref name="a"/> to <paramref name="b"/>:
        /// every tile under its corners, sampled every half tile, is open floor (playtest 2026-11-02: checking the centre
        /// line alone let a body clip a rock corner beside it and stick there). A large body also needs its centre where
        /// <see cref="LargeGrid"/> lets it stand, as the integrator does: a lone rock can sit between its corners.
        /// </summary>
        private bool ClearLine(float2 a, float2 b, float radius, byte sizeClass)
        {
            float length = math.distance(a, b);
            int steps = (int)math.ceil(length * 2f);
            for (int k = 1; k <= steps; k++)
            {
                float2 p = math.lerp(a, b, k / (float)steps);
                if (!Open(p + new float2(-radius, -radius)) || !Open(p + new float2(radius, -radius))
                    || !Open(p + new float2(-radius, radius)) || !Open(p + new float2(radius, radius))) return false;
                int2 centre = (int2)math.floor(p);
                if (sizeClass == 1 && LargeGrid[centre.y * Width + centre.x] != 0) return false;
            }
            return true;
        }

        private bool Open(float2 p)
        {
            int2 t = (int2)math.floor(p);
            if ((uint)t.x >= (uint)Width || (uint)t.y >= (uint)Height) return false;
            int index = t.y * Width + t.x;
            return TileRules.IsFloor(Tiles[index]) && Used[index] == 0;
        }

        /// <summary>
        /// True when a straight step enters a tile with no room on either side, where a body must be on the tile's centre line
        /// to fit. Two units pushing each other apart there balance off that line for good, so a unit lining up takes no push.
        /// </summary>
        private bool EntersGap(int i, float2 position, byte direction)
        {
            if (direction % 2 != 0) return false; // the odd directions are the diagonals
            int2 step = FlowDirections.Step(direction);
            int2 next = (int2)math.floor(position) + step;
            int2 side = new int2(step.y, step.x);
            return ClosedFor(i, next + side) && ClosedFor(i, next - side);
        }

        private bool ClosedFor(int i, int2 tile) =>
            !Open((float2)tile + 0.5f) || (SizeClass[i] == 1 && LargeGrid[tile.y * Width + tile.x] != 0);

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

        /// <summary>
        /// How much more of a push one of two units takes than the other. With equal shares, two big units heading for one tile
        /// from opposite sides balance just short of it and never move again; an uneven pair can't balance. Only between two units on
        /// live orders: uneven pushes don't sum to zero, so a group that has stopped would drift for good instead of settling.
        /// </summary>
        private const float GiveWay = 0.25f;

        private bool OnOrder(int u) => (uint)OrderSlot[u] < (uint)OrderLive.Length && OrderLive[OrderSlot[u]] != 0;

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
            bool moving = OnOrder(i);
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = CellStart[cell]; k < CellStart[cell + 1]; k++)
                {
                    int j = Sorted[k];
                    float2 delta = position - Positions[j];
                    float distanceSq = math.lengthsq(delta);
                    if (distanceSq >= margin * margin || j == i || Health[j] <= 0f || Airborne[j] != 0) continue; // margin: no body reaches further
                    float rj = Radius[j];
                    float sum = radius + rj;
                    if (distanceSq >= sum * sum) continue;
                    float distance = math.sqrt(distanceSq);
                    // Exactly stacked: push apart along a direction derived from the pair, deterministically.
                    float2 away = distance > 1e-4f ? delta / distance : (i < j ? new float2(1f, 0f) : new float2(-1f, 0f));
                    float share = (rj * rj) / (radius * radius + rj * rj);
                    if (moving && OnOrder(j)) share *= i < j ? 1f - GiveWay : 1f + GiveWay;
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
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<float> BlastRadiusByType;
        [ReadOnly] public NativeArray<UnitCombat> Combat;
        [ReadOnly] public NativeArray<byte> Airborne;
        public int Tick;
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
            bool bomber = Type[i] < BlastRadiusByType.Length && BlastRadiusByType[Type[i]] > 0f;
            UnitCombat leap = bomber ? Combat[NetIdAllocator.IndexOf(IdOf[i])] : default;
            if (bomber && leap.AirborneAt(IdOf[i], Tick))
            {
                // v0.20: along the leap's line, over units (a leap's line is clear of walls, rock and buildings), reaching its end
                // on the leap's last tick so it lands there.
                float t = math.saturate((Tick + 1 - leap.LeapStart) / (float)math.max(1, leap.LeapEnd - leap.LeapStart));
                Positions[i] = math.lerp(leap.LeapFrom, leap.LeapTo, t);
                return;
            }
            float2 position = Positions[i];
            float2 step = Velocity[i] * Dt;
            float radius = Radius[i];
            byte sizeClass = SizeClass[i];
            float2 moved = position;
            if (step.x != 0f)
            {
                float2 candidate = new float2(position.x + step.x, position.y);
                if (!Blocked(candidate, radius, sizeClass) || Escapes(position, candidate, radius, sizeClass)) moved.x = candidate.x;
            }
            if (step.y != 0f)
            {
                float2 candidate = new float2(moved.x, position.y + step.y);
                if (!Blocked(candidate, radius, sizeClass) || Escapes(moved, candidate, radius, sizeClass)) moved.y = candidate.y;
            }
            Positions[i] = moved;
        }

        /// <summary>
        /// True when a body already overlapping something may take this step anyway, as long as its centre stays on free floor
        /// (playtest 2026-11-04: a Heavy spawned at a producer's side, pushed out by a new building or walled in overlaps a tile,
        /// and every step from there overlapped it too, so it stood frozen for good).
        /// </summary>
        private bool Escapes(float2 from, float2 to, float radius, byte sizeClass)
        {
            if (!Blocked(from, radius, sizeClass)) return false;
            int2 centre = (int2)math.floor(to);
            if ((uint)centre.x >= (uint)Width || (uint)centre.y >= (uint)Height) return false;
            int t = centre.y * Width + centre.x;
            return TileRules.IsFloor(Tiles[t]) && Used[t] == 0;
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
                if (!TileRules.IsFloor(Tiles[t]) || Used[t] != 0) return true;
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
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float2> OrderGoal;
        [ReadOnly] public NativeArray<float> FormationSpeed;
        [ReadOnly] public NativeArray<int> FormationLead;
        public int Count;
        public NativeArray<int> Followers;
        /// <summary>Each order's most followers so far (<see cref="OrderBook.PeakFollowers"/>), raised here.</summary>
        public NativeArray<int> Peak;
        /// <summary>v0.16: each formation's leading follower's distance to the goal, under its lead handle (<see cref="float.MaxValue"/> when none).</summary>
        public NativeArray<float> Lead;

        public void Execute()
        {
            for (int h = 0; h < Followers.Length; h++) { Followers[h] = 0; Lead[h] = float.MaxValue; }
            for (int i = 0; i < Count; i++)
            {
                int slot = OrderSlot[i];
                if ((uint)slot >= (uint)Followers.Length || Arrived[i] != 0 || Health[i] <= 0f) continue;
                Followers[slot]++;
                int lead = FormationLead[i];
                if (FormationSpeed[i] > 0f && (uint)lead < (uint)Lead.Length) Lead[lead] = math.min(Lead[lead], math.distance(Positions[i], OrderGoal[slot]));
            }
            for (int h = 0; h < Followers.Length; h++) Peak[h] = math.max(Peak[h], Followers[h]);
        }
    }

    /// <summary>
    /// Writes position and velocity back, and clears the order (and moving stance) of a unit that arrived,
    /// reporting it in <see cref="SimData.Arrivals"/> for its next waypoint.
    /// </summary>
    [BurstCompile]
    public partial struct WriteBackMovementJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<byte> Arrived;
        public NativeQueue<int>.ParallelWriter Arrivals;
        public int Count;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Count) return;
            unit.Position = Positions[index];
            unit.Velocity = Velocity[index];
            if (Arrived[index] != 0)
            {
                unit.OrderSlot = -1;
                unit.FormationSpeed = 0f; // v0.16: the formation ends at the slot
                unit.FormationOffset = float2.zero;
                unit.FormationLead = 0;
                Arrivals.Enqueue(unit.Id);
                if (unit.Stance == Stances.Move || unit.Stance == Stances.AttackMove) unit.Stance = Stances.Idle;
            }
        }
    }
}
