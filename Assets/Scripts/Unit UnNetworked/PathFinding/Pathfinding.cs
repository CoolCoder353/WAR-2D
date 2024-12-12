using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
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
    Distance,
}




public static class Pathfinding
{
    public static Path FindPath(TilemapStruct tilemap, int2 start, int2 end, HCostMethod hCostMethod = HCostMethod.Euclidean, int maxIterations = 1000, PathfindingVisualizer visualizer = null)
    {
        if (start.Equals(end))
        {
            return new Path(new PathNode[0], 0, 0);
        }
        //Mulitply by -1 to make it chose the lowest fcost first
        var openSet = new PriorityQueue<PathNode>(tilemap.width * tilemap.height, (x, y) => (-1 * x.fcost).CompareTo(-1 * y.fcost));

        var closedSet = new HashSet<int2>();

        //todo: Make this not a dictionary, but a native hashmap
        var validNeighbours = new Dictionary<int2, PathNode>();
        var invalidNeighbours = new HashSet<int2>();



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

            //Add the current node to the invalid neighbours list as well so we dont double lookup, this means that we may miss a faster path but it should be fine
            invalidNeighbours.Add(currentNode.position);

            if (currentNode.position.Equals(end))
            {
                return RetracePath(currentNode);
            }


            PathNode[] neighbours = GetNeighbours(tilemap, currentNode, validNeighbours, invalidNeighbours);
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
            if (visualizer != null)
            {
                UseVisualizer(visualizer, openSet, closedSet);
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
            case HCostMethod.Distance:
                return HCostDistance(start, end);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    //Calculate path using D* Lite
    [Obsolete("This method is not working correctly, use FindPath instead")]
    public static Path FindPathDLite(TilemapStruct tilemap, int2 start, int2 end, HCostMethod hCostMethod = HCostMethod.Euclidean, int maxIterations = 100000, PathfindingVisualizer visualizer = null)
    {
        if (start.Equals(end))
        {
            return new Path(new PathNode[0], 0, 0);
        }

        var openSet = new PriorityQueue<PathNode>(tilemap.width * tilemap.height, (x, y) => (x.fcost).CompareTo(y.fcost));
        var closedSet = new HashSet<int2>();
        var gScore = new Dictionary<int2, float>();
        var rhs = new Dictionary<int2, float>();
        var cameFrom = new Dictionary<int2, int2>();

        var validNeighbours = new Dictionary<int2, PathNode>();
        var invalidNeighbours = new HashSet<int2>();


        gScore[start] = float.MaxValue;
        rhs[start] = 0;
        gScore[end] = float.MaxValue;
        rhs[end] = float.MaxValue;

        openSet.Enqueue(new PathNode { position = start, hcost = CalculateHCost(start, end, hCostMethod) });

        int iterations = 0;

        while (openSet.Count > 0 && iterations < maxIterations)
        {
            iterations++;

            var current = openSet.Dequeue();

            if (closedSet.Contains(current.position))
            {
                continue;
            }

            if (current.position.Equals(end) && gScore[current.position] == rhs[current.position])
            {
                break;
            }

            closedSet.Add(current.position);



            foreach (PathNode neighbor in GetNeighbours(tilemap, current, validNeighbours, invalidNeighbours))
            {
                if (neighbor == null || closedSet.Contains(neighbor.position))
                {
                    continue;
                }

                float tentativeGScore = gScore[current.position] + CalculateHCost(current.position, neighbor.position, hCostMethod);
                if (!gScore.ContainsKey(neighbor.position) || tentativeGScore < gScore[neighbor.position])
                {
                    gScore[neighbor.position] = tentativeGScore;

                    if (!rhs.TryGetValue(neighbor.position, out float neighborRhs))
                    {
                        neighborRhs = float.MaxValue;
                    }
                    rhs[neighbor.position] = Math.Min(neighborRhs, tentativeGScore);
                    openSet.Enqueue(new PathNode { position = neighbor.position, hcost = gScore[neighbor.position] + CalculateHCost(current.position, neighbor.position, hCostMethod) });

                    cameFrom[neighbor.position] = current.position;
                }
            }
            if (visualizer != null)
            {
                UseVisualizer(visualizer, openSet, closedSet);
            }
        }

        if (iterations >= maxIterations)
        {
            Debug.LogWarning("Max iterations reached");
            return new Path(new PathNode[0], 0, 0);
        }

        if (gScore[end] == float.MaxValue)
        {
            return new Path(new PathNode[0], 0, 0);
        }

        Debug.Log($"Found path with a cost of {gScore[end]} after {iterations} iterations");
        var path = new List<PathNode>();
        int2 currentpos = end;
        while (!currentpos.Equals(start))
        {
            path.Add(new PathNode { position = currentpos });
            currentpos = cameFrom[currentpos];
        }
        path.Add(new PathNode { position = start });
        path.Reverse();

        return new Path(path.ToArray(), path.Count, gScore[end]);
    }


    private static void UseVisualizer(PathfindingVisualizer visualizer, PriorityQueue<PathNode> openSet, HashSet<int2> closedSet)
    {
        if (visualizer != null)
        {
            visualizer.SetOpenMap(openSet);
            visualizer.SetClosedMap(closedSet);
            // Delay the loop to make it easier to see the pathfinding 
            System.Threading.Thread.Sleep(visualizer.delay);
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

    private static float HCostDistance(int2 start, int2 end)
    {
        return math.distance(start, end);
    }


    private static PathNode[] GetNeighbours(TilemapStruct tilemap, PathNode currentNode, Dictionary<int2, PathNode> validNeighbours, HashSet<int2> invalidNeighbours)
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
                if (invalidNeighbours.Contains(neighbourPos))
                {
                    continue;
                }

                else if (validNeighbours.TryGetValue(neighbourPos, out PathNode neighbourChecked))
                {
                    neighbours.Add(neighbourChecked);
                }
                else
                {
                    TileNode neighbourTile = tilemap.GetTile(neighbourPos);
                    if (neighbourTile.isWalkable)
                    {
                        PathNode neighbour = TileNode.TileNodeToPathNode(neighbourTile);
                        validNeighbours.Add(neighbourPos, neighbour);
                        neighbours.Add(neighbour);
                    }
                    else
                    {
                        invalidNeighbours.Add(neighbourPos);
                    }
                }
            }
        }
        return neighbours.ToArray();
    }
}