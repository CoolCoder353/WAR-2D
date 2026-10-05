using System;
using System.Collections.Generic;
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


    public static BurstPath BurstFindPath(TilemapStruct tilemap, int2 start, int2 end, HCostMethod hCostMethod = HCostMethod.Euclidean)
    {
        BurstFindPath(ref tilemap, start.x, start.y, end.x, end.y, out BurstPath path, hCostMethod, Allocator.Temp);
        return path;
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

}