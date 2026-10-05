using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Spike;
using Random = Unity.Mathematics.Random;

/// <summary>
/// Correctness tests for the spike's spatial hash and target search: the cell index, the counting
/// sort that groups units by cell, the nearest-enemy search against an O(n^2) reference, and its
/// time slicing.
/// </summary>
public class SpatialHashTests
{
    /// <summary>The plan's attack range: 5 tiles.</summary>
    private const float AttackRange = 5f;

    /// <summary>Cells are 5 tiles square; the default of the spike's cell-size sweep.</summary>
    private const int CellSize = 5;

    /// <summary>How many grid cells a side of a <paramref name="size"/>-tile area needs.</summary>
    private static int CellsAcross(int size) => (size + CellSize - 1) / CellSize;

    /// <summary>Builds the hash for a unit set and runs one search tick over it.</summary>
    private static void Search(
        NativeArray<float2> positions, NativeArray<byte> team, NativeArray<float> health,
        NativeArray<float> rangeSq, int cellsX, int cellsY,
        NativeArray<int> cell, NativeArray<int> cellStart, NativeArray<int> sorted, NativeArray<int> target,
        int tick, int slice)
    {
        JobHandle handle = new CellIndexJob
        {
            Positions = positions, Cell = cell,
            InvCellSize = 1f / CellSize, CellsX = cellsX, CellsY = cellsY,
        }.Schedule(positions.Length, 64);
        handle = new CountingSortJob { Cell = cell, CellStart = cellStart, Sorted = sorted }.Schedule(handle);
        handle = new NearestEnemyJob
        {
            Positions = positions, Team = team, Health = health, RangeSq = rangeSq,
            CellStart = cellStart, Sorted = sorted,
            InvCellSize = 1f / CellSize, CellsX = cellsX, CellsY = cellsY,
            SearchCells = (int)math.ceil(AttackRange / CellSize),
            Tick = tick, Slice = slice, Target = target,
        }.Schedule(positions.Length, 64, handle);
        handle.Complete();
    }

    /// <summary>A temp array of <paramref name="count"/> copies of <paramref name="value"/>.</summary>
    private static NativeArray<int> Filled(int count, int value)
    {
        var array = new NativeArray<int>(count, Allocator.TempJob);
        for (int i = 0; i < count; i++) array[i] = value;
        return array;
    }

