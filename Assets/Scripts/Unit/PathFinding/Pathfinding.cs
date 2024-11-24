using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

public enum GCostMethod
{
    Manhattan,
    Euclidean,
    Chebyshev,
    Octile,
    Minkowski,
    Diagonal,
    DiagonalShort,
    DiagonalLong,
    EuclideanNoSQR,
}


public static class Pathfinding
{
    public static Path FindPath(Tilemap tilemap, int2 start, int2 end, GCostMethod gCostMethod = GCostMethod.Euclidean, int maxIterations = 1000)
    {
        if (start.Equals(end))
        {
            return new Path(new PathNode[0], 0, 0);
        }

        var openSet = new PriorityQueue<PathNode>(tilemap.width * tilemap.height, (x, y) => x.cost.CompareTo(y.cost));
        var closedSet = new HashSet<int2>();

        var startNode = new PathNode
        {
            start = start,
            end = end,
            fcost = 0,
            gcost = 0,
            hcost = 0,
        };

        openSet.Enqueue(startNode);

        while (openSet.Count > 0)
        {
            PathNode currentNode = openSet.Dequeue();
            closedSet.Add(currentNode.start);

            if (currentNode.start.Equals(end))
            {
                return RetracePath(currentNode);
            }



        }
    }

    private static Path RetracePath(PathNode endNode)
    {

    }
}