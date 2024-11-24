using System;
using System.Collections.Generic;
using Unity.Mathematics;


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

        var openSet = new PriorityQueue<PathNode>(tilemap.width * tilemap.height, (x, y) => x.fcost.CompareTo(y.fcost));
        var closedSet = new HashSet<int2>();

        var startNode = new PathNode
        {
            position = start,
            gcost = 0,
            hcost = 0,
            parent = new PathNodeLink { parent = null, pathNode = new PathNode { position = start, gcost = 0, hcost = 0 }, depth = 1 }

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
            foreach (PathNode pathNode in neighbours)
            {
                if (closedSet.Contains(pathNode.position) || pathNode.position.Equals(currentNode.position) || pathNode.weight <= 0)
                {
                    continue;
                }

                float newHCost = 0;
                float newGCost = currentNode.gcost + math.distance(currentNode.position, pathNode.position);
                switch (hCostMethod)
                {
                    case HCostMethod.Manhattan:

                        newHCost = HCostManhattan(pathNode.position, end);
                        break;
                    case HCostMethod.Euclidean:
                        newHCost = HCostEuclidean(pathNode.position, end);
                        break;
                    case HCostMethod.Chebyshev:
                        newHCost = HCostChebyshev(pathNode.position, end);
                        break;
                    case HCostMethod.Octile:
                        newHCost = HCostOctile(pathNode.position, end);
                        break;
                    case HCostMethod.Minkowski:
                        newHCost = HCostMinkowski(pathNode.position, end);
                        break;
                    case HCostMethod.Diagonal:
                        newHCost = HCostDiagonal(pathNode.position, end);
                        break;
                    case HCostMethod.DiagonalShort:
                        newHCost = HCostDiagonalShort(pathNode.position, end);
                        break;
                    case HCostMethod.DiagonalLong:
                        newHCost = HCostDiagonalLong(pathNode.position, end);
                        break;
                    case HCostMethod.EuclideanNoSQR:
                        newHCost = HCostEuclideanNoSQR(pathNode.position, end);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                PathNode newPathNode = new PathNode(pathNode);
                newPathNode.gcost = newGCost;
                newPathNode.hcost = newHCost;
                newPathNode.parent = new PathNodeLink { parent = currentNode.parent, pathNode = pathNode, depth = currentNode.parent.depth + 1 };

                //We may have repeats of the same node, but with different costs which doesnt really matter as we are using a priority queue
                //so the node with the lowest cost will be the first to be dequeued
                openSet.Enqueue(newPathNode);
            }

        }
        return new Path(new PathNode[0], 0, 0);
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

    private static Path RetracePath(PathNode endNode)
    {
        PathNode[] path = new PathNode[endNode.parent.depth];
        while (endNode.parent.parent != null)
        {
            path[endNode.parent.depth - 1] = endNode;
            endNode = endNode.parent.pathNode;
        }
        return new Path(path, path.Length, endNode.gcost);
    }
}