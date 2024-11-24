using Unity.Mathematics;


public class PathNodeLink
{
    public PathNodeLink parent;
    public PathNode pathNode;

    public int depth;


}

public struct PathNode
{
    public int2 position;

    public float gcost;
    public float hcost;

    public PathNodeLink parent;

    public float fcost => gcost + hcost;

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
        };
    }
}

public struct TilemapStruct
{
    public (int2, TileNode)[] tiles;
    public int width;
    public int height;

    public TilemapStruct((int2, TileNode)[] tiles, int width, int height)
    {
        this.tiles = tiles;
        this.width = width;
        this.height = height;
    }

    public TileNode GetTile(int2 position)
    {
        return tiles[position.y * width + position.x].Item2;
    }
}