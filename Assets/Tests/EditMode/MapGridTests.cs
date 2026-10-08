using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.World;

/// <summary>Accessor, footprint and hashing tests for <see cref="MapGrid"/> and <see cref="MapStore"/>.</summary>
public class MapGridTests
{
    [Test]
    public void OutsideTheMapIsBorder()
    {
        using MapStore store = MapStore.FromAscii("..", "..");
        Assert.IsFalse(store.Grid.Contains(new int2(-1, 0)));
        Assert.IsFalse(store.Grid.Contains(new int2(2, 0)));
        Assert.AreEqual(TileType.Border, store.Grid.TileAt(new int2(-1, 0)));
        Assert.AreEqual(TileType.Border, store.Grid.TileAt(new int2(0, 5)));
        Assert.AreEqual(TileType.Ground, store.Grid.TileAt(new int2(1, 1)));
    }

    [Test]
    public void OnlyFreeGroundIsWalkable()
    {
        using MapStore store = MapStore.FromAscii(".#gB");
        MapGrid g = store.Grid;
        Assert.IsTrue(g.IsWalkable(new int2(0, 0)));
        Assert.IsFalse(g.IsWalkable(new int2(1, 0)));
        Assert.IsFalse(g.IsWalkable(new int2(2, 0)));
        Assert.IsFalse(g.IsWalkable(new int2(3, 0)));
        store.SetUsed(new int2(0, 0), true);
        Assert.IsFalse(store.Grid.IsWalkable(new int2(0, 0)));
        Assert.IsTrue(store.Grid.BlocksMovement(new int2(0, 0)));
    }

    [Test]
    public void SetUsedRecordsChangedTiles()
    {
        using MapStore store = MapStore.FromAscii("...");
        store.SetUsed(new int2(2, 0), true);
        CollectionAssert.AreEqual(new[] { new int2(2, 0) }, store.ChangedTiles);
        store.SetUsed(new int2(2, 0), false);
        Assert.AreEqual(2, store.ChangedTiles.Count);
        Assert.IsFalse(store.Grid.IsUsed(new int2(2, 0)));
    }

    [Test]
    public void HashFollowsTheSeed()
    {
        using MapStore a = MapStore.Generate(128, 5, 0.04f);
        using MapStore b = MapStore.Generate(128, 5, 0.04f);
        using MapStore c = MapStore.Generate(128, 6, 0.04f);
        Assert.AreEqual(a.Hash(), b.Hash());
        Assert.AreNotEqual(a.Hash(), c.Hash());
        Assert.AreEqual(8, a.HqSites.Length);
    }

    [Test]
    public void DisposeTwiceIsSafe()
    {
        MapStore store = MapStore.FromAscii("..");
        store.Dispose();
        Assert.DoesNotThrow(store.Dispose);
    }
}
