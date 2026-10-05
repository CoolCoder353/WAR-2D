using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>8-directional A* over the tile map. Used tiles and walls are impassable.</summary>
[BurstCompile]
public static class Pathfinding
{
    /// <summary>Returns the tiles from start to end inclusive, or an empty list when there is no path.</summary>
    public static List<int2> FindPath(TilemapStruct tilemap, int2 start, int2 end)
    {
        var result = new List<int2>();
        BurstFindPath(ref tilemap, start.x, start.y, end.x, end.y, out BurstPath path);
        try
        {
            for (int i = 0; i < path.pathLength; i++) result.Add(path.path[i].position);
        }
        finally
        {
            path.Dispose();
        }
        return result;
    }

    [BurstCompile]
    private static void BurstFindPath(ref TilemapStruct tilemap, int startx, int starty, int endx, int endy, out BurstPath path)
    {
        int2 start = new int2(startx, starty);
        int2 end = new int2(endx, endy);
        path = default;

        TileNode goal = tilemap.GetTile(end);
        if (start.Equals(end) || !goal.isWalkable || goal.isUsed) return;

        var openSet = new NativePriorityQueue(64, Allocator.Temp);
        var closedSet = new NativeHashSet<int2>(256, Allocator.Temp);
        var connections = new NativeHashMap<int2, PathNode>(256, Allocator.Temp);
        var validNeighbours = new NativeHashMap<int2, PathNode>(256, Allocator.Temp);
        var neighbours = new NativeList<PathNode>(8, Allocator.Temp);
        int maxExpansions = math.max(1, tilemap.width * tilemap.height);
        int expansions = 0;

        openSet.Enqueue(new PathNode { position = start, gcost = 0, hcost = 0, weight = 1 });

        while (openSet.Length > 0 && expansions++ < maxExpansions)
        {
            PathNode current = openSet.Dequeue();
            if (!closedSet.Add(current.position)) continue;

            if (current.position.Equals(end))
            {
                RetracePath(ref current, ref connections, out path);
                break;
            }

            GetNeighbours(ref tilemap, ref current, ref validNeighbours, ref closedSet, ref neighbours);
            foreach (PathNode neighbour in neighbours)
            {
                if (closedSet.Contains(neighbour.position) || neighbour.weight <= 0) continue;
                if (openSet.Contains(neighbour)) continue;

                var node = new PathNode(neighbour)
                {
                    gcost = current.gcost + math.distance(current.position, neighbour.position),
                    hcost = Octile(neighbour.position, end),
                };
                connections[node.position] = current;
                openSet.Enqueue(node);
            }
        }

        openSet.Dispose();
        closedSet.Dispose();
        connections.Dispose();
        validNeighbours.Dispose();
        neighbours.Dispose();
    }

    private static float Octile(int2 a, int2 b)
    {
        float dx = math.abs(a.x - b.x);
        float dy = math.abs(a.y - b.y);
        return dx + dy + (math.SQRT2 - 2f) * math.min(dx, dy);
    }

    private static void RetracePath(ref PathNode endNode, ref NativeHashMap<int2, PathNode> connections, out BurstPath path)
    {
        var reversed = new NativeList<PathNode>(64, Allocator.Temp);
        PathNode current = endNode;
        while (connections.ContainsKey(current.position))
        {
            reversed.Add(current);
            current = connections[current.position];
        }
        reversed.Add(current);

        var ordered = new NativeArray<PathNode>(reversed.Length, Allocator.Temp);
        for (int i = 0; i < reversed.Length; i++) ordered[i] = reversed[reversed.Length - 1 - i];
        path = new BurstPath(ordered, reversed.Length, endNode.gcost);
        reversed.Dispose();
    }

    private static void GetNeighbours(ref TilemapStruct tilemap, ref PathNode current, ref NativeHashMap<int2, PathNode> validNeighbours, ref NativeHashSet<int2> closedSet, ref NativeList<PathNode> neighbours)
    {
        neighbours.Clear();
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0) continue;
                int2 pos = current.position + new int2(x, y);
                if (closedSet.Contains(pos)) continue;
                if (validNeighbours.TryGetValue(pos, out PathNode cached))
                {
                    neighbours.Add(cached);
                    continue;
                }
                TileNode tile = tilemap.GetTile(pos);
                if (tile.isWalkable && !tile.isUsed)
                {
                    PathNode node = TileNode.TileNodeToPathNode(tile);
                    validNeighbours.Add(pos, node);
                    neighbours.Add(node);
                }
            }
        }
    }
}
