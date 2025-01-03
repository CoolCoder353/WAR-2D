using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Tilemaps;

[BurstCompile]
public class WorldStateManager : NetworkBehaviour
{
    public static WorldStateManager Instance { get; private set; }

    public TilemapStruct world { get; private set; }

    private EntityManager EntityManager => World.DefaultGameObjectInjectionWorld.EntityManager;


    public Tilemap WalkableTilemap;
    public Tilemap UnwalkableTilemap;

    public bool showTileMapweights = false;

    public Vector3 visualOffset = new Vector3(0.5f, 0.5f, 0);

    private Dictionary<ClientPlayer, (int2, int2)> playerView = new Dictionary<ClientPlayer, (int2, int2)>();

    private List<(int, int2)> unitPositions = new List<(int, int2)>();


    private Dictionary<int, Entity> Units = new Dictionary<int, Entity>();

    private void Awake()
    {

        if (Instance == null)
        {
            Debug.LogWarning("WorldStateManager is null. Setting ourself as the instance.");
            Instance = this;
            GenerateTileMap();
        }
        else
        {
            Destroy(this);
        }
    }

    [ServerCallback]
    public void OnDrawGizmos()
    {
        //Also check if the game is running
        if (showTileMapweights && Application.isPlaying)
        {
            DrawTileMap(world.tiles);
        }
    }



    [ServerCallback]
    public void FixedUpdate()
    {
        UpdatePlayerViews();
    }

    [Server]
    private void DrawTileMap(NativeHashMap<int2, TileNode> tilemap)
    {
        foreach (KVPair<int2, TileNode> tilepair in tilemap)
        {
            TileNode tile = tilepair.Value;
            Vector3 position = new Vector3(tile.position.x, tile.position.y, 0) + visualOffset;
            Gizmos.color = Color.Lerp(Color.white, Color.black, tile.weight / 10f);
            Gizmos.DrawCube(position, Vector3.one);
        }
    }

    [ServerCallback]
    private void GenerateTileMap()
    {
        BoundsInt bounds = WalkableTilemap.cellBounds;
        //Add 2 to the size to account for the border of the chunk
        NativeHashMap<int2, TileNode> tiles = new NativeHashMap<int2, TileNode>((bounds.size.x + 1) * (bounds.size.y + 1), Allocator.Persistent);

        //Go through all the tiles in the chunk
        for (int i = 0; i < bounds.size.x + 1; i++)
        {
            for (int j = 0; j < bounds.size.y + 1; j++)
            {
                int tilex = i + bounds.position.x - 1;
                int tiley = j + bounds.position.y - 1;
                Vector3Int localPlace = new Vector3Int(tilex, tiley, 0);
                Vector3 worldPosition = WalkableTilemap.CellToWorld(localPlace);
                int2 worldPlace = new int2((int)worldPosition.x, (int)worldPosition.y);

                int weight = 1;

                if (UnwalkableTilemap.GetTile(localPlace) != null)
                {
                    weight = 0;
                }

                TileNode tileNode = new TileNode
                {
                    position = worldPlace,
                    weight = weight,
                    used = 0
                };

                tiles[worldPlace] = tileNode;
            }
        }

        TilemapStruct tilemap = new TilemapStruct
        {
            tiles = tiles,
            width = bounds.size.x + 1,
            height = bounds.size.y + 1
        };


        world = tilemap;
    }


    [Server]
    public TileNode GetTile(int2 position)
    {
        return world.GetTile(position);
    }
    [Server]
    public void SetTile(int2 position, TileNode tile)
    {
        world.SetTile(position, tile);
    }


    //<section>Units

    [Command(requiresAuthority = false)]
    public void UpdateClientView(int2 startcorner, int2 endcorner, NetworkConnectionToClient sender = null)
    {
        playerView[sender.identity.GetComponent<ClientPlayer>()] = (startcorner, endcorner);

    }


    [Server, BurstCompile]
    public void UpdatePlayerViews()
    {
        foreach (KeyValuePair<ClientPlayer, (int2, int2)> player in playerView)
        {
            int2 startcorner = player.Value.Item1;
            int2 endcorner = player.Value.Item2;

            NativeList<Entity> entitiesInBox = FindEntitiesInBox(startcorner, endcorner);


            // Debug.Log($"For player {player.Key.nickname} found {entitiesInBox.Length} entities in box {startcorner}, {endcorner}");
            List<int> clientUnits = new List<int>();
            foreach (Entity entity in entitiesInBox)
            {
                ClientUnit clientUnit = EntityManager.GetComponentData<ClientUnit>(entity);
                clientUnit.position = EntityManager.GetComponentData<LocalTransform>(entity).Position.xy;

                clientUnits.Add(clientUnit.id);

                if (player.Key.visuableUnits.Any(u => u.id == clientUnit.id))
                {
                    //Update the position of the unit

                    // Debug.Log($"Sending the following unit to the client: '{player.Key.nickname}' with unit: id '{clientUnit.id}', position '{clientUnit.position}' and sprite '{clientUnit.spriteName}'");

                    player.Key.visuableUnits[player.Key.visuableUnits.FindIndex(u => u.id == clientUnit.id)] = clientUnit;
                }
                else
                {
                    player.Key.visuableUnits.Add(clientUnit);
                }



            }
            entitiesInBox.Dispose();

            //TODO: This is not the most effecient way to do this
            //Remove the units that are not in the view anymore
            for (int i = player.Key.visuableUnits.Count - 1; i >= 0; i--)
            {
                if (!clientUnits.Contains(player.Key.visuableUnits[i].id))
                {
                    player.Key.visuableUnits.RemoveAt(i);
                }
            }
        }
    }

