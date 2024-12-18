using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Burst;
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

[BurstCompile]
public static class Pathfinding
{


    public static BurstPath BurstFindPath(TilemapStruct tilemap, int2 start, int2 end, HCostMethod hCostMethod = HCostMethod.Euclidean, bool doJob = true)
    {
        if (doJob)
        {
            Task.Run(() =>
                    {
                        BurstFindPath(ref tilemap, start.x, start.y, end.x, end.y, out BurstPath path, hCostMethod, Allocator.TempJob);
                        return path;
                    });

            return new BurstPath(new NativeArray<PathNode>(0, Allocator.Persistent), 0, 0);
        }
        else
        {
            BurstFindPath(ref tilemap, start.x, start.y, end.x, end.y, out BurstPath path, hCostMethod, Allocator.Temp);
            return path;
        }

    }

    [BurstCompile]
    public static void BurstFindPath(ref TilemapStruct tilemap, int startx, int starty, int endx, int endy, out BurstPath path, HCostMethod hCostMethod = HCostMethod.Euclidean, Allocator allocator = Allocator.Temp)
    {
        int2 start = new int2(startx, starty);
        int2 end = new int2(endx, endy);
        path = new BurstPath(new NativeArray<PathNode>(0, Allocator.Persistent), 0, 0);
        // Check if the start and end are the same
        if (start.Equals(end))
        {
            return;
        }
        //Random starting size for the open set, factor of 2 is a good starting point, closed set should be bigger ... because it should be.
        NativePriorityQueue openSet = new NativePriorityQueue(32, allocator);
        NativeHashSet<int2> closedSet = new NativeHashSet<int2>(128, allocator);

        NativeHashMap<int2, PathNode> connections = new NativeHashMap<int2, PathNode>(32, allocator);
        NativeHashMap<int2, PathNode> validNeighbours = new NativeHashMap<int2, PathNode>(64, allocator);

        PathNode startNode = new PathNode()
        {
            position = start,
            gcost = 0,
            hcost = 0,
        };
        openSet.Enqueue(startNode);

        while (openSet.Length > 0)
        {
            PathNode currentNode = openSet.Dequeue();
            closedSet.Add(currentNode.position);

            if (currentNode.position.Equals(end))
            {
                BurstRetracePath(ref currentNode, ref connections, out path, allocator);
                openSet.Dispose();
                closedSet.Dispose();
                connections.Dispose();
                validNeighbours.Dispose();
                return;
            }

            NativeList<PathNode> neighbours = new NativeList<PathNode>(allocator);
            BurstGetNeighbours(ref tilemap, ref currentNode, ref validNeighbours, ref closedSet, ref neighbours);
            // NOTE: There is potential for a neighbour to be null, this should be accounted for but hasn't been yet


            foreach (PathNode neighbour in neighbours)
            {
                if (closedSet.Contains(neighbour.position) || openSet.Contains(neighbour) || neighbour.position.Equals(currentNode.position) || neighbour.weight <= 0)
                {
                    continue;
                }

                float newGCost = currentNode.gcost + math.distance(currentNode.position, neighbour.position);
                float newHCost = CalculateHCost(neighbour.position, end, hCostMethod);

                PathNode newPathNode = new PathNode(neighbour)
                {
                    gcost = newGCost,
                    hcost = newHCost,
                };
                connections[newPathNode.position] = currentNode;
                openSet.Enqueue(newPathNode);
            }
            neighbours.Dispose();

        }

        openSet.Dispose();
        closedSet.Dispose();
        connections.Dispose();
        validNeighbours.Dispose();

        return;
    }

    public static Path FindPath(TilemapStruct tilemap, int2 start, int2 end, HCostMethod hCostMethod = HCostMethod.Euclidean, int maxIterations = 1000)
    {
        if (start.Equals(end))
        {
            return new Path(new PathNode[0], 0, 0);
        }
        // Multiply by -1 to make it choose the lowest fcost first
        var openSet = new PriorityQueue<PathNode>(tilemap.width * tilemap.height, (x, y) => (-1 * x.fcost).CompareTo(-1 * y.fcost));

        var closedSet = new HashSet<int2>();

        // Where the key is the current node and the value is the parent node
        var connections = new NativeHashMap<int2, PathNode>(30, Allocator.TempJob);

        var validNeighbours = new NativeHashMap<int2, PathNode>(100, Allocator.TempJob);
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

            // Add the current node to the invalid neighbours list as well so we don't double lookup, this means that we may miss a faster path but it should be fine
            invalidNeighbours.Add(currentNode.position);

            if (currentNode.position.Equals(end))
            {
                return RetracePath(currentNode, connections);
            }

            PathNode[] neighbours = GetNeighbours(tilemap, currentNode, validNeighbours, invalidNeighbours);
            foreach (PathNode neighbour in neighbours)
            {
                if (closedSet.Contains(neighbour.position) || neighbour.position.Equals(currentNode.position) || neighbour.weight <= 0)
                {
                    continue;
                }

                float newGCost = currentNode.gcost + math.distance(currentNode.position, neighbour.position);
                float newHCost = CalculateHCost(neighbour.position, end, hCostMethod);

                PathNode newPathNode = new PathNode(neighbour)
                {
                    gcost = newGCost,
                    hcost = newHCost,
                };
                connections[newPathNode.position] = currentNode;
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
            case HCostMethod.Distance:
                return HCostDistance(start, end);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    [BurstCompile]
    private static void BurstRetracePath(ref PathNode endNode, ref NativeHashMap<int2, PathNode> connections, out BurstPath path, Allocator allocator = Allocator.Temp)
    {
        NativeList<PathNode> pathList = new NativeList<PathNode>(allocator);
        PathNode currentNode = endNode;

        while (connections.ContainsKey(currentNode.position))
        {
            pathList.Add(currentNode);
            currentNode = connections[currentNode.position];
        }

        // Add the start node
        pathList.Add(currentNode);

        // Reverse the path
        NativeArray<PathNode> reversedPath = new NativeArray<PathNode>(pathList.Length, Allocator.Persistent);
        for (int i = 0; i < pathList.Length; i++)
        {
            reversedPath[i] = pathList[pathList.Length - 1 - i];
        }

        path = new BurstPath(reversedPath, pathList.Length, endNode.gcost);
        pathList.Dispose();
    }

    private static Path RetracePath(PathNode endNode, NativeHashMap<int2, PathNode> connections)
    {
        List<PathNode> path = new List<PathNode>();
        PathNode currentNode = endNode;

        Debug.Log($"Retracing path from {endNode.position}");
        while (connections.ContainsKey(currentNode.position))
        {
            path.Add(currentNode);
            currentNode = connections[currentNode.position];
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

    [BurstCompile]
    private static void BurstGetNeighbours(ref TilemapStruct tilemap, ref PathNode currentNode, ref NativeHashMap<int2, PathNode> validNeighbours, ref NativeHashSet<int2> invalidNeighbours, ref NativeList<PathNode> neighbours)
    {
        neighbours.Clear();

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

                if (validNeighbours.TryGetValue(neighbourPos, out PathNode neighbourChecked))
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
    }

    private static PathNode[] GetNeighbours(TilemapStruct tilemap, PathNode currentNode, NativeHashMap<int2, PathNode> validNeighbours, HashSet<int2> invalidNeighbours)
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