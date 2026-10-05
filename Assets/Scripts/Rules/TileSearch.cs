using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>Bounded breadth-first search for the nearest tile matching a predicate.</summary>
public static class TileSearch
{
    public static bool FindNearest(int2 origin, Func<int2, bool> isFree, Func<int2, bool> canTraverse, int maxVisited, out int2 found)
    {
        var queue = new Queue<int2>();
        var visited = new HashSet<int2> { origin };
        queue.Enqueue(origin);

        while (queue.Count > 0 && visited.Count <= maxVisited)
        {
            int2 current = queue.Dequeue();
            if (isFree(current))
            {
                found = current;
                return true;
            }

            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0) continue;
                    int2 neighbour = current + new int2(x, y);
                    if (canTraverse(neighbour) && visited.Add(neighbour)) queue.Enqueue(neighbour);
                }
            }
        }

        found = default;
        return false;
    }
}