    [Server]
    public void AddUnit(Entity entity, int id)
    {
        Units.Add(id, entity);
    }

    [Server]
    public bool IsAvaliable(int2 position, int id)
    {
        if (unitPositions.Any(u => u.Item2.Equals(position) && u.Item1 != id))
        {
            return false;
        }
        return true;
    }

    [Server]
    public void ClaimLocation(int2 position, int id)
    {
        Debug.Log($"Claiming location {position} for unit {id}");
        if (!IsAvaliable(position, id))
        {
            Debug.LogWarning($"Unit with id {id} tried to claim a location that is already taken.");
            return;
        }
        //Ignore if it is already claimed
        if (unitPositions.Any(u => u.Item2.Equals(position) && u.Item1 == id))
        {
            return;
        }
        unitPositions.Add((id, position));
    }

    [Server]
    public void ReleaseLocation(int2 position, int id)
    {
        Debug.Log($"Releasing location {position} for unit {id}");
        if (!unitPositions.Any(u => u.Item2.Equals(position) && u.Item1 == id))
        {
            Debug.LogWarning($"Unit with id {id} tried to release a location that is not taken or owned by that unit.");
            return;
        }
        unitPositions.Remove((id, position));
    }

    [Server]
    public void ReleaseAllLocations(int id)
    {
        unitPositions.RemoveAll(u => u.Item1 == id);
    }

    //TODO: Change this to be more effecient, we are going through all the units TWICE!
    [Command(requiresAuthority = false)]
    public void CmdMoveUnits(int2 goal, int2 startcorner, int2 endcorner)
    {
        // Debug.Log("Moving units at server");
        List<ClientUnit> units = new List<ClientUnit>();
        List<int2> setGoals = new List<int2>();

        NativeList<Entity> entitiesInBox = FindEntitiesInBox(startcorner, endcorner);
        foreach (Entity entity in entitiesInBox)
        {
            ClientUnit clientUnit = EntityManager.GetComponentData<ClientUnit>(entity);
            clientUnit.position = EntityManager.GetComponentData<LocalTransform>(entity).Position.xy;

            units.Add(clientUnit);
        }

        foreach (ClientUnit unit in units)
        {
            //TODO: Make sure we are not moving units that are not owned by the player

            if (Units.TryGetValue(unit.id, out Entity entity))
            {
                //TODO: Set the goal to the closest walkable tile we havent claimed yet.
                //We can do this through a bredth first search from the goal to the unit, stopping at the first walkable tile that is not claimed

                int2 specificgoal = FindBestGoalLocation(goal, unit.id, setGoals);
                setGoals.Add(specificgoal);

                //Make sure the unit doesnt own any location
                ReleaseAllLocations(unit.id);

                MoveUnit(entity, specificgoal);
            }
            else
            {
                Debug.LogWarning($"Unit with id {unit.id} not found when trying to move units.");
            }
        }
        entitiesInBox.Dispose();
    }


    [Server]
    private int2 FindBestGoalLocation(int2 goal, int id, List<int2> setGoals)
    {

        int2 bestGoal = goal;

        // Perform a breadth-first search to find the closest walkable tile that is not claimed
        Queue<int2> queue = new Queue<int2>();
        HashSet<int2> visited = new HashSet<int2>();
        queue.Enqueue(goal);
        visited.Add(goal);

        while (queue.Count > 0)
        {
            int2 current = queue.Dequeue();
            visited.Add(current);
            // Check if the current location is available and not already set as a goal
            if (!setGoals.Contains(current) && world.GetTile(current).isWalkable && IsAvaliable(current, id))
            {
                bestGoal = current;
                break;
            }

            // Add neighboring tiles to the queue
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0) continue;

                    int2 neighbor = new int2(current.x + x, current.y + y);
                    if (!visited.Contains(neighbor) && world.GetTile(neighbor).isWalkable)
                    {
                        queue.Enqueue(neighbor);

                    }
                }
            }
        }

        return bestGoal;
    }

    //This will need to be burst compiled and run through the job system
    [Server]
    private NativeList<Entity> FindEntitiesInBox(int2 startcorner, int2 endcorner)
    {
        NativeList<Entity> entitiesInBox = FindEntitiesInBoxJobMethod(startcorner, endcorner);
        return entitiesInBox;
    }
    [Server]
    private void MoveUnit(Entity entity, int2 goal)
    {
        LocalTransform localTransform = EntityManager.GetComponentData<LocalTransform>(entity);
        float2 start = localTransform.Position.xy;
        int2 startInt = new((int)start.x, (int)start.y);

        if (startInt.Equals(goal))
        {
            return;
        }

        Path path = Path.BurstToPath(Pathfinding.BurstFindPath(WorldStateManager.Instance.world, startInt, goal, doJob: false));


        if (path.pathLength > 0)
        {

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
        [Unity.Collections.ReadOnly] public NativeArray<Entity> entities;
        [Unity.Collections.ReadOnly] public NativeArray<LocalTransform> transforms;
        public int2 startcorner;
        public int2 endcorner;
        public NativeList<Entity> result;

        public void Execute()
        {

            // Debug.Log($"Checking '{entities.Length}' entities in box {startcorner}, {endcorner}");
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
    [Server]
    public NativeList<Entity> FindEntitiesInBoxJobMethod(int2 startcorner, int2 endcorner)
    {
        NativeList<Entity> entitiesInBox = new(Allocator.TempJob);

        var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementComponent>(), ComponentType.ReadOnly<LocalTransform>());
        NativeArray<Entity> entities = query.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> transforms = query.ToComponentDataArray<LocalTransform>(Allocator.TempJob);

        // Debug.Log($"Checking '{entities.Length}' entities in box {startcorner}, {endcorner}");

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