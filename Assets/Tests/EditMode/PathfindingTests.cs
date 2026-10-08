using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.World;

public class PathfindingTests
{
    private MapStore store;
    private MapGrid map => store.Grid;

    [SetUp]
    public void SetUp()
    {
        var rows = new string[20];
        for (int i = 0; i < rows.Length; i++) rows[i] = new string('.', 20);
        store = MapStore.FromAscii(rows);
    }

    [TearDown]
    public void TearDown() => store.Dispose();

    private void Wall(int x, int y)
    {
        var tiles = map.Tiles;
        tiles[map.Index(new int2(x, y))] = (byte)TileType.Wall;
    }

    [Test]
    public void StraightLine_StartsAtStartEndsAtEnd()
    {
        var path = Pathfinding.FindPath(map, new int2(0, 0), new int2(5, 0));
        Assert.That(path[0], Is.EqualTo(new int2(0, 0)));
        Assert.That(path[path.Count - 1], Is.EqualTo(new int2(5, 0)));
        Assert.That(path.Count, Is.EqualTo(6));
    }

    [Test]
    public void GoesAroundWall()
    {
        for (int y = 0; y < 19; y++) Wall(5, y);
        var path = Pathfinding.FindPath(map, new int2(0, 0), new int2(10, 0));
        Assert.That(path, Is.Not.Empty);
        Assert.That(path, Has.None.Matches<int2>(p => p.x == 5 && p.y < 19));
    }

    [Test]
    public void AvoidsUsedTiles()
    {
        for (int y = 0; y < 19; y++) store.SetUsed(new int2(5, y), true);
        var path = Pathfinding.FindPath(map, new int2(0, 0), new int2(10, 0));
        Assert.That(path, Has.None.Matches<int2>(p => p.x == 5 && p.y < 19));
    }

    [Test]
    public void Unreachable_ReturnsEmpty()
    {
        for (int y = 0; y < 20; y++) Wall(5, y);
        Assert.That(Pathfinding.FindPath(map, new int2(0, 0), new int2(10, 0)), Is.Empty);
    }

    [Test]
    public void GoalOnWall_ReturnsEmpty()
    {
        Wall(10, 0);
        Assert.That(Pathfinding.FindPath(map, new int2(0, 0), new int2(10, 0)), Is.Empty);
    }

    [Test]
    public void StartEqualsGoal_ReturnsEmpty()
    {
        Assert.That(Pathfinding.FindPath(map, new int2(3, 3), new int2(3, 3)), Is.Empty);
    }

    [Test]
    public void RepeatedSearches_DoNotLeak()
    {
        // Persistent allocations that are never freed make this throw at the end of the test run when leak detection is on.
        NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        for (int i = 0; i < 200; i++) Pathfinding.FindPath(map, new int2(0, 0), new int2(19, 19));
        Assert.Pass();
    }
}
