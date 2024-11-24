using Unity.Mathematics;

public struct PathNode
{
    public int2 start;
    public int2 end;
    public float fcost;
    public float gcost;
    public float hcost;

    public float cost => gcost + hcost + fcost;



}

public struct Path
{
    public PathNode[] path;
    public int pathLength;
    public int pathCost;

    public Path(PathNode[] path, int pathLength, int pathCost)
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
}

public struct Tilemap
{
    public (int2, TileNode)[] tiles;
    public int width;
    public int height;

    public Tilemap((int2, TileNode)[] tiles, int width, int height)
    {
        this.tiles = tiles;
        this.width = width;
        this.height = height;
    }
}