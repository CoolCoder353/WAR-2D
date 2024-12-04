using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;


public enum HCostMethod
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
    public static Path FindPath(TilemapStruct tilemap, int2 start, int2 end, HCostMethod hCostMethod = HCostMethod.Euclidean, int maxIterations = 1000)
    {
        if (start.Equals(end))
        {
            return new Path(new PathNode[0], 0, 0);
        }
        //Mulitply by -1 to make it chose the lowest fcost first
        var openSet = new PriorityQueue<PathNode>(tilemap.width * tilemap.height, (x, y) => (-1 * x.fcost).CompareTo(-1 * y.fcost));
        var closedSet = new HashSet<int2>();



        var startNode = new PathNode()
        {
            position = start,
            gcost = 0,
            hcost = 0,
        };

        openSet.Enqueue(startNode);

        while (openSet.Count > 0)
        {
            PathNode currentNode = openSet.Dequeue();
            closedSet.Add(currentNode.position);

            if (currentNode.position.Equals(end))
            {
                return RetracePath(currentNode);
            }

            PathNode[] neighbours = GetNeighbours(tilemap, currentNode);
            foreach (PathNode neigbour in neighbours)
            {
                if (closedSet.Contains(neigbour.position) || neigbour.position.Equals(currentNode.position) || neigbour.weight <= 0)
                {
                    continue;
                }

                float newGCost = currentNode.gcost + math.distance(currentNode.position, neigbour.position);
                float newHCost = CalculateHCost(neigbour.position, end, hCostMethod);



                PathNode newPathNode = new PathNode(neigbour)
                {
                    gcost = newGCost,
                    hcost = newHCost,
                    parent = currentNode
                };
                openSet.Enqueue(newPathNode);
            }
        }
        return new Path(new PathNode[0], 0, 0);
    }

    private static float CalculateHCost(int2 start, int2 end, HCostMethod hCostMethod)
    {
        switch (hCostMethod)
        {
            case HCostMethod.Manhattan:
                return HCostManhattan(start, end);
            case HCostMethod.Euclidean:
                return HCostEuclidean(start, end);
            case HCostMethod.Chebyshev:
                return HCostChebyshev(start, end);
            case HCostMethod.Octile:
                return HCostOctile(start, end);
            case HCostMethod.Minkowski:
                return HCostMinkowski(start, end);
            case HCostMethod.Diagonal:
                return HCostDiagonal(start, end);
            case HCostMethod.DiagonalShort:
                return HCostDiagonalShort(start, end);
            case HCostMethod.DiagonalLong:
                return HCostDiagonalLong(start, end);
            case HCostMethod.EuclideanNoSQR:
                return HCostEuclideanNoSQR(start, end);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static Path RetracePath(PathNode endNode)
    {
        List<PathNode> path = new List<PathNode>();
        PathNode currentNode = endNode;

        Debug.Log($"Retracing path from {endNode.position}");
        while (currentNode.parent != null)
        {
            path.Add(currentNode);
            currentNode = currentNode.parent;
        }

        // Add the start node
        path.Add(currentNode);

        path.Reverse();
        Debug.Log($"Path retrace complete with a size of {path.Count}");
        return new Path(path.ToArray(), path.Count, endNode.gcost);
    }

    private static float HCostManhattan(int2 start, int2 end)
    {
        return math.abs(start.x - end.x) + math.abs(start.y - end.y);
    }

    private static float HCostEuclidean(int2 start, int2 end)
    {
        return math.sqrt(math.pow(start.x - end.x, 2) + math.pow(start.y - end.y, 2));
    }

    private static float HCostChebyshev(int2 start, int2 end)
    {
        return math.max(math.abs(start.x - end.x), math.abs(start.y - end.y));
    }

    private static float HCostOctile(int2 start, int2 end)
    {
        float dx = math.abs(start.x - end.x);
        float dy = math.abs(start.y - end.y);
        return dx + dy + (math.sqrt(2) - 2) * math.min(dx, dy);
    }

    private static float HCostMinkowski(int2 start, int2 end)
    {
        return math.pow(math.pow(math.abs(start.x - end.x), 3) + math.pow(math.abs(start.y - end.y), 3), 1 / 3);
    }

    private static float HCostDiagonal(int2 start, int2 end)
    {
        float dx = math.abs(start.x - end.x);
        float dy = math.abs(start.y - end.y);
        return (dx + dy) + (math.sqrt(2) - 2) * math.min(dx, dy);
    }

    private static float HCostDiagonalShort(int2 start, int2 end)
    {
        float dx = math.abs(start.x - end.x);
        float dy = math.abs(start.y - end.y);
        return (dx + dy) + (math.sqrt(2) - 1) * math.min(dx, dy);
    }

    private static float HCostDiagonalLong(int2 start, int2 end)
    {
        float dx = math.abs(start.x - end.x);
        float dy = math.abs(start.y - end.y);
        return (dx + dy) + (math.sqrt(2) - 3) * math.min(dx, dy);
    }

    private static float HCostEuclideanNoSQR(int2 start, int2 end)
    {
        return math.pow(math.pow(start.x - end.x, 2) + math.pow(start.y - end.y, 2), 1 / 2);
    }



    private static PathNode[] GetNeighbours(TilemapStruct tilemap, PathNode currentNode)
    {
        var neighbours = new List<PathNode>();

        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                int2 neighbourPos = currentNode.position + new int2(x, y);

                TileNode neighbourTile = tilemap.GetTile(neighbourPos);
                if (neighbourTile.isWalkable)
                {
                    neighbours.Add(TileNode.TileNodeToPathNode(neighbourTile));
                }
            }
        }

        return neighbours.ToArray();
    }
}