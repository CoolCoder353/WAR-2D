using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Sim;
using Random = Unity.Mathematics.Random;

/// <summary>The counting-sort hash and nearest-enemy search agree with brute force.</summary>
public class SpatialHashTests
{
    private const float Range = 5f;
    private const int CellSize = 5;

    private sealed class Rig : System.IDisposable
    {
        public NativeArray<float2> Positions, BuildingPositions;
        public NativeArray<int> Cell, CellStart, Sorted, Target, BCell, BCellStart, BSorted;
        public NativeArray<float> Health, BuildingHealth, RangeSqByType;
        /// <summary>Owner slots: 1 and 2 attack each other.</summary>
        public NativeArray<byte> Owner, BuildingOwner;
        public NativeArray<ushort> AttackMask;
        public NativeArray<byte> Type, TargetKind, Stance, OrderLive;
        public NativeArray<int> OrderSlot;
        public int CellsAcross;

        public Rig(int units, int buildings, int size)
        {
            CellsAcross = (size + CellSize - 1) / CellSize;
            int cells = CellsAcross * CellsAcross + 1;
            Positions = new NativeArray<float2>(units, Allocator.TempJob);
            Owner = new NativeArray<byte>(units, Allocator.TempJob);
            AttackMask = new NativeArray<ushort>(new ushort[] { 0, 1 << 2, 1 << 1 }, Allocator.TempJob);
            Health = new NativeArray<float>(units, Allocator.TempJob);
            Type = new NativeArray<byte>(units, Allocator.TempJob);
            Stance = new NativeArray<byte>(units, Allocator.TempJob);
            OrderSlot = new NativeArray<int>(units, Allocator.TempJob);
            OrderLive = new NativeArray<byte>(1, Allocator.TempJob);
            Cell = new NativeArray<int>(units, Allocator.TempJob);
            CellStart = new NativeArray<int>(cells, Allocator.TempJob);
            Sorted = new NativeArray<int>(units, Allocator.TempJob);
            Target = new NativeArray<int>(units, Allocator.TempJob);
            TargetKind = new NativeArray<byte>(units, Allocator.TempJob);
            BuildingPositions = new NativeArray<float2>(buildings, Allocator.TempJob);
            BuildingOwner = new NativeArray<byte>(buildings, Allocator.TempJob);
            BuildingHealth = new NativeArray<float>(buildings, Allocator.TempJob);
            BCell = new NativeArray<int>(buildings, Allocator.TempJob);
            BCellStart = new NativeArray<int>(cells, Allocator.TempJob);
            BSorted = new NativeArray<int>(buildings, Allocator.TempJob);
            RangeSqByType = new NativeArray<float>(new[] { 0f, Range * Range }, Allocator.TempJob);
            for (int i = 0; i < units; i++) { Type[i] = 1; Health[i] = 100f; }
        }

        public void Search(int tick = 0, int slice = 1)
        {
            float inv = 1f / CellSize;
            JobHandle h = new CellIndexJob { Positions = Positions, Cell = Cell, InvCellSize = inv, CellsX = CellsAcross, CellsY = CellsAcross }.Schedule(Positions.Length, 64);
            h = new CountingSortJob { Cell = Cell, CellStart = CellStart, Sorted = Sorted }.Schedule(h);
            h = new CellIndexJob { Positions = BuildingPositions, Cell = BCell, InvCellSize = inv, CellsX = CellsAcross, CellsY = CellsAcross }.Schedule(BuildingPositions.Length, 64, h);
            h = new CountingSortJob { Cell = BCell, CellStart = BCellStart, Sorted = BSorted }.Schedule(h);
            h = new NearestEnemyJob
            {
                Positions = Positions, OwnerSlot = Owner, AttackMask = AttackMask, Health = Health, Type = Type, RangeSqByType = RangeSqByType,
                Stance = Stance, OrderSlot = OrderSlot, OrderLive = OrderLive,
                CellStart = CellStart, Sorted = Sorted,
                BuildingPositions = BuildingPositions, BuildingOwnerSlot = BuildingOwner, BuildingHealth = BuildingHealth,
                BuildingCellStart = BCellStart, BuildingSorted = BSorted,
                InvCellSize = inv, CellsX = CellsAcross, CellsY = CellsAcross, SearchCells = (int)math.ceil(Range / CellSize),
                Tick = tick, Slice = slice, Target = Target, TargetKind = TargetKind,
            }.Schedule(Positions.Length, 64, h);
            h.Complete();
        }

