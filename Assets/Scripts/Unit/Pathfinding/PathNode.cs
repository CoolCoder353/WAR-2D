using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Tilemaps;
using UnityEngine;


public struct PathNode
{
    public int2 position;

    public float gcost;
    public float hcost;

    public float weight;

    public TileType tileType;

    public float fcost => (gcost * 0.01f + hcost) * weight;

    public static TileNode PathNodeToTileNode(PathNode pathNode)
    {
        return new TileNode
        {
            position = pathNode.position,
            weight = 1,
            used = 0,
            tileType = pathNode.tileType
        };
    }

    //Cloning
    public PathNode(PathNode pathNode)
    {
        position = pathNode.position;
        gcost = pathNode.gcost;
        hcost = pathNode.hcost;
        weight = pathNode.weight;
        tileType = pathNode.tileType;
    }

    public PathNode(int2 position, float gcost, float hcost, float weight, TileType tileType)
    {
        this.position = position;
        this.gcost = gcost;
        this.hcost = hcost;
        this.weight = weight;
        this.tileType = tileType;
    }


    //Comparer
    public bool Equals(PathNode pathNode)
    {
        return position.Equals(pathNode.position);
    }

}

public struct BurstPath : IDisposable
{
    public NativeArray<PathNode> path;
    public int pathLength;
    public float pathCost;

    public BurstPath(NativeArray<PathNode> path, int pathLength, float pathCost)
    {
        this.path = path;
        this.pathLength = pathLength;
        this.pathCost = pathCost;
    }

    public void Dispose()
    {
        if (path.IsCreated)
        {
            path.Dispose();
        }
    }
}

// Named PathResult rather than Path: a global-namespace Path shadows PathCore.Path in the
// predefined assemblies that compile DOTween's Modules, which now reference the WAR2D assembly.
public struct PathResult
{
    public PathNode[] path;
    public int pathLength;
    public float pathCost;

    public PathResult(PathNode[] path, int pathLength, float pathCost)
    {
        this.path = path;
        this.pathLength = pathLength;
        this.pathCost = pathCost;
    }


    public static PathResult BurstToPath(BurstPath burstPath)
    {
        PathNode[] path = new PathNode[burstPath.path.Length];
        for (int i = 0; i < burstPath.path.Length; i++)
        {
            path[i] = burstPath.path[i];
        }

        return new PathResult(path, burstPath.pathLength, burstPath.pathCost);
    }
}

public struct TileNode
{
    public int2 position;
    public float weight;
    public int used;

    public bool isUsed => used > 0;

    public bool isWalkable => weight > 0;

    public TileType tileType;


    public static PathNode TileNodeToPathNode(TileNode tileNode)
    {
        return new PathNode
        {
            position = tileNode.position,
            gcost = 0,
            hcost = 0,
            weight = tileNode.weight * (tileNode.isUsed ? 0 : 1),
            tileType = tileNode.tileType
        };
    }
}

public struct TilemapStruct
{
    public NativeHashMap<int2, TileNode> tiles;
    public int width;
    public int height;

    public TilemapStruct(NativeHashMap<int2, TileNode> tiles, int width, int height)
    {
        this.tiles = tiles;
        this.width = width;
        this.height = height;
    }

    public bool Contains(int2 position) => tiles.ContainsKey(position);

    /// <summary>Returns the tile, or an unwalkable wall for positions outside the map.</summary>
    public TileNode GetTile(int2 position)
    {
        if (tiles.TryGetValue(position, out TileNode tileNode)) return tileNode;
        return new TileNode { position = position, weight = 0, used = 0, tileType = TileType.Wall };
    }

    public void SetTile(int2 position, TileNode tileNode)
    {
        tiles[position] = tileNode;
    }
}

public enum TileType
{
    Ground,
    Wall,
    Gem
}