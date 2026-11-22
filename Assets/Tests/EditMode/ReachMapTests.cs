using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;

/// <summary>The AI's reach map (v0.21 Warpath §3): floor components, walls that join them, breaches, sliced relabels.</summary>
public class ReachMapTests
{
    private const int N = 16;

    /// <summary>A 16×16 map of open ground with a rock border.</summary>
    private static byte[] Open()
    {
        var t = new byte[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                t[y * N + x] = (byte)(x == 0 || y == 0 || x == N - 1 || y == N - 1 ? TileType.Wall : TileType.Ground);
        return t;
    }

    /// <summary>Column <paramref name="x"/> (inside the border) set to <paramref name="kind"/>.</summary>
    private static void Column(byte[] t, int x, TileType kind)
    {
        for (int y = 1; y < N - 1; y++) t[y * N + x] = (byte)kind;
    }

    private static ReachMap Built(byte[] t)
    {
        var map = new ReachMap(N, N);
        map.Rebuild(t);
        return map;
    }

    private static readonly int2 West = new int2(3, 8), East = new int2(12, 8);

    [Test]
    public void OpenFloorIsOpen()
    {
        byte[] t = Open();
        Assert.That(Built(t).Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(Reach.Open));
    }

    [Test]
    public void RockSplitIsCut()
    {
        byte[] t = Open();
        Column(t, 7, TileType.Wall);
        Assert.That(Built(t).Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(Reach.Cut));
    }

    [Test]
    public void OneWallTileIsAOneTileBreach()
    {
        byte[] t = Open();
        Column(t, 7, TileType.PlayerWall);
        var breach = new List<int2>();
        Reach r = Built(t).Query(West, East, t, _ => 150f, breach, out float cost);
        Assert.That(r, Is.EqualTo(Reach.Walled));
        Assert.That(breach.Count, Is.EqualTo(1));
        Assert.That(breach[0].x, Is.EqualTo(7));
        Assert.That(cost, Is.EqualTo(150f));
    }

    [Test]
    public void ThickerWallCostsMore()
    {
        byte[] t = Open();
        Column(t, 7, TileType.PlayerWall);
        Column(t, 8, TileType.PlayerWall);
        var breach = new List<int2>();
        Reach r = Built(t).Query(West, East, t, _ => 150f, breach, out float cost);
        Assert.That(r, Is.EqualTo(Reach.Walled));
        Assert.That(breach.Count, Is.EqualTo(2));
        Assert.That(breach[0].x, Is.EqualTo(7), "from the near side first");
        Assert.That(breach[1].x, Is.EqualTo(8));
        Assert.That(cost, Is.EqualTo(300f));
    }

    [Test]
    public void TheCheapestBreachWins()
    {
        byte[] t = Open();
        Column(t, 7, TileType.PlayerWall);
        int weak = 11 * N + 7;
        var breach = new List<int2>();
        Built(t).Query(West, East, t, i => i == weak ? 10f : 150f, breach, out float cost);
        Assert.That(breach, Is.EqualTo(new List<int2> { new int2(7, 11) }));
        Assert.That(cost, Is.EqualTo(10f));
    }

    [Test]
    public void AWallBlueprintIsFloor()
    {
        byte[] t = Open();
        Column(t, 7, TileType.WallBlueprint);
        Assert.That(Built(t).Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(Reach.Open));
    }

    [Test]
    public void AWallGoneSinceTheRelabelIsNotCounted()
    {
        byte[] t = Open();
        Column(t, 7, TileType.PlayerWall);
        Column(t, 8, TileType.PlayerWall);
        ReachMap map = Built(t);
        Column(t, 8, TileType.Ground); // destroyed, not relabelled yet
        map.ForgetAnswers();
        var breach = new List<int2>();
        Assert.That(map.Query(West, East, t, _ => 150f, breach, out float cost), Is.EqualTo(Reach.Walled));
        Assert.That(cost, Is.EqualTo(150f));
    }

    [Test]
    public void AnEndOffTheFloorSnapsToTheNearestFloor()
    {
        byte[] t = Open();
        t[8 * N + 12] = (byte)TileType.Wall; // East itself is rock
        Assert.That(Built(t).Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(Reach.Open));
    }

    [Test]
    public void SlicedRelabelMatchesAFullOne()
    {
        byte[] t = Open();
        Column(t, 5, TileType.Wall);
        Column(t, 10, TileType.PlayerWall);
        t[4 * N + 5] = (byte)TileType.Ground; // a gap in the rock
        ReachMap full = Built(t);
        var sliced = new ReachMap(N, N, tilesPerStep: 37);
        sliced.MarkDirty();
        int steps = 0;
        while (!sliced.Step(t)) steps++;
        Assert.That(steps, Is.GreaterThan(1), "it took several steps");
        Assert.That(sliced.Components, Is.EqualTo(full.Components));
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                var p = new int2(x, y);
                Assert.That(sliced.ComponentAt(p) >= 0, Is.EqualTo(full.ComponentAt(p) >= 0), $"floor at {p}");
            }
        Assert.That(sliced.Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(full.Query(West, East, t, _ => 1f, null, out _)));
    }

    [Test]
    public void QueriesUseTheLastFinishedLabels()
    {
        byte[] t = Open();
        var map = new ReachMap(N, N, tilesPerStep: 20);
        map.Rebuild(t);
        int version = map.Version;
        Column(t, 7, TileType.Wall);
        map.MarkDirty();
        map.Step(t); // part way
        Assert.That(map.Version, Is.EqualTo(version));
        Assert.That(map.Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(Reach.Open), "the old labels until the relabel finishes");
        while (!map.Step(t)) { }
        Assert.That(map.Version, Is.EqualTo(version + 1));
        Assert.That(map.Query(West, East, t, _ => 1f, null, out _), Is.EqualTo(Reach.Cut));
    }
}
