using System;
using System.Collections.Generic;
using System.Threading;
using NaughtyAttributes;
using Unity.Collections;
using Unity.Collections.NotBurstCompatible;
using Unity.Mathematics;
using UnityEngine;

public class PathfindingVisualizer : MonoBehaviour
{
    public static PathfindingVisualizer Instance { get; private set; } = null;

    public bool showTileMapweights = false;

    public Vector3 visualOffset = new Vector3(0.5f, 0.5f, 0);

    private PriorityQueue<PathNode> lastOpenSet;
    private HashSet<int2> lastClosedSet;

    [Tooltip("The delay in ms between each pathfinding step")]
    public int delay = 10;
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(this);
        }
    }

    public void OnDrawGizmos()
    {
        // Also check if the game is running
        if (showTileMapweights && Application.isPlaying && lastClosedSet != null && lastOpenSet.Count != 0)
        {
            DrawClosedMap(lastClosedSet);
            DrawOpenMap(lastOpenSet);
        }
    }

    public void DrawClosedMap(HashSet<int2> closedSet)
    {
        // Make sure we clone the closed set so we can draw it, we don't want to modify the original
        closedSet = new HashSet<int2>(closedSet);

        foreach (int2 tile in closedSet)
        {
            Vector3 position = new Vector3(tile.x, tile.y, 0) + visualOffset;
            Gizmos.color = Color.red;
            Gizmos.DrawCube(position, Vector3.one);
        }
    }

    public void SetOpenMap(PriorityQueue<PathNode> openSet)
    {
        if (openSet.Count == 0)
        {
            return;
        }
        // Make sure we clone the open set so we can draw it, we don't want to modify the original
        openSet = new PriorityQueue<PathNode>(openSet);

        lastOpenSet = openSet;
    }



    public void SetClosedMap(HashSet<int2> closedSet)
    {
        if (closedSet.Count == 0)
        {
            return;
        }
        // Make sure we clone the closed set so we can draw it, we don't want to modify the original
        closedSet = new HashSet<int2>(closedSet);

        lastClosedSet = closedSet;
    }

    public void DrawOpenMap(PriorityQueue<PathNode> openSet)
    {
        // Make sure we clone the open set so we can draw it, we don't want to modify the original
        openSet = new PriorityQueue<PathNode>(openSet);

        List<PathNode> tempList = new List<PathNode>();

        while (openSet.Count > 0)
        {
            PathNode tile = openSet.Dequeue();
            tempList.Add(tile);
            Vector3 position = new Vector3(tile.position.x, tile.position.y, 0) + visualOffset;
            Gizmos.color = Color.green;
            Gizmos.DrawCube(position, Vector3.one);
        }

        // Restore the original openSet
        foreach (var node in tempList)
        {
            openSet.Enqueue(node);
        }

    }

    public void BurstSetOpenMap(NativePriorityQueue openSet)
    {
        //Convert to a ordinary priority queue
        PriorityQueue<PathNode> priorityQueue = new PriorityQueue<PathNode>();
        while (openSet.Length > 0)
        {
            PathNode pathNode = openSet.Dequeue();
            priorityQueue.Enqueue(pathNode);
        }

        SetOpenMap(priorityQueue);

    }

    public void BurstSetClosedMap(NativeHashSet<int2> closedSet)
    {
        HashSet<int2> closedHashSet = new HashSet<int2>();
        NativeArray<int2> closedArray = closedSet.ToNativeArray(Allocator.Temp);
        for (int i = 0; i < closedArray.Length; i++)
        {
            closedHashSet.Add(closedArray[i]);
        }

        SetClosedMap(closedHashSet);
    }
}