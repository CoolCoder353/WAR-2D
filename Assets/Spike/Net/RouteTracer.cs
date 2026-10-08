using System;
using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Turns a flow field into the waypoint polyline a client is sent: walk the unit's field from its
    /// tile to the goal keeping only the cells where the direction changes, then (as an experiment)
    /// pull the line taut against the tile grid. The route is what the hybrid model's client predicts
    /// along, so its fidelity against the simulation is what the correction rate measures.
    /// </summary>
    public static class RouteTracer
    {
        /// <summary>Steps a trace may take before it gives up; a route longer than this is capped.</summary>
        public static int MaxSteps(int width, int height) => 4 * math.max(width, height);

        /// <summary>
        /// Traces the route for a unit standing on <paramref name="start"/> and following the order
        /// slot whose direction grid begins at <paramref name="orderBase"/>. The route is the start
        /// cell, one waypoint per direction change, and the cell the walk ends on (the goal, or the
        /// last cell before a blocked one). Always at least one waypoint: a unit with no route at all
        /// still predicts from its own tile. Returns the waypoint count written to
        /// <paramref name="into"/>.
        /// </summary>
        public static int Trace(NativeArray<byte> directions, int orderBase, int width, int height,
            int2 start, NativeList<int2> into, int maxWaypoints)
        {
            into.Clear();
            start = math.clamp(start, int2.zero, new int2(width - 1, height - 1));
            into.Add(start);

            int steps = MaxSteps(width, height);
            int2 tile = start;
            byte walking = DirectionAt(directions, orderBase, width, height, tile);
            while (into.Length < maxWaypoints && steps-- > 0 && FlowDirections.IsStep(walking))
            {
                int2 next = tile + FlowDirections.Step(walking);
                if ((uint)next.x >= (uint)width || (uint)next.y >= (uint)height) break;
                tile = next;
                byte direction = DirectionAt(directions, orderBase, width, height, tile);
                if (direction != walking) into.Add(tile);
                walking = direction;
            }
            if (into.Length < maxWaypoints && !into[into.Length - 1].Equals(tile)) into.Add(tile);
            return into.Length;
        }

        /// <summary>
        /// Greedy line-of-sight smoothing: keep the start, then the farthest waypoint whose straight
        /// line from the last kept one does not cross a blocked tile, and the last waypoint. Returns
        /// the waypoint count written to <paramref name="into"/>; the caller must not pass the same
        /// list as input and output.
        /// </summary>
        public static int Smooth(NativeArray<int2> tiles, int first, int count, NativeArray<byte> grid,
            int width, int height, NativeList<int2> into)
        {
            into.Clear();
            if (count <= 0) return 0;
            into.Add(tiles[first]);
            int kept = 0;
            while (kept < count - 1)
            {
                int next = count - 1; // when nothing is visible, the next kept point is the end
                for (int candidate = count - 1; candidate > kept + 1; candidate--)
                {
                    if (!LineOfSight(tiles[first + kept], tiles[first + candidate], grid, width, height)) continue;
                    next = candidate;
                    break;
                }
                into.Add(tiles[first + next]);
                kept = next;
            }
            return into.Length;
        }

        /// <summary>
        /// True when the straight line between two tile centres crosses only floor tiles, using the
        /// same no-corner-cutting rule the flow fields path with.
        /// </summary>
        public static bool LineOfSight(int2 from, int2 to, NativeArray<byte> grid, int width, int height)
        {
            int dx = math.abs(to.x - from.x), dy = math.abs(to.y - from.y);
            int sx = from.x < to.x ? 1 : -1, sy = from.y < to.y ? 1 : -1;
            int x = from.x, y = from.y;
            int error = dx - dy;
            while (true)
            {
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
                if (grid[y * width + x] != SpikeMap.Floor) return false;
                if (x == to.x && y == to.y) return true;

                int doubled = 2 * error;
                bool stepX = doubled > -dy, stepY = doubled < dx;
                if (stepX && stepY)
                {
                    // A diagonal may not slip between two blocked tiles.
                    if ((uint)(x + sx) >= (uint)width || (uint)(y + sy) >= (uint)height) return false;
                    if (grid[y * width + x + sx] != SpikeMap.Floor || grid[(y + sy) * width + x] != SpikeMap.Floor) return false;
                    x += sx;
                    y += sy;
                    error += -dy + dx;
                }
                else if (stepX)
                {
                    x += sx;
                    error -= dy;
                }
                else
                {
                    y += sy;
                    error += dx;
                }
            }
        }

        private static byte DirectionAt(NativeArray<byte> directions, int orderBase, int width, int height, int2 tile)
        {
            if ((uint)tile.x >= (uint)width || (uint)tile.y >= (uint)height) return FlowDirections.None;
            return directions[orderBase + tile.y * width + tile.x];
        }
    }

    /// <summary>
    /// Every unit's route, as the server computed it: for each unit slot the waypoints the client was
    /// sent (tile centres, <c>float2</c> so <see cref="PathFollower.Evaluate"/> can walk them
    /// directly) and a generation that changes only when the route really did. The arena keeps the
    /// routes of the current match only; a re-trace that returns the same polyline costs nothing and
    /// sends nothing.
    /// </summary>
    public sealed class RouteStore : IDisposable
    {
        private const int MinCapacity = 4096;

        /// <summary>Waypoints the arena may hold before it is compacted whatever else says (32 MB).</summary>
        private const int MaxArena = 4 * 1024 * 1024;

        private NativeList<float2> arena;
        private NativeArray<int> start;
        private NativeArray<int> count;
        private NativeArray<int> generation;
        private readonly int capacity;
        private readonly Allocator allocator;
        private long liveWaypoints;
        private int generationCounter;
        private bool disposed;

        /// <summary>Allocates one route slice per unit id, with an empty waypoint arena.</summary>
        public RouteStore(int idCapacity, Allocator allocator)
        {
            this.capacity = math.max(1, idCapacity);
            this.allocator = allocator;
            arena = new NativeList<float2>(math.max(MinCapacity, math.min(this.capacity, 1 << 16) * 4), allocator);
            start = new NativeArray<int>(this.capacity, allocator);
            count = new NativeArray<int>(this.capacity, allocator);
            generation = new NativeArray<int>(this.capacity, allocator);
            for (int i = 0; i < this.capacity; i++) start[i] = -1;
        }

        /// <summary>The waypoints every route indexes into.</summary>
        public NativeArray<float2> Waypoints => arena.AsArray();

        /// <summary>Per-id first waypoint (-1 for none), for jobs that cannot call <see cref="First"/>.</summary>
        public NativeArray<int> Starts => start;

        /// <summary>Per-id waypoint count, for jobs.</summary>
        public NativeArray<int> Counts => count;

        /// <summary>Per-id route generation, for jobs.</summary>
        public NativeArray<int> Generations => generation;

        /// <summary>
        /// First waypoint of a unit's route in <see cref="Waypoints"/>, or -1. Routes are keyed by the
        /// unit's id, not by its ECS slot: the simulation's query slots churn by a quarter of the army
        /// a tick as units spawn and die, and anything a slot holds belongs to a different unit next
        /// tick.
        /// </summary>
        public int First(int id) => (uint)id < (uint)capacity ? start[id] : -1;

        /// <summary>Waypoints in a unit's route.</summary>
        public int Count(int id) => (uint)id < (uint)capacity ? count[id] : 0;

        /// <summary>Bumped whenever the unit's route changes; the encoder sends a MoveOrder on a change.</summary>
        public int Generation(int id) => (uint)id < (uint)capacity ? generation[id] : 0;

        /// <summary>Waypoints stored across every live route.</summary>
        public long LiveWaypoints => liveWaypoints;

        /// <summary>Bytes the arena has grown to, for the bench's memory line.</summary>
        public long ArenaBytes => (long)arena.Capacity * 8;

        /// <summary>
        /// Sets a unit's route from traced tiles. A route identical to the one already stored is
        /// ignored, so the caller may re-trace as often as it likes without producing traffic.
        /// Returns true when the route changed.
        /// </summary>
        public bool Set(int id, NativeList<int2> tiles)
        {
            ThrowIfDisposed();
            if ((uint)id >= (uint)capacity || tiles.Length == 0) return false;

            if (start[id] >= 0 && count[id] == tiles.Length && SameAs(id, tiles)) return false;

            int first = arena.Length;
            for (int i = 0; i < tiles.Length; i++) arena.Add((float2)tiles[i] + new float2(0.5f));
            // The route this replaces stays in the arena until the next compaction, but it is no
            // longer live: the live count is what the compaction threshold is judged against, and
            // forgetting the subtraction makes the arena grow without bound.
            liveWaypoints += tiles.Length - count[id];

            start[id] = first;
            count[id] = tiles.Length;
            generation[id] = ++generationCounter;
            MaybeCompact();
            return true;
        }

        /// <summary>Disposes the arena and the per-slot slices. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            arena.Dispose();
            start.Dispose();
            count.Dispose();
            generation.Dispose();
        }

        private bool SameAs(int id, NativeList<int2> tiles)
        {
            for (int i = 0; i < tiles.Length; i++)
            {
                float2 stored = arena[start[id] + i];
                int2 tile = (int2)math.floor(stored);
                if (tile.x != tiles[i].x || tile.y != tiles[i].y) return false;
            }
            return true;
        }

        /// <summary>
        /// Rewrites the arena from the live routes when the abandoned ones (from a route that got
        /// longer and moved) take more room than the live ones plus a slack.
        /// </summary>
        private void MaybeCompact()
        {
            if (arena.Length <= MinCapacity) return;
            // Compact when the abandoned routes take more room than the live ones, or when the arena
            // has grown past a hard cap whatever the accounting says - a seven-figure arena is tens of
            // megabytes, and a runaway one is a crash rather than a metric.
            bool overLive = arena.Length > liveWaypoints * 2 + MinCapacity;
            bool overCap = arena.Length > MaxArena && arena.Length > liveWaypoints + MinCapacity;
            if (!overLive && !overCap) return;

            var compacted = new NativeList<float2>(arena.Length, allocator);
            liveWaypoints = 0;
            for (int id = 0; id < capacity; id++)
            {
                if (count[id] <= 0) continue;
                int first = compacted.Length;
                for (int i = 0; i < count[id]; i++) compacted.Add(arena[start[id] + i]);
                start[id] = first;
                liveWaypoints += count[id];
            }
            arena.Dispose();
            arena = compacted;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(RouteStore));
        }
    }

    /// <summary>Inputs for <see cref="RouteMaintenance"/>.</summary>
    public struct RouteConfig
    {
        /// <summary>Waypoints a traced route may have; a longer walk stops there.</summary>
        public int MaxWaypoints;

        /// <summary>Sample one traced route in this many for the smoothing experiment.</summary>
        public int SmoothSampleEvery;

        /// <summary>
        /// Routes re-traced per tick after a terrain change rebuilt a field. A terrain change dirties
        /// every full-map field, so a map's whole army is queued at once; the budget spreads the
        /// re-check out instead of stalling a tick on it.
        /// </summary>
        public int RetraceBudget;

        /// <summary>Route defaults: 512 waypoints, smoothing sampled from every 16th trace, 4,000 re-traces a tick.</summary>
        public static RouteConfig Defaults => new RouteConfig
        {
            MaxWaypoints = 512,
            SmoothSampleEvery = 16,
            RetraceBudget = 4000,
        };
    }

    /// <summary>
    /// The server's path state: every live unit's route is re-traced when it takes an order the route
    /// does not cover, or when a terrain change rebuilt the field the route came from. It runs before
    /// the encoder each tick, over the whole world - path computation is server-side work, not
    /// interest-managed traffic, and the encoder only ever reads the routes of units its client is
    /// allowed to know.
    /// </summary>
    public sealed class RouteMaintenance : IDisposable
    {
        private readonly RouteConfig config;
        private readonly NativeArray<byte> tiles;
        private readonly int width, height;
        private readonly Allocator allocator;
        private readonly NativeList<int2> scratch;
        private readonly NativeList<int2> smoothed;
        private NativeArray<int> orderOfId;
        private NativeArray<int> generationOfId;

        private SpikeWorldData world;
        private RouteStore store;

        /// <summary>Routes traced since the last reset (metrics only).</summary>
        public long Traces { get; private set; }

        /// <summary>Traces that produced a different route; only these send a MoveOrder.</summary>
        public long Changes { get; private set; }

        /// <summary>Traces a unit's new order caused (served at once, never deferred).</summary>
        public long OrderTraces { get; private set; }

        /// <summary>Traces a rebuilt field's generation caused (budgeted).</summary>
        public long StaleTraces { get; private set; }

        /// <summary>Stale routes the last pass left for the next tick, because the budget ran out.</summary>
        public long Deferred { get; private set; }

        /// <summary>Waypoints of a sampled trace, before smoothing.</summary>
        public long SampleRaw { get; private set; }

        /// <summary>Waypoints of a sampled trace, after the greedy line-of-sight pass.</summary>
        public long SampleSmoothed { get; private set; }

        /// <summary>Samples the two waypoint counters above were taken over.</summary>
        public long Samples { get; private set; }

        /// <summary>Main-thread milliseconds the last <see cref="Maintain"/> took.</summary>
        public double LastMilliseconds { get; private set; }

        /// <summary>Generations per order slot; the bench bumps one when a rebuilt field was copied in.</summary>
        private int[] orderGeneration;

        /// <summary>Prepares the tracer over a map's tile grid.</summary>
        public RouteMaintenance(in RouteConfig config, SpikeMap map, Allocator allocator)
        {
            this.config = config;
            this.allocator = allocator;
            tiles = map.Tiles;
            width = map.Width;
            height = map.Height;
            scratch = new NativeList<int2>(config.MaxWaypoints, allocator);
            smoothed = new NativeList<int2>(config.MaxWaypoints, allocator);
            orderGeneration = Array.Empty<int>();
        }

        /// <summary>Binds the simulation's SoA and order slots this maintenance follows.</summary>
        public void SetWorld(in SpikeWorldData world)
        {
            this.world = world;
            if (orderOfId.Length != world.IdCapacity)
            {
                if (orderOfId.IsCreated) orderOfId.Dispose();
                if (generationOfId.IsCreated) generationOfId.Dispose();
                orderOfId = new NativeArray<int>(math.max(1, world.IdCapacity), allocator);
                generationOfId = new NativeArray<int>(math.max(1, world.IdCapacity), allocator);
                for (int i = 0; i < orderOfId.Length; i++)
                {
                    orderOfId[i] = -1;
                    generationOfId[i] = -1;
                }
            }
            if (orderGeneration.Length < world.OrderCapacity) orderGeneration = new int[world.OrderCapacity];
        }

        /// <summary>Binds the store the traced routes are written to.</summary>
        public void SetStore(RouteStore store) => this.store = store;

        /// <summary>
        /// Marks an order slot's field as rebuilt: every route traced from it is re-traced on the next
        /// <see cref="Maintain"/>, and only the ones that really changed send anything. The replication
        /// benches do not call this (see <see cref="ReplicationMatch.ApplyTerrain"/> for why); it is the
        /// hook a server that re-bases routes after a terrain change would use.
        /// </summary>
        public void InvalidateOrder(int orderSlot)
        {
            if ((uint)orderSlot < (uint)orderGeneration.Length) orderGeneration[orderSlot]++;
        }

        /// <summary>
        /// One maintenance pass: every live unit whose order or whose order's field generation changed
        /// is re-traced. A unit that has no route at all yet (a fresh spawn, or a unit that has never
        /// been ordered) gets a one-waypoint route at its tile, so its Enter always carries something
        /// the client can predict from.
        /// </summary>
        public void Maintain()
        {
            if (store == null) throw new InvalidOperationException("RouteMaintenance has no store");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int capacity = world.Capacity;
            int retraced = 0;
            Deferred = 0;
            int idCapacity = orderOfId.Length;
            for (int i = 0; i < capacity; i++)
            {
                if (world.Health[i] <= 0f) continue;
                int id = world.IdOf[i];
                if ((uint)id >= (uint)idCapacity) continue;

                int order = world.FieldGoal[i];
                bool traced = false;
                if ((uint)order < (uint)world.OrderCount)
                {
                    int slotGeneration = (uint)order < (uint)orderGeneration.Length ? orderGeneration[order] : 0;
                    bool orderChanged = orderOfId[id] != order;
                    if (orderChanged || generationOfId[id] != slotGeneration)
                    {
                        // A new order is served at once; a route the rebuilt field may have changed is
                        // re-checked within the pass's budget, and the rest wait for the next tick.
                        if (!orderChanged && retraced >= config.RetraceBudget)
                        {
                            Deferred++;
                            continue;
                        }
                        RouteTracer.Trace(world.Directions, world.OrderBase[order], world.Width, world.Height,
                            (int2)math.floor(world.Positions[i]), scratch, config.MaxWaypoints);
                        orderOfId[id] = order;
                        generationOfId[id] = slotGeneration;
                        traced = true;
                        retraced++;
                        if (orderChanged) OrderTraces++;
                        else StaleTraces++;
                    }
                }
                else if (store.Count(id) == 0)
                {
                    scratch.Clear();
                    scratch.Add((int2)math.floor(world.Positions[i]));
                    orderOfId[id] = -1;
                    traced = true;
                }

                if (!traced) continue;
                Traces++;
                if (store.Set(id, scratch))
                {
                    Changes++;
                    // The smoothing experiment rides on changed routes only, one in
                    // SmoothSampleEvery of them, so its line-of-sight pass stays cheap.
                    if (config.SmoothSampleEvery > 0 && Changes % config.SmoothSampleEvery == 0)
                        SampleSmoothing(scratch);
                }
            }
            LastMilliseconds = clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// The waypoint-count experiment: smooth the traced route with a greedy line-of-sight pass and
        /// record both counts for every <see cref="RouteConfig.SmoothSampleEvery"/>-th change. The
        /// encoder itself sends the traced route - the taut one is measured, not used, so the
        /// correction rate stays attributable to the route the client actually has.
        /// </summary>
        public void SampleSmoothing(NativeList<int2> traced)
        {
            Samples++;
            SampleRaw += traced.Length;
            SampleSmoothed += RouteTracer.Smooth(traced.AsArray(), 0, traced.Length, tiles, width, height, smoothed);
        }

        /// <summary>Disposes the tracer's scratch lists. Safe to call twice.</summary>
        public void Dispose()
        {
            scratch.Dispose();
            smoothed.Dispose();
            if (orderOfId.IsCreated) orderOfId.Dispose();
            if (generationOfId.IsCreated) generationOfId.Dispose();
        }
    }
}