        public void Dispose()
        {
            Positions.Dispose(); Owner.Dispose(); AttackMask.Dispose(); Health.Dispose(); Type.Dispose(); Stance.Dispose(); OrderSlot.Dispose(); OrderLive.Dispose(); Cell.Dispose(); CellStart.Dispose();
            Sorted.Dispose(); Target.Dispose(); TargetKind.Dispose(); BuildingPositions.Dispose(); BuildingOwner.Dispose();
            BuildingHealth.Dispose(); BCell.Dispose(); BCellStart.Dispose(); BSorted.Dispose(); RangeSqByType.Dispose();
        }
    }

    [Test]
    public void MatchesBruteForce()
    {
        const int count = 2000, size = 128;
        using var rig = new Rig(count, 0, size);
        var rng = new Random(20261005u);
        for (int i = 0; i < count; i++) { rig.Positions[i] = rng.NextFloat2(0f, size); rig.Owner[i] = (byte)(1 + rng.NextInt(2)); }
        rig.Search();

        int found = 0;
        for (int i = 0; i < count; i++)
        {
            float expected = float.PositiveInfinity;
            for (int j = 0; j < count; j++)
                if (rig.Owner[j] != rig.Owner[i]) expected = math.min(expected, math.distancesq(rig.Positions[i], rig.Positions[j]));
            if (expected > Range * Range) { Assert.AreEqual(-1, rig.Target[i], $"unit {i} has no enemy in range"); continue; }
            found++;
            Assert.GreaterOrEqual(rig.Target[i], 0, $"unit {i} missed an enemy {math.sqrt(expected)} away");
            Assert.AreEqual(expected, math.distancesq(rig.Positions[i], rig.Positions[rig.Target[i]]), 1e-5f);
        }
        Assert.Greater(found, 0);
    }

    [Test]
    public void IgnoresDeadAndFriendly()
    {
        using var rig = new Rig(4, 0, 64);
        rig.Positions[0] = new float2(10, 10); rig.Owner[0] = 1;
        rig.Positions[1] = new float2(10.5f, 10); rig.Owner[1] = 2; rig.Health[1] = 0f; // dead enemy, closest
        rig.Positions[2] = new float2(11, 10); rig.Owner[2] = 1;                        // ally
        rig.Positions[3] = new float2(13, 10); rig.Owner[3] = 2;                        // live enemy
        rig.Search();
        Assert.AreEqual(3, rig.Target[0]);
        Assert.AreEqual(TargetKinds.Unit, rig.TargetKind[0]);
    }

    [Test]
    public void FallsBackToEnemyBuildings()
    {
        using var rig = new Rig(1, 2, 64);
        rig.Positions[0] = new float2(10, 10); rig.Owner[0] = 1;
        rig.BuildingPositions[0] = new float2(12, 10); rig.BuildingOwner[0] = 1; rig.BuildingHealth[0] = 100; // own
        rig.BuildingPositions[1] = new float2(13, 10); rig.BuildingOwner[1] = 2; rig.BuildingHealth[1] = 100; // enemy
        rig.Search();
        Assert.AreEqual(1, rig.Target[0]);
        Assert.AreEqual(TargetKinds.Building, rig.TargetKind[0]);
    }

    [Test]
    public void SliceSkipsOffTickUnits()
    {
        using var rig = new Rig(2, 0, 64);
        rig.Positions[0] = new float2(10, 10); rig.Owner[0] = 1;
        rig.Positions[1] = new float2(12, 10); rig.Owner[1] = 2;
        rig.Target[0] = -7; rig.Target[1] = -7;
        rig.Search(tick: 0, slice: 2); // only slot 0 searches on tick 0
        Assert.AreEqual(1, rig.Target[0]);
        Assert.AreEqual(-7, rig.Target[1]);
    }
}
