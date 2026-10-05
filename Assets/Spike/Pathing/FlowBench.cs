using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>flow</c> bench. Measures a single field's build (integration plus direction), eight
    /// builds scheduled together, the clearance pass, and the reference load: 16 orders/s,
    /// 4 terrain changes/s, 64 live orders (plus the large-class twins of orders that include large
    /// units) and at most 2 rebuilds a tick.
    ///
    /// <para>All timing is schedule-to-<c>Complete()</c> wall time on the main thread, as the spike's
    /// tick measurements are. The refload loop runs as fast as it can rather than on the 20 Hz clock:
    /// the clock only adds idle time between ticks, and the per-tick cost it would report is the same
    /// block of work measured here.</para>
    /// </summary>
    public static class FlowBench
    {
        /// <summary>Single-field samples, and the warm-up that pays for Burst compilation.</summary>
        private const int BuildSamples = 600, BuildWarmup = 100;

        /// <summary>Eight fields scheduled together: the per-tick wall time when 8 orders land at once.</summary>
        private const int ParallelFields = 8, ParallelSamples = 100, ParallelWarmup = 20;

        /// <summary>Clearance samples: the whole map, and the window a single terrain change needs.</summary>
        private const int FullClearanceSamples = 100, FullClearanceWarmup = 10;
        private const int WindowClearanceSamples = 600, WindowClearanceWarmup = 100;

        /// <summary>Orders per second: 8 players ordering every 10 ticks at 20 Hz.</summary>
        private const int ReferenceOrderIntervalTicks = 10;

        /// <summary>
        /// Ticks an order's units follow their field. 16 orders/s and a 4 s follow give the reference
        /// load's 64 live fields; a field is released when its last follower's window ends.
        /// </summary>
        private const int ReferenceFollowTicks = 80;

        /// <summary>The plan's fallback 1: at most two field rebuilds a tick.</summary>
        private const int ReferenceMaxRebuildsPerTick = 2;

        /// <summary>Radius of the random spread of each order's clicked goal, so orders rarely share a field.</summary>
        private const int GoalJitterRadius = 8;

        /// <summary>The reference load is measured at the plan's two map sizes; 256 is a build-size point.</summary>
        private const int ReferenceMinMapSize = 512;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["flow"] = Run;

        /// <summary>
        /// One live order's fields, the number of followers each was acquired with, and when the bench
        /// can stop following them. The followers are the units that will follow the field: the
        /// refcount has to drop back to zero for the field to be freed.
        /// </summary>
        private sealed class LiveOrder
        {
            public int Small = -1, Large = -1;
            public int SmallFollowers, LargeFollowers;
            public int AcquireTick, ReleaseTick;
            public int ReadyTick = -1;
        }

        public static IEnumerator Run(SpikeArgs a)
        {
            SpikeMap map = MapGenerator.Generate(a.MapSize, (uint)a.Seed, Allocator.Persistent);
            try
            {
                yield return null;
                MeasureBuild(a, map);
                yield return null;
                MeasureParallelBuilds(a, map);
                yield return null;
                MeasureClearance(a, map);
                yield return null;
                if (a.MapSize >= ReferenceMinMapSize) MeasureReferenceLoad(a, map);
            }
            finally
            {
                map.Dispose();
            }
        }

        /// <summary>One integration plus direction build, on fields with random floor goals.</summary>
        private static void MeasureBuild(SpikeArgs a, in SpikeMap map)
        {
            int cells = map.Width * map.Height;
            var rng = new Random((uint)a.Seed + 11u);
            var cost = new NativeArray<ushort>(cells, Allocator.Persistent);
            var direction = new NativeArray<byte>(cells, Allocator.Persistent);
            var goals = new NativeArray<int>(1, Allocator.Persistent);
            var stats = new SpikeStats();
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < BuildSamples + BuildWarmup; sample++)
            {
                goals[0] = RandomFloor(map, ref rng);
                sw.Restart();
                JobHandle handle = new BuildIntegrationFieldJob
                {
                    Width = map.Width, Height = map.Height, Tiles = map.Tiles, Goals = goals, Cost = cost,
                }.Schedule();
                handle = new BuildDirectionFieldJob
                {
                    Width = map.Width, Height = map.Height, Tiles = map.Tiles, Cost = cost, Direction = direction,
                }.Schedule(cells, 64, handle);
                handle.Complete();
                sw.Stop();
                if (sample >= BuildWarmup) stats.Add(sw.Elapsed.TotalMilliseconds);
            }

            // The ushort saturation check: floor cells left at Unreachable next to reachable ones.
            CountSaturation(map, cost, out int stuck, out int maxCost);
            SpikeResults.Write(a, "flow.build", "ms", stats);
            SpikeResults.Write(a, "flow.saturated", "cells", Single(stuck));
            SpikeResults.Write(a, "flow.build.maxcost", "cost", Single(maxCost));
            Debug.Log($"[Flow] build {map.Width}^2: {BuildSamples} samples, max cost {maxCost} of ushort {ushort.MaxValue}, " +
                      $"{stuck} floor cells stuck at Unreachable next to a reachable cell");

            cost.Dispose();
            direction.Dispose();
            goals.Dispose();
        }

        /// <summary>Eight fields scheduled together, one per worker: the wall time of a burst of orders.</summary>
        private static void MeasureParallelBuilds(SpikeArgs a, in SpikeMap map)
        {
            int cells = map.Width * map.Height;
            var rng = new Random((uint)a.Seed + 22u);
            var cost = new NativeArray<ushort>[ParallelFields];
            var direction = new NativeArray<byte>[ParallelFields];
            var goals = new NativeArray<int>[ParallelFields];
            for (int field = 0; field < ParallelFields; field++)
            {
                cost[field] = new NativeArray<ushort>(cells, Allocator.Persistent);
                direction[field] = new NativeArray<byte>(cells, Allocator.Persistent);
                goals[field] = new NativeArray<int>(1, Allocator.Persistent);
            }
            var integration = new NativeArray<JobHandle>(ParallelFields, Allocator.Persistent);
            var chained = new NativeArray<JobHandle>(ParallelFields, Allocator.Persistent);
            var stats = new SpikeStats();
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < ParallelSamples + ParallelWarmup; sample++)
            {
                for (int field = 0; field < ParallelFields; field++) goals[field][0] = RandomFloor(map, ref rng);
                sw.Restart();
                for (int field = 0; field < ParallelFields; field++)
                {
                    integration[field] = new BuildIntegrationFieldJob
                    {
                        Width = map.Width, Height = map.Height, Tiles = map.Tiles, Goals = goals[field], Cost = cost[field],
                    }.Schedule();
                    chained[field] = new BuildDirectionFieldJob
                    {
                        Width = map.Width, Height = map.Height, Tiles = map.Tiles, Cost = cost[field], Direction = direction[field],
                    }.Schedule(cells, 64, integration[field]);
                }
                JobHandle.CombineDependencies(chained).Complete();
                sw.Stop();
                if (sample >= ParallelWarmup) stats.Add(sw.Elapsed.TotalMilliseconds);
            }

            SpikeResults.Write(a, "flow.parallel8", "ms", stats);
            foreach (NativeArray<ushort> field in cost) field.Dispose();
            foreach (NativeArray<byte> field in direction) field.Dispose();
            foreach (NativeArray<int> field in goals) field.Dispose();
            integration.Dispose();
            chained.Dispose();
        }

        /// <summary>The clearance grid: a whole-map pass, and the 2 x MaxClearance window a terrain change needs.</summary>
        private static void MeasureClearance(SpikeArgs a, in SpikeMap map)
        {
            int cells = map.Width * map.Height;
            var rng = new Random((uint)a.Seed + 33u);
            var clearance = new NativeArray<byte>(cells, Allocator.Persistent);
            var filtered = new NativeArray<byte>(cells, Allocator.Persistent);
            var full = new SpikeStats();
            var window = new SpikeStats();
            var sw = Stopwatch.StartNew();
            int half = ClearanceJob.MaxClearance;

            for (int sample = 0; sample < FullClearanceSamples + FullClearanceWarmup; sample++)
            {
                sw.Restart();
                new ClearanceJob { Width = map.Width, Height = map.Height, Tiles = map.Tiles, Clearance = clearance }
                    .Run();
                new FilterTilesJob
                {
                    Width = map.Width, Height = map.Height, Tiles = map.Tiles, Clearance = clearance,
                    MinClearance = FlowSizeClass.MinClearance(FlowSizeClass.Large), Filtered = filtered,
                }.Run(cells);
                sw.Stop();
                if (sample >= FullClearanceWarmup) full.Add(sw.Elapsed.TotalMilliseconds);
            }

            for (int sample = 0; sample < WindowClearanceSamples + WindowClearanceWarmup; sample++)
            {
                int2 changed = new int2(rng.NextInt(1, map.Width - 1), rng.NextInt(1, map.Height - 1));
                int2 low = math.max(changed - half, new int2(1, 1));
                int2 high = math.min(changed + half, new int2(map.Width - 2, map.Height - 2));
                int2 origin = low, size = high - low + 1;

                sw.Restart();
                new ClearanceJob
                {
                    Width = map.Width, Height = map.Height, Tiles = map.Tiles,
                    WindowOrigin = origin, WindowSize = size, Clearance = clearance,
                }.Run();
                new FilterTilesJob
                {
                    Width = map.Width, Height = map.Height, Origin = origin, Size = size, Tiles = map.Tiles,
                    Clearance = clearance, MinClearance = FlowSizeClass.MinClearance(FlowSizeClass.Large),
                    Filtered = filtered,
                }.Run(size.x * size.y);
                sw.Stop();
                if (sample >= WindowClearanceWarmup) window.Add(sw.Elapsed.TotalMilliseconds);
            }

            SpikeResults.Write(a, "flow.clearance.full", "ms", full);
            SpikeResults.Write(a, "flow.clearance.window", "ms", window);
            clearance.Dispose();
            filtered.Dispose();
        }

        /// <summary>
        /// The reference load: the scenario's 16 orders/s and 4 terrain changes/s, 64 live orders plus
        /// the large-class twin of every order that includes large units, and RebuildDirty(2).
        /// </summary>
        private static void MeasureReferenceLoad(SpikeArgs a, SpikeMap map)
        {
            int players = math.min(a.Teams, MapGenerator.HqCount);
            var config = SpikeScenarioConfig.Defaults;
            config.Players = players;
            config.UnitsPerPlayer = math.max(1, a.Units / players);
            config.LargePercent = a.LargePercent;
            config.Placement = SpikePlacement.Hqs;
            config.Clump = false;
            config.OrderIntervalTicks = ReferenceOrderIntervalTicks;
            config.ScriptTicks = 0; // unlimited: the bench decides how long the match is

            using var scenario = SpikeScenario.Create(config, map, Allocator.Persistent);
            using var cache = new FlowFieldCache(map, Allocator.Persistent);
            var rng = new Random((uint)a.Seed + 44u);
            var terrain = new NativeList<int2>(8, Allocator.Persistent);
            var orders = new List<LiveOrder>();

            var tickStats = new SpikeStats();
            var latencyStats = new SpikeStats();
            var backlogStats = new SpikeStats();
            var fieldStats = new SpikeStats();
            var smallStats = new SpikeStats();
            var largeStats = new SpikeStats();
            var memStats = new SpikeStats();
            var poolStats = new SpikeStats();

            int ticks = math.max(1, a.Ticks);
            int warmup = math.min(a.Warmup, ticks - 1);
            int totalOrders = 0, totalTerrain = 0, neverReady = 0;
            var sw = Stopwatch.StartNew();

            for (int tick = 0; tick < ticks; tick++)
            {
                sw.Restart();

                // Terrain: the script names the tiles, the bench flips them and invalidates the cache.
                terrain.Clear();
                scenario.TerrainChangesAt(tick, terrain);
                for (int i = 0; i < terrain.Length; i++)
                {
                    int2 tile = terrain[i];
                    int cell = tile.y * map.Width + tile.x;
                    map.Tiles[cell] = map.Tiles[cell] == SpikeMap.Floor ? SpikeMap.Rock : SpikeMap.Floor;
                    cache.Invalidate(tile);
                    totalTerrain++;
                }

                // Orders: every player orders a quarter of its army at a fresh goal near its target.
                foreach ((int player, int2 goal, float fraction) in scenario.OrdersAt(tick))
                {
                    int units = (int)math.round(fraction * scenario.UnitsPerPlayer);
                    int2 click = RandomFloorNear(map, ref rng, goal, GoalJitterRadius);
                    int large = LargeUnitsIn(scenario, player, units);
                    var order = new LiveOrder
                    {
                        SmallFollowers = units - large,
                        AcquireTick = tick,
                        ReleaseTick = tick + ReferenceFollowTicks,
                    };
                    order.Small = cache.Acquire(click, FlowSizeClass.Small, order.SmallFollowers);
                    if (large > 0)
                    {
                        order.LargeFollowers = large;
                        order.Large = cache.Acquire(click, FlowSizeClass.Large, large);
                    }
                    orders.Add(order);
                    totalOrders++;
                }

                // Units that have followed their field long enough release it.
                for (int i = orders.Count - 1; i >= 0; i--)
                {
                    if (tick < orders[i].ReleaseTick) continue;
                    LiveOrder order = orders[i];
                    if (order.ReadyTick < 0) neverReady++;
                    cache.Release(order.Small, order.SmallFollowers);
                    if (order.Large >= 0) cache.Release(order.Large, order.LargeFollowers);
                    orders.RemoveAt(i);
                }

                // The fallback's rebuild budget, scheduled in parallel and completed this tick.
                cache.RebuildDirty(ReferenceMaxRebuildsPerTick).Complete();
                cache.CompleteRebuilds();

                sw.Stop();

                // An order is served when both its fields are ready; latency is measured from Acquire.
                // Orders from the warm-up ticks are excluded: the cache is still filling to the
                // reference load's 64 live fields for the first 4 s.
                foreach (LiveOrder order in orders)
                {
                    if (order.ReadyTick >= 0) continue;
                    if (!cache.IsReady(order.Small)) continue;
                    if (order.Large >= 0 && !cache.IsReady(order.Large)) continue;
                    order.ReadyTick = tick;
                    if (order.AcquireTick >= warmup) latencyStats.Add((tick - order.AcquireTick) * 50.0);
                }

                if (tick < warmup) continue;
                tickStats.Add(sw.Elapsed.TotalMilliseconds);
                backlogStats.Add(cache.DirtyCount);
                fieldStats.Add(cache.LiveCount);
                smallStats.Add(cache.LiveCountOfClass(FlowSizeClass.Small));
                largeStats.Add(cache.LiveCountOfClass(FlowSizeClass.Large));
                memStats.Add(cache.LiveBytes / (1024.0 * 1024.0));
                poolStats.Add(cache.PooledBytes / (1024.0 * 1024.0));
            }

            foreach (LiveOrder order in orders) if (order.ReadyTick < 0) neverReady++;

            SpikeResults.Write(a, "flow.refload.tick", "ms", tickStats);
            SpikeResults.Write(a, "flow.refload.latency", "ms", latencyStats);
            SpikeResults.Write(a, "flow.refload.backlog", "fields", backlogStats);
            SpikeResults.Write(a, "flow.refload.fields", "fields", fieldStats);
            SpikeResults.Write(a, "flow.refload.small", "fields", smallStats);
            SpikeResults.Write(a, "flow.refload.large", "fields", largeStats);
            SpikeResults.Write(a, "flow.refload.mem", "MB", memStats);
            SpikeResults.Write(a, "flow.refload.pool", "MB", poolStats);
            SpikeResults.Write(a, "flow.refload.orders", "orders", Single(totalOrders));
            SpikeResults.Write(a, "flow.refload.terrain", "changes", Single(totalTerrain));
            SpikeResults.Write(a, "flow.refload.rebuilds", "fields", Single(cache.RebuildCount));
            SpikeResults.Write(a, "flow.refload.neverready", "orders", Single(neverReady));

            Debug.Log($"[Flow] refload {map.Width}^2: {ticks} ticks ({warmup} warm-up), {totalOrders} orders, " +
                      $"{totalTerrain} terrain changes, {cache.RebuildCount} field rebuilds, {neverReady} orders never ready, " +
                      $"ends with {cache.LiveCount} live fields ({cache.LiveBytes / (1024.0 * 1024.0):F1} MB) " +
                      $"and {cache.DirtyCount} dirty");
        }

        /// <summary>A single-sample stat row.</summary>
        private static SpikeStats Single(double value)
        {
            var stats = new SpikeStats();
            stats.Add(value);
            return stats;
        }

        /// <summary>A random floor cell index; the generated maps are at least a third floor.</summary>
        private static int RandomFloor(in SpikeMap map, ref Random rng)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int cell = rng.NextInt(map.Tiles.Length);
                if (map.Tiles[cell] == SpikeMap.Floor) return cell;
            }
            for (int cell = 0; cell < map.Tiles.Length; cell++)
                if (map.Tiles[cell] == SpikeMap.Floor) return cell;
            throw new System.InvalidOperationException("the map has no floor tile");
        }

        /// <summary>A random floor tile within <paramref name="radius"/> of the centre, so orders rarely share a field.</summary>
        private static int2 RandomFloorNear(in SpikeMap map, ref Random rng, int2 centre, int radius)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var tile = new int2(rng.NextInt(-radius, radius + 1), rng.NextInt(-radius, radius + 1)) + centre;
                if (!map.IsBlocked(tile)) return tile;
            }
            for (int ring = 0; ring <= radius * 2; ring++)
            {
                for (int i = 0; i < (ring == 0 ? 1 : 8 * ring); i++)
                {
                    int2 tile = RingTile(centre, ring, i);
                    if (!map.IsBlocked(tile)) return tile;
                }
            }
            return centre;
        }

        /// <summary>
        /// The large units in the first <paramref name="units"/> of the player's army. The scenario
        /// spreads promotions through an army, so any quarter of it has its share.
        /// </summary>
        private static int LargeUnitsIn(in SpikeScenario scenario, int player, int units)
        {
            int baseIndex = player * scenario.UnitsPerPlayer;
            int large = 0;
            for (int i = 0; i < units; i++)
                if (scenario.UnitSizeClass[baseIndex + i] == 1) large++;
            return large;
        }

        /// <summary>Counts floor cells left at Unreachable with a reachable neighbour, and the largest cost.</summary>
        private static void CountSaturation(in SpikeMap map, NativeArray<ushort> cost, out int stuck, out int maxCost)
        {
            stuck = 0;
            maxCost = 0;
            for (int y = 1; y < map.Height - 1; y++)
            for (int x = 1; x < map.Width - 1; x++)
            {
                int cell = y * map.Width + x;
                if (map.Tiles[cell] != SpikeMap.Floor) continue;
                if (cost[cell] == BuildIntegrationFieldJob.Unreachable)
                {
                    bool reachableNeighbour = false;
                    for (int dy = -1; dy <= 1 && !reachableNeighbour; dy++)
                    for (int dx = -1; dx <= 1 && !reachableNeighbour; dx++)
                        reachableNeighbour = cost[(y + dy) * map.Width + x + dx] != BuildIntegrationFieldJob.Unreachable;
                    if (reachableNeighbour) stuck++;
                    continue;
                }
                maxCost = math.max(maxCost, cost[cell]);
            }
        }

        /// <summary>The i-th tile of the ring at Chebyshev distance <paramref name="ring"/>, same walk as the cache's.</summary>
        private static int2 RingTile(int2 origin, int ring, int i)
        {
            if (ring == 0) return origin;
            int side = 2 * ring;
            if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
            i -= side;
            if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
            i -= side;
            if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
            i -= side;
            return new int2(origin.x - ring, origin.y + ring - i);
        }
    }
}
