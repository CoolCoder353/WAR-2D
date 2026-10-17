using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Pathing;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// Every unit's current route, by id index: a slice of one shared waypoint arena. Waypoints are
    /// flow-cell centres in tile units (whole numbers, since cells are 2 tiles), the first one the
    /// unit's own rounded position. A route's generation changes whenever its waypoints do, which is
    /// what tells the encoder to send a MoveOrder.
    /// </summary>
    public sealed class RouteStore : IDisposable
    {
        internal NativeList<float2> Arena;
        internal NativeArray<int> Start, Count, Generation;
        /// <summary>Per index: the order and id the route was traced for, so a change triggers a retrace.</summary>
        internal NativeArray<int> TracedOrder, TracedId;
        /// <summary>[0] the generation counter, [1] live waypoints.</summary>
        internal NativeArray<int> Counters;
        private bool disposed;

        public RouteStore(int idCapacity, Allocator allocator)
        {
            Arena = new NativeList<float2>(64 * 1024, allocator);
            Start = new NativeArray<int>(idCapacity, allocator);
            Count = new NativeArray<int>(idCapacity, allocator);
            Generation = new NativeArray<int>(idCapacity, allocator);
            TracedOrder = new NativeArray<int>(idCapacity, allocator);
            TracedId = new NativeArray<int>(idCapacity, allocator);
            Counters = new NativeArray<int>(2, allocator);
        }

        /// <summary>The arena (valid until the next maintenance pass).</summary>
        public NativeArray<float2> Waypoints => Arena.AsArray();
        public int First(int index) => Count[index] > 0 ? Start[index] : -1;
        public int CountOf(int index) => Count[index];
        public int GenerationOf(int index) => Generation[index];

        /// <summary>Sets a route directly (tests and tools).</summary>
        public void Set(int index, params float2[] waypoints)
        {
            Start[index] = Arena.Length;
            foreach (float2 w in waypoints) Arena.Add(w);
            Count[index] = waypoints.Length;
            Counters[0] = Counters[0] + 1;
            Generation[index] = Counters[0];
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Arena.Dispose(); Start.Dispose(); Count.Dispose(); Generation.Dispose();
            TracedOrder.Dispose(); TracedId.Dispose(); Counters.Dispose();
        }
    }

    /// <summary>
    /// Keeps the routes current: a unit that is new or got a new order is traced at once; units
    /// already on an order are re-traced on a rotation (the field may have been rebuilt), within a
    /// budget per tick. Tracing walks the order's flow cells from the unit and keeps a waypoint where
    /// the direction changes, ending at the goal or where the field stops. The traces run in parallel
    /// (<see cref="RouteTraceJob"/>); one job then writes the changed routes into the arena.
    /// </summary>
    public struct RouteMaintenance
    {
        public const int MaxWaypoints = 256;
        public const int RetraceEvery = 40;

        public int Count, Tick, RetraceBudget;
        /// <summary>Tiles the sent route may cut across the traced cells (see <see cref="RouteTraceJob"/>).</summary>
        public float Tolerance;
        public NativeArray<float2> Positions;
        public NativeArray<float> Health;
        public NativeArray<int> IdOf;
        public NativeArray<int> OrderSlot;
        public NativeArray<int> BlockOf;
        public NativeArray<byte> Blocks;
        public int SectorsX, SectorsY, SectorCount, CellsPerSector, CellSize, Width, Height;

        public NativeList<float2> Arena;
        public NativeArray<int> Start, RouteCount, Generation, TracedOrder, TracedId, Counters;

        /// <summary>Schedules the parallel traces and the commit after <paramref name="dependsOn"/>.</summary>
        public JobHandle Schedule(JobHandle dependsOn)
        {
            var stream = new NativeStream(math.max(1, Count), Allocator.TempJob);
            JobHandle traced = new RouteTraceJob
            {
                Tick = Tick, Tolerance = Tolerance, Positions = Positions, Health = Health, IdOf = IdOf, OrderSlot = OrderSlot,
                BlockOf = BlockOf, Blocks = Blocks, SectorsX = SectorsX, SectorCount = SectorCount, CellsPerSector = CellsPerSector,
                CellSize = CellSize, Width = Width, Height = Height,
                Arena = Arena.AsDeferredJobArray(), Start = Start, RouteCount = RouteCount, TracedOrder = TracedOrder, TracedId = TracedId,
                Out = stream.AsWriter(),
            }.Schedule(Count, 256, dependsOn);
            JobHandle committed = new RouteCommitJob
            {
                Count = Count, RetraceBudget = RetraceBudget, IdOf = IdOf, OrderSlot = OrderSlot, In = stream.AsReader(),
                Arena = Arena, Start = Start, RouteCount = RouteCount, Generation = Generation, TracedOrder = TracedOrder, TracedId = TracedId, Counters = Counters,
            }.Schedule(traced);
            return stream.Dispose(committed);
        }

        /// <summary>Runs both jobs to completion (tests and tools).</summary>
        public void Run() => Schedule(default).Complete();
    }

    /// <summary>What the trace found for one unit, the first item of its stream entry.</summary>
    internal enum RouteResult
    {
        /// <summary>Traced, and still the route it has.</summary>
        Same = 1,
        /// <summary>A new unit or order: a route follows, always written.</summary>
        Fresh = 2,
        /// <summary>A rotation found a different route: a route follows, written within the budget.</summary>
        Rotated = 3,
        /// <summary>The order ended: keep the route it walked.</summary>
        Arrived = 4,
    }

    /// <summary>
    /// Traces each unit that needs it (see <see cref="RouteMaintenance"/>) and writes the result to its own
    /// stream entry. It only reads the routes; <see cref="RouteCommitJob"/> writes them.
    /// </summary>
    [BurstCompile]
    internal struct RouteTraceJob : IJobParallelFor
    {
        public int Tick;
        public float Tolerance;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<int> BlockOf;
        [ReadOnly] public NativeArray<byte> Blocks;
        public int SectorsX, SectorCount, CellsPerSector, CellSize, Width, Height;
        [ReadOnly] public NativeArray<float2> Arena;
        [ReadOnly] public NativeArray<int> Start, RouteCount, TracedOrder, TracedId;
        public NativeStream.Writer Out;

        public void Execute(int i)
        {
            Out.BeginForEachIndex(i);
            Classify(i);
            Out.EndForEachIndex();
        }

        private void Classify(int i)
        {
            if (Health[i] <= 0f) return;
            int id = IdOf[i];
            int index = NetIdAllocator.IndexOf(id);
            if (index >= Start.Length) return;
            int order = OrderSlot[i];
            bool fresh = TracedId[index] != id;
            bool changed = TracedOrder[index] != order;
            bool rotate = order >= 0 && (Tick + index) % RouteMaintenance.RetraceEvery == 0;
            if (!fresh && !changed && !rotate) return;
            if (!fresh && order < 0) { Out.Write(RouteResult.Arrived); return; }

            var route = new FixedList4096Bytes<float2>();
            Trace(order, Positions[i], ref route);
            if (!fresh && SameRoute(index, ref route)) { Out.Write(RouteResult.Same); return; }
            Out.Write(fresh || changed ? RouteResult.Fresh : RouteResult.Rotated);
            Out.Write(route.Length);
            for (int k = 0; k < route.Length; k++) Out.Write(route[k]);
        }

        private void Trace(int order, float2 position, ref FixedList4096Bytes<float2> into)
        {
            int2 tile = math.clamp((int2)math.round(position), 0, new int2(Width - 1, Height - 1));
            into.Add(tile);
            if (order < 0) return;
            int2 cell = (int2)math.floor(position / CellSize);
            byte walking = DirectionAtCell(order, cell);
            int steps = 4 * math.max(Width, Height) / CellSize;
            bool first = true;
            while (into.Length < RouteMaintenance.MaxWaypoints && steps-- > 0 && FlowDirections.IsStep(walking))
            {
                int2 next = cell + FlowDirections.Step(walking);
                byte direction = DirectionAtCell(order, next);
                if (direction == FlowDirections.None) break;
                cell = next;
                // The unit steers for the next cell's centre first (see SimMovementSystem), so the route
                // does too; after that it keeps a waypoint only where the direction changes.
                if (first || direction != walking) into.Add(Centre(cell));
                first = false;
                walking = direction;
            }
            float2 end = Centre(cell);
            if (into.Length < RouteMaintenance.MaxWaypoints && !into[into.Length - 1].Equals(end)) into.Add(end);
            Simplify(ref into);
        }

        /// <summary>
        /// Drops turns the route can cut, working back from the goal: from each kept waypoint, the next one kept is
        /// the turn furthest back whose straight line stays within <see cref="Tolerance"/> of every turn in between
        /// (the cells between two turns are on a straight line, so the whole traced path stays within it). A
        /// staircase of eight-way steps across a cave becomes one segment. Working from the goal keeps the same
        /// waypoints as the unit walks (only the one next to it can change), so <see cref="SameRoute"/> still sees
        /// the route as unchanged. The unit's own tile, its first cell and the goal are always kept.
        /// </summary>
        private void Simplify(ref FixedList4096Bytes<float2> route)
        {
            if (Tolerance <= 0f || route.Length <= 3) return;
            Reverse(ref route, 1, route.Length);
            float limit = Tolerance * Tolerance;
            int kept = 2, anchor = 1;
            while (anchor < route.Length - 1)
            {
                int next = anchor + 1;
                for (int j = anchor + 2; j < route.Length; j++)
                {
                    bool fits = true;
                    for (int k = anchor + 1; k < j && fits; k++) fits = DistanceSq(route[k], route[anchor], route[j]) <= limit;
                    if (!fits) break;
                    next = j;
                }
                route[kept++] = route[next];
                anchor = next;
            }
            route.Length = kept;
            Reverse(ref route, 1, kept);
        }

        private static void Reverse(ref FixedList4096Bytes<float2> list, int from, int to)
        {
            for (int i = from, j = to - 1; i < j; i++, j--) (list[i], list[j]) = (list[j], list[i]);
        }

        internal static float DistanceSq(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a;
            float t = math.saturate(math.dot(p - a, ab) / math.max(1e-6f, math.dot(ab, ab)));
            return math.lengthsq(a + ab * t - p);
        }

        private float2 Centre(int2 cell) => (float2)(cell * CellSize) + CellSize * 0.5f;

        private byte DirectionAtCell(int handle, int2 cell)
        {
            int2 tile = cell * CellSize;
            if ((uint)handle >= OrderFieldTable.MaxOrders || tile.x < 0 || tile.y < 0 || tile.x >= Width || tile.y >= Height) return FlowDirections.None;
            int sx = tile.x / SectorGraph.SectorSize, sy = tile.y / SectorGraph.SectorSize;
            int block = BlockOf[handle * SectorCount + sy * SectorsX + sx];
            if (block < 0) return FlowDirections.None;
            return Blocks[block * CellsPerSector * CellsPerSector + cell.y % CellsPerSector * CellsPerSector + cell.x % CellsPerSector];
        }

        /// <summary>
        /// True when the retrace only shows the unit further along the route it already has: everything after the
        /// retrace's first steps (the unit's own tile, the next cell and the waypoint after it, which change as it
        /// walks) is the end of the old route. The client's prediction already follows that route, so resending it
        /// would only cost bandwidth (on winding cave routes, nearly every rotation did).
        /// </summary>
        private bool SameRoute(int index, ref FixedList4096Bytes<float2> route)
        {
            int old = RouteCount[index], first = Start[index];
            if (old <= 0 || route.Length < 2) return false;
            if (route.Length <= 3) return Arena[first + old - 1].Equals(route[route.Length - 1]);
            int tail = route.Length - 3; // after the unit's tile, its first cell and the simplified waypoint next to it
            if (old - tail < 2) return false;
            for (int k = 0; k < tail; k++) if (!Arena[first + old - tail + k].Equals(route[3 + k])) return false;
            // ...and the waypoint next to the unit lies on the old route (not round the other side of something).
            float near = math.max(1f, 2f * Tolerance);
            for (int k = first; k < first + old - 1; k++) if (DistanceSq(route[2], Arena[k], Arena[k + 1]) <= near * near) return true;
            return false;
        }
    }

    /// <summary>Writes the traced results into the route store, in unit order: new routes into the arena, a new generation each.</summary>
    [BurstCompile]
    internal struct RouteCommitJob : IJob
    {
        public int Count, RetraceBudget;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> OrderSlot;
        public NativeStream.Reader In;
        public NativeList<float2> Arena;
        public NativeArray<int> Start, RouteCount, Generation, TracedOrder, TracedId, Counters;

        public void Execute()
        {
            int retraced = 0;
            for (int i = 0; i < Count; i++)
            {
                if (In.BeginForEachIndex(i) == 0) { In.EndForEachIndex(); continue; }
                int id = IdOf[i];
                int index = NetIdAllocator.IndexOf(id);
                RouteResult result = In.Read<RouteResult>();
                if (result == RouteResult.Arrived) TracedOrder[index] = OrderSlot[i];
                else if (result == RouteResult.Same) { TracedId[index] = id; TracedOrder[index] = OrderSlot[i]; }
                else
                {
                    int length = In.Read<int>();
                    if (result == RouteResult.Rotated && retraced >= RetraceBudget)
                    {
                        for (int k = 0; k < length; k++) In.Read<float2>(); // over budget: the next rotation tries again
                    }
                    else
                    {
                        bool fresh = TracedId[index] != id;
                        int first = Arena.Length;
                        for (int k = 0; k < length; k++) Arena.Add(In.Read<float2>());
                        Counters[1] += length - (fresh ? 0 : RouteCount[index]);
                        Start[index] = first;
                        RouteCount[index] = length;
                        Counters[0]++;
                        Generation[index] = Counters[0];
                        TracedId[index] = id;
                        TracedOrder[index] = OrderSlot[i];
                        retraced++;
                    }
                }
                In.EndForEachIndex();
            }
            if (Arena.Length > 2 * Counters[1] + 65536) Compact();
        }

        private void Compact()
        {
            var compacted = new NativeList<float2>(Counters[1] + 1024, Allocator.Temp);
            int live = 0;
            for (int index = 0; index < Start.Length; index++)
            {
                if (RouteCount[index] <= 0) continue;
                int first = compacted.Length;
                for (int k = 0; k < RouteCount[index]; k++) compacted.Add(Arena[Start[index] + k]);
                Start[index] = first;
                live += RouteCount[index];
            }
            Arena.Clear();
            Arena.AddRange(compacted.AsArray());
            compacted.Dispose();
            Counters[1] = live;
        }
    }
}
