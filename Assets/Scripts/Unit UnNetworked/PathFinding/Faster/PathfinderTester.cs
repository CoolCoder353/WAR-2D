using UnityEngine;
using NaughtyAttributes;
using System.Collections.Generic;
using Unity.Mathematics;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;

[System.Serializable]
public struct TestResult
{
    public long timeTaken;
    public Path path;

    public Vector2Int start;
    public Vector2Int end;
}

public enum PathfinderType
{
    AStar,
    DLite,
    FloodFill
}

public class PathfinderTester : MonoBehaviour
{
    public WorldStateManagerNew worldStateManager;

    public Vector2Int start;
    public Vector2Int end;

    public Dictionary<PathfinderType, List<TestResult>> results = new Dictionary<PathfinderType, List<TestResult>>();

    public bool DrawResults = false;
    public PathfinderType DrawResultsType = PathfinderType.AStar;
    public int DrawResultsIndex = 0;

    private CancellationTokenSource cancellationTokenSource;

    private void Awake()
    {
        results.Add(PathfinderType.AStar, new List<TestResult>());
        results.Add(PathfinderType.DLite, new List<TestResult>());
        results.Add(PathfinderType.FloodFill, new List<TestResult>());
    }

    private void Start()
    {
        worldStateManager = WorldStateManagerNew.Instance;
    }

    public void OnDrawGizmos()
    {
        if (DrawResults)
        {
            if (results[DrawResultsType].Count > DrawResultsIndex)
            {
                Gizmos.color = Color.red;
                Path path = results[DrawResultsType][DrawResultsIndex].path;
                for (int i = 0; i < path.pathLength - 1; i++)
                {
                    Gizmos.DrawLine(new Vector3(path.path[i].position.x, path.path[i].position.y, 0), new Vector3(path.path[i + 1].position.x, path.path[i + 1].position.y, 0));
                }
            }
        }

        Gizmos.color = Color.green;
        Gizmos.DrawCube(new Vector3(start.x, start.y, 0), Vector3.one);
        Gizmos.color = Color.blue;
        Gizmos.DrawCube(new Vector3(end.x, end.y, 0), Vector3.one);
    }

    [Button("Test A* Pathfinding")]
    public void TestAStarPathfinding()
    {
        Debug.Log("Starting A* Pathfinding test");
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = cancellationTokenSource.Token;

        Task.Run(() =>
        {
            try
            {
                // Run this in a separate thread to avoid freezing the editor
                TestResult result = TestAStarPathfindingInternal(token);

                // Done testing, add the result to the list
                results[PathfinderType.AStar].Add(result);

                Debug.Log("A* Pathfinding test complete");
                Debug.Log($"Result: Time:{result.timeTaken}ms, Path Length:{result.path.pathLength}, Start:{result.start}, End:{result.end}");
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation
                Debug.Log("Pathfinding test was canceled.");
            }
        }, token);
    }

    [Button("Test A* Pathfinding No Thread")]
    public void TestAStarPathfindingNoThread()
    {
        Debug.Log("Starting A* Pathfinding test");
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = cancellationTokenSource.Token;
        TestResult result = TestAStarPathfindingInternal(token);
        results[PathfinderType.AStar].Add(result);
        Debug.Log("A* Pathfinding test complete");
        Debug.Log($"Result: Time:{result.timeTaken}ms, Path Length:{result.path.pathLength}, Start:{result.start}, End:{result.end}");
    }




    [Button("Test D* Lite Pathfinding")]
    public void TestDListPathFinding()
    {
        Debug.Log("Starting D* Lite Pathfinding test");
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = cancellationTokenSource.Token;

        Task.Run(() =>
        {
            try
            {
                // Run this in a separate thread to avoid freezing the editor
                TestResult result = TestDLitePathfindingInternal(token);

                // Done testing, add the result to the list
                results[PathfinderType.DLite].Add(result);

                Debug.Log("D* Lite Pathfinding test complete");
                Debug.Log($"Result: Time:{result.timeTaken}ms, Path Length:{result.path.pathLength}, Start:{result.start}, End:{result.end}");
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation
                Debug.Log("Pathfinding test was canceled.");
            }
        }, token);
    }

    [Button("Test D* Lite Pathfinding No Thread")]

    public void TestDListPathFindingNoThread()
    {
        Debug.Log("Starting D* Lite Pathfinding test");
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken token = cancellationTokenSource.Token;
        TestResult result = TestDLitePathfindingInternal(token);
        results[PathfinderType.DLite].Add(result);
        Debug.Log("D* Lite Pathfinding test complete");
        Debug.Log($"Result: Time:{result.timeTaken}ms, Path Length:{result.path.pathLength}, Start:{result.start}, End:{result.end}");
    }




    [Button("Cancel Pathfinding Test")]
    public void CancelPathfindingTest()
    {
        if (cancellationTokenSource != null)
        {
            cancellationTokenSource.Cancel();
        }
    }

    [Button("Save Results in CSV")]
    public void SaveResultsInCSV()
    {
        StringBuilder csvContent = new StringBuilder();
        csvContent.AppendLine("PathType,TimeTaken,PathLength,StartX,StartY,EndX,EndY");

        foreach (var resultType in results)
        {
            foreach (var result in resultType.Value)
            {
                csvContent.AppendLine($"{resultType.Key},{result.timeTaken},{result.path.pathLength},{result.start.x},{result.start.y},{result.end.x},{result.end.y}");
            }
        }

        string filePath = System.IO.Path.Combine(Application.dataPath, "PathfindingResults.csv");
        File.WriteAllText(filePath, csvContent.ToString());

        Debug.Log($"Results saved to {filePath}");
    }

    private TestResult TestAStarPathfindingInternal(CancellationToken token)
    {
        System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        int2 intstart = new int2(start.x, start.y);
        int2 intend = new int2(end.x, end.y);

        stopwatch.Start();
        Path path = Pathfinding.FindPath(worldStateManager.world, intstart, intend, hCostMethod: HCostMethod.Distance, visualizer: PathfindingVisualizer.Instance);
        stopwatch.Stop();

        TestResult result = new TestResult();
        result.timeTaken = stopwatch.ElapsedMilliseconds;
        result.path = path;
        result.start = start;
        result.end = end;

        return result;
    }

    private TestResult TestDLitePathfindingInternal(CancellationToken token)
    {
        System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        int2 intstart = new int2(start.x, start.y);
        int2 intend = new int2(end.x, end.y);

        stopwatch.Start();
        Path path = Pathfinding.FindPathDLite(worldStateManager.world, intstart, intend, visualizer: PathfindingVisualizer.Instance);
        stopwatch.Stop();

        TestResult result = new TestResult();
        result.timeTaken = stopwatch.ElapsedMilliseconds;
        result.path = path;
        result.start = start;
        result.end = end;

        return result;
    }
}