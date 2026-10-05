using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

public class PathfindingTests
{
    private TilemapStruct map;

    [SetUp]
    public void SetUp()
    {
        var tiles = new NativeHashMap<int2, TileNode>(400, Allocator.Persistent);
        for (int x = 0; x < 20; x++)
            for (int y = 0; y < 20; y++)
                tiles[new int2(x, y)] = new TileNode { position = new int2(x, y), weight = 1, tileType = TileType.Ground };
        map = new TilemapStruct(tiles, 20, 20);
    }

    [TearDown]
    public void TearDown() => map.tiles.Dispose();

    private void Wall(int x, int y) => map.SetTile(new int2(x, y), new TileNode { position = new int2(x, y), weight = 0, tileType = TileType.Wall });

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
        for (int y = 0; y < 19; y++) map.SetTile(new int2(5, y), new TileNode { position = new int2(5, y), weight = 1, used = 1 });
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
