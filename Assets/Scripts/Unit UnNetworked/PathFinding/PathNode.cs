using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Tilemaps;



public class PathNode
{
    public int2 position;

    public float gcost;
    public float hcost;

    public PathNode parent;

    public float weight;

    public float fcost => (gcost + hcost) * weight;

    public static TileNode PathNodeToTileNode(PathNode pathNode)
    {
        return new TileNode
        {
            position = pathNode.position,
            weight = 1,
            used = 0,
        };
    }

    //Cloning
    public PathNode(PathNode pathNode)
    {
        position = pathNode.position;
        gcost = pathNode.gcost;
        hcost = pathNode.hcost;
        parent = pathNode.parent;
        weight = pathNode.weight;
    }

    public PathNode()
    {
        position = new int2(0, 0);
        gcost = 0;
        hcost = 0;
        parent = null;
        weight = 1;
    }

    //Comparer
    public bool Equals(PathNode pathNode)
    {
        return position.Equals(pathNode.position);
    }


}

public struct Path
{
    public PathNode[] path;
    public int pathLength;
    public float pathCost;

    public Path(PathNode[] path, int pathLength, float pathCost)
    {
        this.path = path;
        this.pathLength = pathLength;
        this.pathCost = pathCost;
    }
}

public struct TileNode
{
    public int2 position;
    public float weight;
    public int used;

    public bool isUsed => used > 0;

    public bool isWalkable => weight > 0;


    public static PathNode TileNodeToPathNode(TileNode tileNode)
    {
        return new PathNode
        {
            position = tileNode.position,
            gcost = 0,
            hcost = 0,
            parent = null,
            weight = tileNode.weight * (tileNode.isUsed ? 0 : 1)
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

    public TileNode GetTile(int2 position)
    {
        if (!tiles.TryGetValue(position, out TileNode tileNode))
        {
            return new TileNode
            {
                position = position,
                weight = 0,
                used = 0
            };
        }

        return tiles[position];
    }

    public void SetTile(int2 position, TileNode tileNode)
    {
        tiles[position] = tileNode;
    }
}