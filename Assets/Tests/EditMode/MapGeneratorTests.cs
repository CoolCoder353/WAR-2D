using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.World;

/// <summary>Correctness tests for the seeded cave generator.</summary>
public class MapGeneratorTests
{
    private const int Size = 256;
    private const uint Seed = 7;

    private static readonly int2[] Steps =
    {
        new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1),
    };

    [Test]
    public void SameSeedSameMap()
    {
        using GenMap a = GenMap.Make(Size, Seed);
        using GenMap b = GenMap.Make(Size, Seed);
        using GenMap c = GenMap.Make(Size, Seed + 1);

        int mismatch = -1, differences = 0;
        for (int i = 0; i < a.Tiles.Length; i++)
        {
            if (a.Tiles[i] != b.Tiles[i] && mismatch < 0) mismatch = i;
            if (a.Tiles[i] != c.Tiles[i]) differences++;
        }
        Assert.AreEqual(-1, mismatch, "the same seed produced two different maps");
        Assert.Greater(differences, 0, "seed 8 produced the same map as seed 7");
    }

    [Test]
    public void BorderIsBlocked()
    {
        using GenMap map = GenMap.Make(Size, Seed);
        int2 bad = new int2(-1, -1);
        for (int x = 0; x < map.Width; x++)
        {
            if (map.TileAt(new int2(x, 0)) != GenMap.Border || map.TileAt(new int2(x, map.Height - 1)) != GenMap.Border)
                bad = new int2(x, 0);
        }
        for (int y = 0; y < map.Height; y++)
        {
            if (map.TileAt(new int2(0, y)) != GenMap.Border || map.TileAt(new int2(map.Width - 1, y)) != GenMap.Border)
                bad = new int2(0, y);
        }
        Assert.AreEqual(new int2(-1, -1), bad, $"the outer ring is not all Border (first bad column/row {bad})");
    }

    [Test]
    public void HqSitesAreFloorAndClear()
    {
        int2[] sites = MapGenerator.HqSites(Size);
        Assert.AreEqual(8, sites.Length);

        using GenMap map = GenMap.Make(Size, Seed);
        string failure = null;
        for (int s = 0; s < sites.Length && failure == null; s++)
        for (int dy = -6; dy <= 6 && failure == null; dy++)
        for (int dx = -6; dx <= 6 && failure == null; dx++)
        {
            int2 tile = sites[s] + new int2(dx, dy);
            if (map.TileAt(tile) != GenMap.Floor)
                failure = $"HQ {s} at {sites[s]} is not clear at {tile}";
        }
        Assert.IsNull(failure);

        for (int i = 0; i < sites.Length; i++)
        for (int j = i + 1; j < sites.Length; j++)
            Assert.AreNotEqual(sites[i], sites[j], $"HQ sites {i} and {j} coincide");
    }

    [Test]
    public void AllFloorIsConnected()
    {
        using GenMap map = GenMap.Make(Size, Seed);
        int2 start = MapGenerator.HqSites(Size)[0];

        var visited = new bool[map.Tiles.Length];
        var queue = new Queue<int>();
        int startIndex = start.y * map.Width + start.x;
        visited[startIndex] = true;
        queue.Enqueue(startIndex);

        int reached = 0, floor = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            reached++;
            int x = i % map.Width, y = i / map.Width;
            foreach (int2 step in Steps)
            {
                int nx = x + step.x, ny = y + step.y;
                if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height) continue;
                int n = ny * map.Width + nx;
                if (visited[n] || map.Tiles[n] != GenMap.Floor) continue;
                visited[n] = true;
                queue.Enqueue(n);
            }
        }

        for (int i = 0; i < map.Tiles.Length; i++)
            if (map.Tiles[i] == GenMap.Floor) floor++;
        Assert.AreEqual(floor, reached, "some floor tiles are not reachable from HQ 0");
    }

    [Test]
    public void RockFractionIsNearTarget()
    {
        using GenMap map = GenMap.Make(512, Seed);
        int blocked = 0, interior = 0;
        for (int y = 1; y < map.Height - 1; y++)
        for (int x = 1; x < map.Width - 1; x++)
        {
            interior++;
            if (map.Tiles[y * map.Width + x] != GenMap.Floor) blocked++;
        }
        float fraction = (float)blocked / interior;
        Assert.GreaterOrEqual(fraction, 0.20f, $"blocked fraction {fraction:P1} is below 20 %");
        Assert.LessOrEqual(fraction, 0.40f, $"blocked fraction {fraction:P1} is above 40 %");
    }

    [Test]
    public void GemsOnlyReplaceRockThatFacesFloor()
    {
        var (tiles, _) = MapGenerator.Generate(256, 7u, 0.5f, Allocator.Temp);
        int gems = 0;
        for (int y = 1; y < 255; y++)
        for (int x = 1; x < 255; x++)
        {
            if (tiles[y * 256 + x] != (byte)TileType.Gem) continue;
            gems++;
            bool facesFloor = tiles[y * 256 + x - 1] == 0 || tiles[y * 256 + x + 1] == 0
                           || tiles[(y - 1) * 256 + x] == 0 || tiles[(y + 1) * 256 + x] == 0;
            Assert.IsTrue(facesFloor, $"gem at {x},{y} is buried in rock");
        }
        Assert.Greater(gems, 0);
        tiles.Dispose();
    }

    [Test]
    public void EveryHqClearingHasGemsWithinMinerReach()
    {
        var (tiles, sites) = MapGenerator.Generate(512, 3u, 0.04f, Allocator.Temp);
        foreach (int2 site in sites)
        {
            bool found = false;
            for (int dy = -MapGenerator.HqClearRadius - 3; dy <= MapGenerator.HqClearRadius + 3 && !found; dy++)
            for (int dx = -MapGenerator.HqClearRadius - 3; dx <= MapGenerator.HqClearRadius + 3 && !found; dx++)
                found = tiles[(site.y + dy) * 512 + site.x + dx] == (byte)TileType.Gem;
            Assert.IsTrue(found, $"HQ site {site} has no gem within reach");
        }
        tiles.Dispose();
    }

    /// <summary>Adapter giving the ported spike cases the old map shape.</summary>
    private readonly struct GenMap : IDisposable
    {
        public const byte Floor = (byte)TileType.Ground, Rock = (byte)TileType.Wall, Gem = (byte)TileType.Gem, Border = (byte)TileType.Border;
        public readonly int Width, Height;
        public readonly NativeArray<byte> Tiles;
        private GenMap(int size, NativeArray<byte> tiles) { Width = size; Height = size; Tiles = tiles; }
        public static GenMap Make(int size, uint seed) => new GenMap(size, MapGenerator.Generate(size, seed, 0.05f, Allocator.TempJob).tiles);
        public bool Contains(int2 t) => (uint)t.x < (uint)Width && (uint)t.y < (uint)Height;
        public byte TileAt(int2 t) => Contains(t) ? Tiles[t.y * Width + t.x] : Border;
        public void Dispose() => Tiles.Dispose();
    }
}
