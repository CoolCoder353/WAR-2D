using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>sim</c> bench: one 20 Hz simulation tick at the plan's scale. Each run builds the
    /// scripted battle (scenario), prebuilds one flow field per (goal, size class) the order stream
    /// uses, and then ticks the <see cref="SpikeTickGroup"/> for a warm-up plus the sampled window.
    ///
    /// <para><b>What the measured section contains:</b> the whole group update - gather, the spatial
    /// hash, the target search, combat (including the damage queue), movement (flow sampling,
    /// separation, integration and the tile slide) and the lifecycle stage (destroy/spawn recording and
    /// playback) - plus, in sync mode, the completion of the tick's jobs. It excludes the flow-field
    /// build (prebuilt before the loop), order intake (between ticks, on a completed world), terrain
    /// changes (the sim row does not apply them: their only effect on the tick is through flow fields,
    /// which the flow row measures) and the quality sampling, which runs after the tick has been timed.</para>
    ///
    /// <para><b>Metrics:</b> <c>sim.tick.wall</c> (sync, ms), <c>sim.tick.main</c> (async main-thread
    /// ms, with <c>sim.tick.main.wait</c> and <c>sim.tick.main.schedule</c> splitting it),
    /// <c>sim.sys.*</c> (per-stage share of the sync tick), <c>sim.overlap</c>, <c>sim.stuck</c>,
    /// <c>sim.engaged</c>, <c>sim.units</c>. The variant suffixes are <c>.clump</c> and
    /// <c>.fronts</c>.</para>
    /// </summary>
    public static class SimBench
    {
        /// <summary>Ticks between quality samples. The plan asks for a sample every 20 ticks.</summary>
        private const int SampleInterval = 20;

        /// <summary>Movement under this many tiles in two seconds counts as stuck.</summary>
        private const float StuckDistance = 0.5f;

        /// <summary>Ticks a unit is ordered for before the order stream re-orders it: every 2 s.</summary>
        private const int OrderIntervalTicks = 40;

        /// <summary>Extra unit slots above the starting army, for the churn's transient overshoot.</summary>
        private const int CapacitySlack = 2048;

        /// <summary>
        /// Overlap is measured over *touching* pairs (distance below r_i + r_j, the pairs separation
        /// works on): a pair "overlaps" when it penetrates more than 10 % of r_i + r_j, so
        /// <c>sim.overlap</c> is the conditional share of touching pairs that are too deep, not a share
        /// of the population.
        /// </summary>
        private const float SeparationThreshold = 0.9f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["sim"] = Run;

        public static IEnumerator Run(SpikeArgs a)
        {
            SpikeMap map = MapGenerator.Generate(a.MapSize, (uint)a.Seed, Allocator.Persistent);
            try
            {
                int players = math.min(a.Teams, MapGenerator.HqCount);
                var build = SpikeScenarioConfig.Defaults;
                build.Players = players;
                build.UnitsPerPlayer = math.max(1, a.Units / players);
                build.LargePercent = a.LargePercent;
                build.Placement = a.Clump ? SpikePlacement.Centre : a.Fronts ? SpikePlacement.Fronts : SpikePlacement.Hqs;
                build.Clump = a.Clump;
                build.OrderIntervalTicks = OrderIntervalTicks;
                build.ScriptTicks = 0; // the bench decides how long the match runs
                using var scenario = SpikeScenario.Create(build, map, Allocator.Persistent);

                using var cache = new FlowFieldCache(map, Allocator.Persistent);
                var config = SpikeSimConfig.Defaults;
                config.Players = players;
                config.Capacity = scenario.UnitCount + CapacitySlack;
                config.IdCapacity = config.Capacity + SpikeSimRules.SpawnPerTick * players * (a.Ticks + a.Warmup) + 64;
                config.Slice = a.SliceTicks;
                config.CellSize = a.CellSize;
                config.SeparationStrength = math.clamp(a.SepStrength, 1, 4);
                config.SeparationIterations = math.clamp(a.SepIterations, 1, 2);
                config.SeparationInterval = math.max(1, a.SepInterval);
                config.MoveHalves = a.MoveHalves;
                config.Async = a.Async;
                config.SpawnPerTick = SpikeSimRules.SpawnPerTick;
                config.SpawnTarget = scenario.UnitsPerPlayer; // each army is kept at its starting size
                config.SpawnOrigins = scenario.HqSites;
                using var sim = SpikeSim.Create(config, map, Allocator.Persistent, cache);
                sim.AddScenario(scenario);
                SetupOrders(sim, cache, scenario, a.LargePercent);

                Debug.Log($"[Sim] {scenario.UnitCount} units, {players} armies, {scenario.Placement} placement, " +
                          $"large {a.LargePercent}%, map {map.Width}^2, slice {config.Slice}, cell {config.CellSize}, " +
                          $"k {config.SeparationStrength} x{config.SeparationIterations}, sep every {config.SeparationInterval}, " +
                          $"async {config.Async}, halves {config.MoveHalves}");
                yield return null;

                // Warm-up: pays for Burst and lets the battle reach its steady state before sampling.
                for (int tick = 0; tick < a.Warmup; tick++)
                {
                    Intake(sim, scenario, tick);
                    sim.Tick();
                }
                yield return null;

                Measure(a, sim, scenario);
            }
            finally
            {
                map.Dispose();
            }
        }

        /// <summary>
        /// Prebuilds the fields the order stream uses: the scenario's goals at tick 0 (each player's
        /// opposing HQ, or the map centre when clumping), one per size class that has units. This is
        /// the "prebuild a field per order before the tick loop" the plan's ruling allows, so the
        /// measured tick never builds a field.
        /// </summary>
        private static void SetupOrders(SpikeSim sim, FlowFieldCache cache, in SpikeScenario scenario, int largePercent)
        {
            var slotByGoal = new Dictionary<long, int>();
            foreach ((int player, int2 goal, float _) in scenario.OrdersAt(0))
            {
                int classes = largePercent > 0 ? 2 : 1;
                for (int sizeClass = 0; sizeClass < classes; sizeClass++)
                {
                    long key = ((long)goal.y * 100000 + goal.x) * 2 + sizeClass;
                    if (!slotByGoal.TryGetValue(key, out int slot))
                    {
                        slot = sim.AddField(cache, goal, sizeClass);
                        slotByGoal[key] = slot;
                    }
                    sim.SetPlayerSlot(player, sizeClass, slot);
                }
            }
        }

        /// <summary>The scenario's order pulse at a tick, applied between ticks on a completed world.</summary>
        private static void Intake(SpikeSim sim, in SpikeScenario scenario, int tick)
        {
            foreach ((int _, int2 _, float fraction) in scenario.OrdersAt(tick))
            {
                sim.ApplyOrders(tick / math.max(1, scenario.OrderIntervalTicks), (int)math.round(1f / fraction));
                return;
            }
        }

        private static void Measure(SpikeArgs a, SpikeSim sim, in SpikeScenario scenario)
        {
            string suffix = a.Clump ? ".clump" : a.Fronts ? ".fronts" : "";
            var tickStats = new SpikeStats();
            var waitStats = new SpikeStats();
            var scheduleStats = new SpikeStats();
            var stageStats = new SpikeStats[SpikeSim.StageNames.Length];
            for (int i = 0; i < stageStats.Length; i++) stageStats[i] = new SpikeStats();
            var overlapStats = new SpikeStats();
            var stuckStats = new SpikeStats();
            var engagedStats = new SpikeStats();
            var unitStats = new SpikeStats();

            sim.SetBreakdown(!a.Async); // the stage breakdown completes inside the tick, so it is sync-only
            SpikeWorldData world = sim.Data;
            var history = new StuckHistory(world.IdCapacity, Allocator.Persistent);
            var previous = new Sample(world, history, sim);
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < a.Ticks; sample++)
            {
                int tick = a.Warmup + sample;
                Intake(sim, scenario, tick);

                sw.Restart();
                sim.Tick();
                sw.Stop();
                double wall = sw.Elapsed.TotalMilliseconds;
                tickStats.Add(wall);
                if (a.Async)
                {
                    waitStats.Add(sim.LastBoundaryMilliseconds);
                    scheduleStats.Add(wall - sim.LastBoundaryMilliseconds);
                }
                else
                {
                    for (int i = 0; i < stageStats.Length; i++) stageStats[i].Add(sim.Group.StageMilliseconds[i]);
                }

                if (sample % SampleInterval != 0) continue;
                previous.Capture(); // completes the tick, which is why the sample is not timed with it
                unitStats.Add(previous.LiveUnits);
                engagedStats.Add(previous.Engaged);
                overlapStats.Add(previous.Overlap);
                stuckStats.Add(history.Stuck());
            }

            SpikeResults.Write(a, "sim.tick.wall" + suffix, "ms", tickStats);
            if (a.Async)
            {
                SpikeResults.Write(a, "sim.tick.main" + suffix, "ms", tickStats);
                SpikeResults.Write(a, "sim.tick.main.wait" + suffix, "ms", waitStats);
                SpikeResults.Write(a, "sim.tick.main.schedule" + suffix, "ms", scheduleStats);
            }
            else
            {
                for (int i = 0; i < stageStats.Length; i++)
                    SpikeResults.Write(a, "sim.sys." + SpikeSim.StageNames[i], "ms", stageStats[i]);
            }
            SpikeResults.Write(a, "sim.units" + suffix, "units", unitStats);
            SpikeResults.Write(a, "sim.engaged" + suffix, "share", engagedStats);
            SpikeResults.Write(a, "sim.overlap" + suffix, "share", overlapStats);
            SpikeResults.Write(a, "sim.stuck" + suffix, "share", stuckStats);
            Debug.Log($"[Sim] live units mean {unitStats.Mean:F0}, engaged {engagedStats.Mean:P1}, " +
                      $"overlap {overlapStats.Mean:P2}, stuck {stuckStats.Mean:P2}");
            history.Dispose();
        }

        /// <summary>
        /// One quality sample of the completed tick: the SoA is current, so the counts are direct.
        /// </summary>
        private struct Sample
        {
            private readonly NativeArray<float2> positions;
            private readonly NativeArray<float> health, radius;
            private readonly NativeArray<int> fieldGoal, target, cellStart, sorted, idOf;
            private readonly int capacity, cellsX, cellsY;
            private readonly StuckHistory history;

            public int LiveUnits;
            public float Engaged, Overlap;

            public Sample(in SpikeWorldData world, StuckHistory history, SpikeSim sim)
            {
                positions = world.Positions;
                health = world.Health;
                radius = world.Radius;
                fieldGoal = world.FieldGoal;
                target = world.Target;
                cellStart = world.CellStart;
                sorted = world.Sorted;
                idOf = world.IdOf;
                capacity = world.Capacity;
                cellsX = world.CellsX;
                cellsY = world.CellsY;
                this.history = history;
                LiveUnits = 0;
                Engaged = 0;
                Overlap = 0;
            }

            /// <summary>Completes the tick and takes the sample.</summary>
            public void Capture()
            {
                int live = 0, engaged = 0, ordered = 0;
                for (int i = 0; i < capacity; i++)
                {
                    if (health[i] <= 0f) continue;
                    live++;
                    if (target[i] >= 0) engaged++;
                    if (fieldGoal[i] >= 0) ordered++;
                }
                LiveUnits = live;
                Engaged = live == 0 ? 0f : (float)engaged / live;

                int touching = 0, overlapped = 0;
                for (int c = 0; c < cellsX * cellsY; c++)
                {
                    int cx = c % cellsX, cy = c / cellsX;
                    for (int k = cellStart[c]; k < cellStart[c + 1]; k++)
                    {
                        int i = sorted[k];
                        if (health[i] <= 0f) continue;
                        for (int k2 = k + 1; k2 < cellStart[c + 1]; k2++)
                            Count(ref touching, ref overlapped, i, sorted[k2]);
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            // The four cells above and right of this one: pairs across cells are
                            // counted once, by the cell that comes first.
                            if (dx == 0 && dy == 0) continue;
                            if (dy < 0 || (dy == 0 && dx < 0)) continue;
                            int nx = cx + dx, ny = cy + dy;
                            if ((uint)nx >= (uint)cellsX || (uint)ny >= (uint)cellsY) continue;
                            int neighbour = ny * cellsX + nx;
                            for (int k2 = cellStart[neighbour]; k2 < cellStart[neighbour + 1]; k2++)
                                Count(ref touching, ref overlapped, i, sorted[k2]);
                        }
                    }
                }
                Overlap = touching == 0 ? 0f : (float)overlapped / touching;

                history.Add(positions, fieldGoal, health, idOf, capacity);
            }

            private readonly void Count(ref int touching, ref int overlapped, int i, int j)
            {
                if (j == i || health[j] <= 0f) return;
                float sum = radius[i] + radius[j];
                float distance = math.distance(positions[i], positions[j]);
                if (distance >= sum) return;
                touching++;
                if (distance < sum * SeparationThreshold) overlapped++;
            }
        }

        /// <summary>
        /// The two-second position history the stuck metric needs: each unit's position and whether it
        /// had an order, one sample back and two samples back, keyed by id. Each id also carries the
        /// sample it was last seen live in, so a unit that dies leaves the metric instead of sitting
        /// there frozen - otherwise every dead unit with an order would count as stuck forever.
        /// </summary>
        private sealed class StuckHistory
        {
            private NativeArray<float2> previous, older;
            private NativeArray<byte> previousOrdered, olderOrdered;
            private NativeArray<int> seenPrevious, seenOlder;
            private int sample;

            public StuckHistory(int idCapacity, Allocator allocator)
            {
                previous = new NativeArray<float2>(idCapacity, allocator);
                older = new NativeArray<float2>(idCapacity, allocator);
                previousOrdered = new NativeArray<byte>(idCapacity, allocator);
                olderOrdered = new NativeArray<byte>(idCapacity, allocator);
                seenPrevious = new NativeArray<int>(idCapacity, allocator);
                seenOlder = new NativeArray<int>(idCapacity, allocator);
            }

            public void Add(NativeArray<float2> positions, NativeArray<int> fieldGoal, NativeArray<float> health,
                NativeArray<int> idOf, int capacity)
            {
                sample++;
                for (int i = 0; i < capacity; i++)
                {
                    if (health[i] <= 0f) continue;
                    int id = idOf[i];
                    if ((uint)id >= (uint)previous.Length) continue;
                    if (sample > 1)
                    {
                        older[id] = previous[id];
                        olderOrdered[id] = previousOrdered[id];
                        seenOlder[id] = seenPrevious[id];
                    }
                    previous[id] = positions[i];
                    previousOrdered[id] = fieldGoal[i] >= 0 ? (byte)1 : (byte)0;
                    seenPrevious[id] = sample;
                }
            }

            /// <summary>
            /// The share of the units that were alive and ordered at both ends of the two-second window
            /// that moved under 0.5 tiles. Dead units, units that spawned inside the window and units
            /// that lost their order are not part of the denominator or the numerator.
            /// </summary>
            public float Stuck()
            {
                if (sample < 2) return 0f;
                int ordered = 0, stuck = 0;
                for (int id = 0; id < olderOrdered.Length; id++)
                {
                    if (seenPrevious[id] != sample || seenOlder[id] != sample - 1) continue; // live at both ends
                    if (olderOrdered[id] == 0 || previousOrdered[id] == 0) continue;
                    ordered++;
                    if (math.distance(older[id], previous[id]) < StuckDistance) stuck++;
                }
                return ordered == 0 ? 0f : (float)stuck / ordered;
            }

            public void Dispose()
            {
                previous.Dispose();
                older.Dispose();
                previousOrdered.Dispose();
                olderOrdered.Dispose();
                seenPrevious.Dispose();
                seenOlder.Dispose();
            }
        }
    }
}
