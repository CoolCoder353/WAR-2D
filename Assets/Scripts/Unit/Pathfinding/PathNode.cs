using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Tilemaps;
using UnityEngine;


public struct PathNode
{
    public int2 position;

    public float gcost;
    public float hcost;

    public float weight;

    public TileType tileType;

    public float fcost => (gcost * 0.01f + hcost) * weight;


    //Cloning
    public PathNode(PathNode pathNode)
    {
        position = pathNode.position;
        gcost = pathNode.gcost;
        hcost = pathNode.hcost;
        weight = pathNode.weight;
        tileType = pathNode.tileType;
    }

    public PathNode(int2 position, float gcost, float hcost, float weight, TileType tileType)
    {
        this.position = position;
        this.gcost = gcost;
        this.hcost = hcost;
        this.weight = weight;
        this.tileType = tileType;
    }


    //Comparer
    public bool Equals(PathNode pathNode)
    {
        return position.Equals(pathNode.position);
    }

}

public struct BurstPath : IDisposable
{
    public NativeArray<PathNode> path;
    public int pathLength;
    public float pathCost;

    public BurstPath(NativeArray<PathNode> path, int pathLength, float pathCost)
    {
        this.path = path;
        this.pathLength = pathLength;
        this.pathCost = pathCost;
    }

    public void Dispose()
    {
        if (path.IsCreated)
        {
            path.Dispose();
        }
    }
}
