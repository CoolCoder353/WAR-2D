using System.Collections;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>hash</c> bench. Measures the counting-sort spatial hash build, the time-sliced nearest
    /// enemy search (slices 1, 4 and 8), the clump stress (every unit inside 64 tiles of the map
    /// centre), and the NativeParallelMultiHashMap build the plan's fallback ladder would use.
    ///
    /// <para>The unit set is the battle layout at the peak of the fight:
    /// <see cref="SpikePlacement.Fronts"/> starts both armies of every pair interleaved at their
    /// shared front, so the armies are already engaged. All timing is schedule-to-<c>Complete()</c>
    /// wall time on the main thread, as the spike's tick measurements are. A search metric is the
    /// per-tick time: the job searches 1 / slice of the units each tick, so a full sweep costs the
    /// reported number times the slice.</para>
    /// </summary>
    public static class HashBench
    {
        /// <summary>Samples per metric, and the warm-up that pays for Burst compilation.</summary>
        private const int Samples = 600, Warmup = 100;

        /// <summary>Brute force is a reference point, not a swept metric, so it gets few samples.</summary>
        private const int BruteSamples = 3, BruteWarmup = 1;

        /// <summary>Units per job batch.</summary>
        private const int Batch = 256;

        /// <summary>The plan's attack range: 5 tiles.</summary>
        private const float AttackRange = 5f;

        /// <summary>Radius of the clump disc, in tiles.</summary>
        private const float ClumpRadius = 64f;

        /// <summary>The slice intervals the bench sweeps: every tick, every 4th, every 8th.</summary>
        private static readonly int[] Slices = { 1, 4, 8 };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["hash"] = Run;

        public static IEnumerator Run(SpikeArgs a)
        {
            SpikeMap map = MapGenerator.Generate(a.MapSize, (uint)a.Seed, Allocator.Persistent);
            try
            {
                int players = math.min(a.Teams, MapGenerator.HqCount);
                var config = SpikeScenarioConfig.Defaults;
                config.Players = players;
                config.UnitsPerPlayer = math.max(1, a.Units / players);
                config.LargePercent = a.LargePercent;
                config.Placement = SpikePlacement.Fronts; // the peak of the fight: each pair is already engaged
                config.Clump = false;
                config.OrderIntervalTicks = 0;
                config.ScriptTicks = 0;
                using var scenario = SpikeScenario.Create(config, map, Allocator.Persistent);

                int cellSize = math.max(1, a.CellSize);
                int cellsX = (map.Width + cellSize - 1) / cellSize;
                int cellsY = (map.Height + cellSize - 1) / cellSize;
                int count = scenario.UnitCount;
                using var rangeSq = Constant(count, AttackRange * AttackRange, Allocator.Persistent);
                using var cell = new NativeArray<int>(count, Allocator.Persistent);
                using var cellStart = new NativeArray<int>(cellsX * cellsY + 1, Allocator.Persistent);
                using var sorted = new NativeArray<int>(count, Allocator.Persistent);
                using var target = new NativeArray<int>(count, Allocator.Persistent);

                Debug.Log($"[Hash] {count} units, cell {cellSize} on a {cellsX}x{cellsY} grid, " +
                          $"fronts placement, map {map.Width}^2");
                yield return null;
                MeasureBuild(a, scenario, cell, cellStart, sorted, cellSize, cellsX, cellsY);
                yield return null;
                MeasureMultiMap(a, scenario, cell, cellSize, cellsX, cellsY);
                yield return null;
                MeasureSearch(a, scenario, rangeSq, cell, cellStart, sorted, target, cellSize, cellsX, cellsY);
                yield return null;
                MeasureClump(a, players, count, map.Width, cellSize, cellsX, cellsY);
                if (a.CellSize == SpikeArgs.Defaults.CellSize)
                {
                    yield return null;
                    MeasureBruteForce(a, scenario, rangeSq);
                }
            }
            finally
            {
                map.Dispose();
            }
        }

        /// <summary>A persistent array of <paramref name="count"/> copies of <paramref name="value"/>.</summary>
        private static NativeArray<float> Constant(int count, float value, Allocator allocator)
        {
            var array = new NativeArray<float>(count, allocator);
            for (int i = 0; i < count; i++) array[i] = value;
            return array;
        }

        /// <summary>Schedules the cell index and the counting sort; the returned handle covers both.</summary>
        private static JobHandle ScheduleHash(NativeArray<float2> positions, NativeArray<int> cell,
            NativeArray<int> cellStart, NativeArray<int> sorted, int cellSize, int cellsX, int cellsY)
        {
            JobHandle handle = new CellIndexJob
            {
                Positions = positions, Cell = cell,
                InvCellSize = 1f / cellSize, CellsX = cellsX, CellsY = cellsY,
            }.Schedule(positions.Length, Batch);
            return new CountingSortJob { Cell = cell, CellStart = cellStart, Sorted = sorted }.Schedule(handle);
        }

        /// <summary>The per-tick cost of rebuilding the hash from scratch.</summary>
        private static void MeasureBuild(SpikeArgs a, in SpikeScenario scenario, NativeArray<int> cell,
            NativeArray<int> cellStart, NativeArray<int> sorted, int cellSize, int cellsX, int cellsY)
        {
            NativeArray<float2> positions = scenario.UnitPositions;
            var stats = new SpikeStats();
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < Samples + Warmup; sample++)
            {
                sw.Restart();
                ScheduleHash(positions, cell, cellStart, sorted, cellSize, cellsX, cellsY).Complete();
                sw.Stop();
                if (sample >= Warmup) stats.Add(sw.Elapsed.TotalMilliseconds);
            }

            // Occupancy of the grid as last built, to explain the cell-size sweep.
            int cells = cellsX * cellsY, max = 0;
            for (int c = 0; c < cells; c++) max = math.max(max, cellStart[c + 1] - cellStart[c]);

            SpikeResults.Write(a, "hash.build", "ms", stats);
            Debug.Log($"[Hash] build: {cells} cells, mean occupancy {positions.Length / (double)cells:F2}, " +
                      $"max {max} units/cell");
        }

        /// <summary>
        /// The fallback ladder's alternative: the same cell index feeding a
        /// <see cref="NativeParallelMultiHashMap{TKey,TValue}"/> through its parallel writer instead of
        /// the counting sort. Cleared every tick, as the sim would, and the clear is timed with the build.
        /// </summary>
        private static void MeasureMultiMap(SpikeArgs a, in SpikeScenario scenario, NativeArray<int> cell,
            int cellSize, int cellsX, int cellsY)
        {
            int count = scenario.UnitCount;
            using var cells = new NativeParallelMultiHashMap<int, int>(count, Allocator.Persistent);
            var stats = new SpikeStats();
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < Samples + Warmup; sample++)
            {
                sw.Restart();
                cells.Clear(); // the counting sort's zeroing pass, so the two builds measure the same work
                JobHandle handle = new CellIndexJob
                {
                    Positions = scenario.UnitPositions, Cell = cell,
                    InvCellSize = 1f / cellSize, CellsX = cellsX, CellsY = cellsY,
                }.Schedule(count, Batch);
                handle = new MultiMapFillJob { Cell = cell, Cells = cells.AsParallelWriter() }.Schedule(count, Batch, handle);
                handle.Complete();
                sw.Stop();
                if (sample >= Warmup) stats.Add(sw.Elapsed.TotalMilliseconds);
            }

            SpikeResults.Write(a, "hash.multimap", "ms", stats);
            Debug.Log($"[Hash] multimap: {cells.Count()} of {count} entries after the last fill");
        }

        /// <summary>
        /// The per-tick nearest-enemy search at slices 1, 4 and 8. Slice s searches every s-th unit
        /// each tick, staggered by index, so s consecutive ticks cover every unit exactly once; the
        /// sample's tick cycles 0..s-1 and the metric is the per-tick time.
        /// </summary>
        private static void MeasureSearch(SpikeArgs a, in SpikeScenario scenario, NativeArray<float> rangeSq,
            NativeArray<int> cell, NativeArray<int> cellStart, NativeArray<int> sorted, NativeArray<int> target,
            int cellSize, int cellsX, int cellsY)
        {
            int count = scenario.UnitCount;
            int searchCells = SearchCells(cellSize);
            foreach (int slice in Slices)
            {
                var search = new NearestEnemyJob
                {
                    Positions = scenario.UnitPositions, Team = scenario.UnitOwners, Health = scenario.UnitHealth,
                    RangeSq = rangeSq, CellStart = cellStart, Sorted = sorted,
                    InvCellSize = 1f / cellSize, CellsX = cellsX, CellsY = cellsY, SearchCells = searchCells,
                    Slice = slice, Target = target,
                };
                var stats = new SpikeStats();
                var sw = Stopwatch.StartNew();

                for (int sample = 0; sample < Samples + Warmup; sample++)
                {
                    search.Tick = sample % slice;
                    ScheduleHash(scenario.UnitPositions, cell, cellStart, sorted, cellSize, cellsX, cellsY).Complete();
                    sw.Restart();
                    search.Schedule(count, Batch).Complete();
                    sw.Stop();
                    if (sample >= Warmup) stats.Add(sw.Elapsed.TotalMilliseconds);
                }

                SpikeResults.Write(a, $"hash.search.slice{slice}", "ms", stats);
                Debug.Log($"[Hash] search slice {slice}: {Samples} ticks, {count / slice} units searched a tick " +
                          $"(a full sweep is {slice} ticks)");
            }
        }

        /// <summary>
        /// The clump stress: every unit inside <see cref="ClumpRadius"/> tiles of the map centre, slice 4.
        /// The scenario's 2-units-per-tile packing cannot fit the reference 80k units in that disc
        /// (about 25k), so this packs them denser with a deterministic sunflower (Vogel) spiral, at the
        /// density the disc can hold (~6 units a tile at 80k). It is a hash stress, not a playable state.
        /// Teams are interleaved by index, so every unit has enemies beside it.
        /// </summary>
        private static void MeasureClump(SpikeArgs a, int players, int count, int mapSize, int cellSize,
            int cellsX, int cellsY)
        {
            const int slice = 4;
            var positions = new NativeArray<float2>(count, Allocator.Persistent);
            var team = new NativeArray<byte>(count, Allocator.Persistent);
            var health = Constant(count, 100f, Allocator.Persistent);
            var rangeSq = Constant(count, AttackRange * AttackRange, Allocator.Persistent);
            var cell = new NativeArray<int>(count, Allocator.Persistent);
            var cellStart = new NativeArray<int>(cellsX * cellsY + 1, Allocator.Persistent);
            var sorted = new NativeArray<int>(count, Allocator.Persistent);
            var target = new NativeArray<int>(count, Allocator.Persistent);
            try
            {
                float2 centre = new float2(mapSize * 0.5f);
                for (int i = 0; i < count; i++)
                {
                    float radius = ClumpRadius * math.sqrt((i + 0.5f) / count);
                    float angle = i * 2.3999632f; // the golden angle, so the spiral is even
                    positions[i] = centre + radius * new float2(math.cos(angle), math.sin(angle));
                    team[i] = (byte)(i % players);
                }

                var search = new NearestEnemyJob
                {
                    Positions = positions, Team = team, Health = health, RangeSq = rangeSq,
                    CellStart = cellStart, Sorted = sorted,
                    InvCellSize = 1f / cellSize, CellsX = cellsX, CellsY = cellsY,
                    SearchCells = SearchCells(cellSize), Slice = slice, Target = target,
                };
                var stats = new SpikeStats();
                var sw = Stopwatch.StartNew();

                for (int sample = 0; sample < Samples + Warmup; sample++)
                {
                    search.Tick = sample % slice;
                    ScheduleHash(positions, cell, cellStart, sorted, cellSize, cellsX, cellsY).Complete();
                    sw.Restart();
                    search.Schedule(count, Batch).Complete();
                    sw.Stop();
                    if (sample >= Warmup) stats.Add(sw.Elapsed.TotalMilliseconds);
                }

                SpikeResults.Write(a, "hash.search.clump", "ms", stats);
                Debug.Log($"[Hash] clump: {count} units packed in a {ClumpRadius}-tile disc " +
                          $"({count / (math.PI * ClumpRadius * ClumpRadius):F1} units/tile), slice {slice}");
            }
            finally
            {
                positions.Dispose();
                team.Dispose();
                health.Dispose();
                rangeSq.Dispose();
                cell.Dispose();
                cellStart.Dispose();
                sorted.Dispose();
                target.Dispose();
            }
        }

        /// <summary>
        /// The O(n^2) reference the hash exists to replace: every unit scans every other unit. Measured
        /// only at the default cell size, once per map, because the answer does not depend on the hash
        /// parameters. Its samples are wall seconds, not the tick cost.
        /// </summary>
        private static void MeasureBruteForce(SpikeArgs a, in SpikeScenario scenario, NativeArray<float> rangeSq)
        {
            using var target = new NativeArray<int>(scenario.UnitCount, Allocator.Persistent);
            var job = new BruteEnemyJob
            {
                Positions = scenario.UnitPositions, Team = scenario.UnitOwners,
                Health = scenario.UnitHealth, RangeSq = rangeSq, Target = target,
            };
            var stats = new SpikeStats();
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < BruteSamples + BruteWarmup; sample++)
            {
                sw.Restart();
                job.Schedule(scenario.UnitCount, Batch).Complete();
                sw.Stop();
                if (sample >= BruteWarmup) stats.Add(sw.Elapsed.TotalMilliseconds);
            }

            SpikeResults.Write(a, "hash.brute", "ms", stats);
            Debug.Log($"[Hash] brute force: {scenario.UnitCount}^2 = " +
                      $"{scenario.UnitCount * (long)scenario.UnitCount:N0} pair checks over {BruteSamples} samples");
        }

        /// <summary>Cells a search must reach to cover <see cref="AttackRange"/>: ceil(range / cell size).</summary>
        private static int SearchCells(int cellSize) => (int)math.ceil(AttackRange / cellSize);

        /// <summary>Fills the multimap with (cell, unit) pairs; the parallel writer is the point of the variant.</summary>
        [BurstCompile]
        private struct MultiMapFillJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Cell;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Cells;

            public void Execute(int i) => Cells.Add(Cell[i], i);
        }

        /// <summary>The O(n^2) nearest living enemy in range, one unit a thread, for the reference timing.</summary>
        [BurstCompile]
        private struct BruteEnemyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Positions;
            [ReadOnly] public NativeArray<byte> Team;
            [ReadOnly] public NativeArray<float> Health;
            [ReadOnly] public NativeArray<float> RangeSq;
            public NativeArray<int> Target;

            public void Execute(int i)
            {
                if (Health[i] <= 0f) { Target[i] = -1; return; }
                float best = RangeSq[i];
                int bestIndex = -1;
                for (int j = 0; j < Positions.Length; j++)
                {
                    if (Team[j] == Team[i] || Health[j] <= 0f) continue;
                    float d = math.distancesq(Positions[i], Positions[j]);
                    if (d <= best) { best = d; bestIndex = j; }
                }
                Target[i] = bestIndex;
            }
        }
    }
}
