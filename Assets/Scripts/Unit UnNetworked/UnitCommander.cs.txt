using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

public class UnitCommander : MonoBehaviour
{
    public static UnitCommander Instance { get; private set; }
    public NativeList<Entity> SelectedUnits = new(Allocator.Persistent);
    private EntityManager EntityManager => World.DefaultGameObjectInjectionWorld.EntityManager;
    private int2 startcorner;
    private int2 endcorner;

    public GameObject selectionBox;

    public bool DisplayLastPath = false;
    private Path lastPath;

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

    private void OnDrawGizmos()
    {
        if (DisplayLastPath && lastPath.pathLength != 0 && Application.isPlaying)
        {
            foreach (PathNode node in lastPath.path)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireCube(new Vector3(node.position.x + 0.5f, node.position.y + 0.5f, 0), new Vector3(1, 1, 0));
            }
        }
    }

    private Vector3 GetMouseWorldPosition()
    {
        Vector3 mousePosition = Input.mousePosition;

        Vector3 truePosition = new Vector3(Camera.main.pixelWidth - mousePosition.x, Camera.main.pixelHeight - mousePosition.y, Camera.main.transform.position.z);

        Vector3 worldPosition = Camera.main.ScreenToWorldPoint(truePosition);

        worldPosition.z = 0;
        return worldPosition;
    }
    public void Update()
    {
        //Mouse down, start selection
        if (Input.GetMouseButtonDown(0))
        {
            selectionBox.SetActive(true);
            Vector3 worldPosition = GetMouseWorldPosition();
            startcorner = new int2((int)worldPosition.x, (int)worldPosition.y);
        }

        if (Input.GetMouseButton(0))
        {
            Vector3 worldPosition = GetMouseWorldPosition();
            Vector3 startPosition = new Vector3(startcorner.x, startcorner.y, 0);
            Vector3 center = (worldPosition + startPosition) / 2;
            selectionBox.transform.position = center;
            Vector3 size = new Vector3(Mathf.Abs(worldPosition.x - startPosition.x), Mathf.Abs(worldPosition.y - startPosition.y), 1);
            selectionBox.transform.localScale = size;

        }

        //Mouse up, end selection 
        if (Input.GetMouseButtonUp(0))
        {
            selectionBox.SetActive(false);
            Vector3 worldPosition = GetMouseWorldPosition();
            endcorner = new int2((int)worldPosition.x, (int)worldPosition.y);

            SelectedUnits = FindEntitiesInBox(startcorner, endcorner);
        }

        if (Input.GetMouseButtonDown(1))
        {
            Vector3 worldPosition = GetMouseWorldPosition();
            int2 goal = new int2((int)worldPosition.x, (int)worldPosition.y);
            Debug.Log($"Moving {SelectedUnits.Length} units to {goal.x},{goal.y}");
            foreach (Entity entity in SelectedUnits)
            {
                MoveUnit(entity, goal);
            }
        }
    }
    //This will need to be burst compiled and run through the job system
    public NativeList<Entity> FindEntitiesInBox(int2 startcorner, int2 endcorner)
    {

        NativeList<Entity> entitiesInBox = FindEntitiesInBoxJobMethod(startcorner, endcorner);
        return entitiesInBox;
    }
    public void MoveUnit(Entity entity, int2 goal)
    {
        LocalTransform localTransform = EntityManager.GetComponentData<LocalTransform>(entity);
        float2 start = localTransform.Position.xy;
        int2 startInt = new((int)start.x, (int)start.y);

        if (startInt.Equals(goal))
        {
            return;
        }

        Path path = Pathfinding.FindPath(WorldStateManager.Instance.tilemap, startInt, goal);
        lastPath = path;
        if (path.pathLength > 0)
        {

            //Unlock any nodes the unit has locked so that we dont perma lock them
            MovementComponent movementComponent = EntityManager.GetComponentData<MovementComponent>(entity);
            if (movementComponent.hasSavedPositionOne)
            {
                WorldStateManager.Instance.UnlockNode(movementComponent.savedPositionOne);
            }
            if (movementComponent.hasSavedPositionTwo)
            {
                WorldStateManager.Instance.UnlockNode(movementComponent.savedPositionTwo);
            }

            //Save the path to the entity to be used by the movement system
            DynamicBuffer<PathPoint> pathBuffer = EntityManager.GetBuffer<PathPoint>(entity);
            pathBuffer.Clear();
            foreach (PathNode node in path.path)
            {
                pathBuffer.Add(new PathPoint { position = node.position });
            }
        }

    }

    [BurstCompile]
    public struct FindEntitiesInBoxJob : IJob
    {
        [ReadOnly] public NativeArray<Entity> entities;
        [ReadOnly] public NativeArray<LocalTransform> transforms;
        public int2 startcorner;
        public int2 endcorner;
        public NativeList<Entity> result;

        public void Execute()
        {
            int2 minCorner = math.min(startcorner, endcorner);
            int2 maxCorner = math.max(startcorner, endcorner);

            for (int i = 0; i < entities.Length; i++)
            {
                if (transforms[i].Position.x >= minCorner.x && transforms[i].Position.x <= maxCorner.x &&
                    transforms[i].Position.y >= minCorner.y && transforms[i].Position.y <= maxCorner.y)
                {
                    result.Add(entities[i]);
                }
            }
        }
    }

    public NativeList<Entity> FindEntitiesInBoxJobMethod(int2 startcorner, int2 endcorner)
    {
        NativeList<Entity> entitiesInBox = new(Allocator.TempJob);

        var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementComponent>(), ComponentType.ReadOnly<LocalTransform>());
        NativeArray<Entity> entities = query.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> transforms = query.ToComponentDataArray<LocalTransform>(Allocator.TempJob);
        FindEntitiesInBoxJob job = new()
        {
            entities = entities,
            transforms = transforms,
            startcorner = startcorner,
            endcorner = endcorner,
            result = entitiesInBox
        };

        JobHandle handle = job.Schedule();
        handle.Complete();

        entities.Dispose();
        transforms.Dispose();

        // Ensure the entitiesInBox is disposed of properly
        NativeList<Entity> result = new NativeList<Entity>(entitiesInBox.Length, Allocator.Persistent);
        result.AddRange(entitiesInBox.AsArray());
        entitiesInBox.Dispose();

        return result;
    }
}