    /// <summary>Fills positions, teams, health and ranges with a deterministic random battle.</summary>
    private static void RandomUnits(int count, int size, NativeArray<float2> positions, NativeArray<byte> team,
        NativeArray<float> health, NativeArray<float> rangeSq, uint seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            positions[i] = rng.NextFloat2(new float2(0f), new float2(size));
            team[i] = (byte)rng.NextInt(2);
            health[i] = 100f;
            rangeSq[i] = AttackRange * AttackRange;
        }
    }

    [Test]
    public void MatchesBruteForce()
    {
        const int count = 2000, size = 128;
        using var positions = new NativeArray<float2>(count, Allocator.TempJob);
        using var team = new NativeArray<byte>(count, Allocator.TempJob);
        using var health = new NativeArray<float>(count, Allocator.TempJob);
        using var rangeSq = new NativeArray<float>(count, Allocator.TempJob);
        using var cell = new NativeArray<int>(count, Allocator.TempJob);
        using var cellStart = new NativeArray<int>(CellsAcross(size) * CellsAcross(size) + 1, Allocator.TempJob);
        using var sorted = new NativeArray<int>(count, Allocator.TempJob);
        using var target = new NativeArray<int>(count, Allocator.TempJob);
        int cells = CellsAcross(size);
        RandomUnits(count, size, positions, team, health, rangeSq, seed: 20261005u);

        Search(positions, team, health, rangeSq, cells, cells, cell, cellStart, sorted, target, tick: 0, slice: 1);

        string failure = null;
        int found = 0;
        for (int i = 0; i < count && failure == null; i++)
        {
            // The O(n^2) reference: the nearest living enemy's squared distance, or none in range.
            float expected = float.PositiveInfinity;
            for (int j = 0; j < count; j++)
            {
                if (team[j] == team[i] || health[j] <= 0f) continue;
                expected = math.min(expected, math.distancesq(positions[i], positions[j]));
            }

            if (expected > rangeSq[i])
            {
                if (target[i] != -1)
                    failure = $"unit {i} has no enemy in range, but the search returned {target[i]}";
                continue;
            }

            found++;
            if (target[i] < 0)
            {
                failure = $"unit {i} has an enemy {math.sqrt(expected):F4} away, but the search found none";
                continue;
            }
            float actual = math.distancesq(positions[i], positions[target[i]]);
            if (math.abs(actual - expected) > 1e-5f)
                failure = $"unit {i} found unit {target[i]} at {math.sqrt(actual):F5}, nearest is {math.sqrt(expected):F5}";
        }
        Assert.IsNull(failure);
        Assert.Greater(found, 0, "the battle should give most units a target");
    }

    [Test]
    public void IgnoresDeadAndFriendly()
    {
        using var positions = new NativeArray<float2>(new[]
        {
            new float2(10f, 10f),   // 0: searcher, team 0
            new float2(10.5f, 10f), // 1: dead enemy, the closest of all
            new float2(11f, 10f),   // 2: live ally
            new float2(13f, 10f),   // 3: live enemy, the nearest live enemy
            new float2(20f, 10f),   // 4: live enemy, out of range
            new float2(40f, 40f),   // 5: searcher, team 1, with only a dead enemy nearby
            new float2(40.5f, 40f), // 6: dead enemy of unit 5
        }, Allocator.TempJob);
        using var team = new NativeArray<byte>(new byte[] { 0, 1, 0, 1, 1, 1, 0 }, Allocator.TempJob);
        using var health = new NativeArray<float>(new[] { 100f, 0f, 100f, 100f, 100f, 100f, 0f }, Allocator.TempJob);
        using var rangeSq = new NativeArray<float>(
            new[] { 25f, 25f, 25f, 25f, 25f, 25f, 25f }, Allocator.TempJob); // 5 tiles squared
        using var cell = new NativeArray<int>(7, Allocator.TempJob);
        using var cellStart = new NativeArray<int>(CellsAcross(64) * CellsAcross(64) + 1, Allocator.TempJob);
        using var sorted = new NativeArray<int>(7, Allocator.TempJob);
        using var target = new NativeArray<int>(7, Allocator.TempJob);

        Search(positions, team, health, rangeSq, CellsAcross(64), CellsAcross(64),
            cell, cellStart, sorted, target, tick: 0, slice: 1);

        Assert.AreEqual(3, target[0], "the dead enemy at 0.5 tiles and the ally at 1 tile are skipped; " +
                                      "the live enemy at 3 tiles is the target, and the one at 10 tiles is out of range");
        Assert.AreEqual(-1, target[5], "a searcher with only a dead enemy in range has no target");
    }

    [Test]
    public void SlicingCoversEveryUnitOnce()
    {
        const int count = 2000, size = 128, slice = 4;
        using var positions = new NativeArray<float2>(count, Allocator.TempJob);
        using var team = new NativeArray<byte>(count, Allocator.TempJob);
        using var health = new NativeArray<float>(count, Allocator.TempJob);
        using var rangeSq = new NativeArray<float>(count, Allocator.TempJob);
        using var cell = new NativeArray<int>(count, Allocator.TempJob);
        using var cellStart = new NativeArray<int>(CellsAcross(size) * CellsAcross(size) + 1, Allocator.TempJob);
        using var sorted = new NativeArray<int>(count, Allocator.TempJob);
        // Sentinel: a unit searched this window never writes -2, so the value marks the untouched units.
        using var target = Filled(count, -2);
        int cells = CellsAcross(size);
        RandomUnits(count, size, positions, team, health, rangeSq, seed: 7u);

        var searched = new bool[count];
        for (int tick = 0; tick < slice; tick++)
        {
            Search(positions, team, health, rangeSq, cells, cells, cell, cellStart, sorted, target, tick, slice);
            int newly = 0;
            for (int i = 0; i < count; i++)
            {
                if (searched[i] || target[i] == -2) continue;
                searched[i] = true;
                newly++;
            }
            Assert.AreEqual(count / slice, newly, $"tick {tick} must search exactly 1/{slice} of the units");
        }

        int missed = 0;
        foreach (bool wasSearched in searched) if (!wasSearched) missed++;
        Assert.AreEqual(0, missed, "every unit must be searched once over the slice window");
    }

    [Test]
    public void SortGroupsByCell()
    {
        const int count = 2000, size = 128;
        using var positions = new NativeArray<float2>(count, Allocator.TempJob);
        using var team = new NativeArray<byte>(count, Allocator.TempJob);
        using var health = new NativeArray<float>(count, Allocator.TempJob);
        using var rangeSq = new NativeArray<float>(count, Allocator.TempJob);
        using var cell = new NativeArray<int>(count, Allocator.TempJob);
        int cells = CellsAcross(size);
        using var cellStart = new NativeArray<int>(cells * cells + 1, Allocator.TempJob);
        using var sorted = new NativeArray<int>(count, Allocator.TempJob);
        RandomUnits(count, size, positions, team, health, rangeSq, seed: 99u);

        JobHandle handle = new CellIndexJob
        {
            Positions = positions, Cell = cell,
            InvCellSize = 1f / CellSize, CellsX = cells, CellsY = cells,
        }.Schedule(count, 64);
        new CountingSortJob { Cell = cell, CellStart = cellStart, Sorted = sorted }.Schedule(handle).Complete();

        Assert.AreEqual(0, cellStart[0], "the first group starts at 0");
        Assert.AreEqual(count, cellStart[cells * cells], "the groups end at the unit count");

        string failure = null;
        var seen = new bool[count];
        for (int c = 0; c < cells * cells && failure == null; c++)
        {
            for (int k = cellStart[c]; k < cellStart[c + 1] && failure == null; k++)
            {
                int unit = sorted[k];
                if (cell[unit] != c)
                    failure = $"Sorted[{k}] is unit {unit} with cell {cell[unit]}, not {c}";
                else if (seen[unit])
                    failure = $"unit {unit} appears twice in Sorted";
                seen[unit] = true;
            }
        }
        Assert.IsNull(failure);

        int seenCount = 0;
        foreach (bool wasSeen in seen) if (wasSeen) seenCount++;
        Assert.AreEqual(count, seenCount, "Sorted must contain every unit exactly once");
    }
}
