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
    /// the direction changes, ending at the goal or where the field stops.
    /// </summary>
    [BurstCompile]
    public struct RouteMaintenanceJob : IJob
    {
        public const int MaxWaypoints = 256;
        public const int RetraceEvery = 40;

        public int Count, Tick, RetraceBudget;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<int> IdOf;
        [ReadOnly] public NativeArray<int> OrderSlot;
        [ReadOnly] public NativeArray<int> BlockOf;
        [ReadOnly] public NativeArray<byte> Blocks;
        public int SectorsX, SectorsY, SectorCount, CellsPerSector, CellSize, Width, Height;

        public NativeList<float2> Arena;
        public NativeArray<int> Start, RouteCount, Generation, TracedOrder, TracedId, Counters;

        public void Execute()
        {
            var scratch = new NativeList<float2>(MaxWaypoints, Allocator.Temp);
            int retraced = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Health[i] <= 0f) continue;
                int id = IdOf[i];
                int index = NetIdAllocator.IndexOf(id);
                if (index >= Start.Length) continue;
                int order = OrderSlot[i];
                bool fresh = TracedId[index] != id;
                bool changed = TracedOrder[index] != order;
                bool rotate = order >= 0 && (Tick + index) % RetraceEvery == 0 && retraced < RetraceBudget;
                if (!fresh && !changed && !rotate) continue;
                if (!fresh && order < 0) { TracedOrder[index] = order; continue; } // arrived: keep the route it walked

                Trace(order, Positions[i], scratch);
                TracedId[index] = id;
                TracedOrder[index] = order;
                if (!fresh && SameRoute(index, scratch)) continue;
                retraced++;
                int first = Arena.Length;
                for (int k = 0; k < scratch.Length; k++) Arena.Add(scratch[k]);
                Counters[1] += scratch.Length - (fresh ? 0 : RouteCount[index]);
                Start[index] = first;
                RouteCount[index] = scratch.Length;
                Counters[0]++;
                Generation[index] = Counters[0];
            }
            scratch.Dispose();
            if (Arena.Length > 2 * Counters[1] + 65536) Compact();
        }

        private void Trace(int order, float2 position, NativeList<float2> into)
        {
            into.Clear();
            int2 tile = math.clamp((int2)math.round(position), 0, new int2(Width - 1, Height - 1));
            into.Add(tile);
            if (order < 0) return;
            int2 cell = (int2)math.floor(position / CellSize);
            byte walking = DirectionAtCell(order, cell);
            int steps = 4 * math.max(Width, Height) / CellSize;
            bool first = true;
            while (into.Length < MaxWaypoints && steps-- > 0 && FlowDirections.IsStep(walking))
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
            if (into.Length < MaxWaypoints && !into[into.Length - 1].Equals(end)) into.Add(end);
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

        private bool SameRoute(int index, NativeList<float2> route)
        {
            if (RouteCount[index] != route.Length) return false;
            int first = Start[index];
            for (int k = 1; k < route.Length; k++) if (!Arena[first + k].Equals(route[k])) return false;
            return true; // the first waypoint is just where the unit stood; it may differ freely
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
