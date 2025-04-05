using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using Mirror.BouncyCastle.Asn1.Misc;
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

    private Dictionary<int, Entity> Buildings = new Dictionary<int, Entity>();

    private EntityManager entityManager;



    private void Awake()
    {

        if (Instance == null)
        {
            Instance = this;
            GenerateTileMap();
        }
        else
        {
            Destroy(this);
        }
    }

    [Server]
    public override void OnStartServer()
    {
        entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
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

        // //TODO: THis is for debugging only
        // int numOfBuildings = entityManager.CreateEntityQuery(typeof(BuildingData)).CalculateEntityCount();
        // Debug.Log($"Found {numOfBuildings} buildings spawned on server via entities");
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


    #region Units

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
            List<int> clientBuildings = new List<int>();
            foreach (Entity entity in entitiesInBox)
            {

                //If its a unit, do the below
                if (!EntityManager.HasComponent<ClientUnit>(entity))
                {
                    //At this point, its a building so do almost the same thing but in a different list
                    if (EntityManager.HasComponent<BuildingData>(entity))
                    {
                        BuildingData buildingData = EntityManager.GetComponentData<BuildingData>(entity);
                        buildingData.position = EntityManager.GetComponentData<LocalTransform>(entity).Position.xy;

                        clientBuildings.Add(buildingData.id);

                        if (player.Key.visuableBuildings.Any(b => b.id == buildingData.id))
                        {
                            player.Key.visuableBuildings[player.Key.visuableBuildings.FindIndex(b => b.id == buildingData.id)] = buildingData;
                        }
                        else
                        {
                            player.Key.visuableBuildings.Add(buildingData);
                        }
                    }
                    continue;
                }

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

            //Remove the buildings that are not in the view anymore
            for (int i = player.Key.visuableBuildings.Count - 1; i >= 0; i--)
            {
                if (!clientBuildings.Contains(player.Key.visuableBuildings[i].id))
                {
                    player.Key.visuableBuildings.RemoveAt(i);
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
    public void AddBuilding(Entity entity, int id)
    {
        Buildings.Add(id, entity);
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

        // Debug.Log($"Found {units.Count} units in box {startcorner}, {endcorner} -> server");

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
            Debug.LogWarning($"Unit at {startInt} is already at goal {goal}");
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

            Debug.Log($"Moving unit at {startInt} to {goal} in {path.pathLength} steps");
        }
        else
        {
            Debug.LogWarning($"No path found for unit at {startInt} to {goal}");
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

        //UNITS
        var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementComponent>(), ComponentType.ReadOnly<LocalTransform>());
        NativeArray<Entity> entities = query.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> transforms = query.ToComponentDataArray<LocalTransform>(Allocator.TempJob);

        //BUILDINGS
        var buildingQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<LocalTransform>());
        NativeArray<Entity> buildingEntities = buildingQuery.ToEntityArray(Allocator.TempJob);
        NativeArray<LocalTransform> buildingTransforms = buildingQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob);


        //EVERYTHING
        NativeArray<Entity> allEntities = new NativeArray<Entity>(entities.Length + buildingEntities.Length, Allocator.TempJob);
        NativeArray<LocalTransform> allTransforms = new NativeArray<LocalTransform>(transforms.Length + buildingTransforms.Length, Allocator.TempJob);

        allEntities.Slice(0, entities.Length).CopyFrom(entities);
        allEntities.Slice(entities.Length, buildingEntities.Length).CopyFrom(buildingEntities);

        allTransforms.Slice(0, transforms.Length).CopyFrom(transforms);
        allTransforms.Slice(transforms.Length, buildingTransforms.Length).CopyFrom(buildingTransforms);

        // Debug.Log($"Checking '{entities.Length}' entities in box {startcorner}, {endcorner}");

        FindEntitiesInBoxJob job = new()
        {
            entities = allEntities,
            transforms = allTransforms,
            startcorner = startcorner,
            endcorner = endcorner,
            result = entitiesInBox
        };

        JobHandle handle = job.Schedule();
        handle.Complete();

        entities.Dispose();
        transforms.Dispose();

        buildingEntities.Dispose();
        buildingTransforms.Dispose();

        allEntities.Dispose();
        allTransforms.Dispose();

        // Ensure the entitiesInBox is disposed of properly
        NativeList<Entity> result = new NativeList<Entity>(entitiesInBox.Length, Allocator.Persistent);
        result.AddRange(entitiesInBox.AsArray());
        entitiesInBox.Dispose();

        return result;
    }

    #endregion
    #region Buildings

    [Command(requiresAuthority = false)]
    public void TryAddBuilding(int2 positon, BuildingType type, NetworkConnectionToClient sender = null)
    {
        if (CanBuildBuilding(positon, type))
        {

            BuildingData buildingData = new BuildingData
            {
                position = new float2(positon.x, positon.y),
                id = UnityEngine.Random.Range(0, int.MaxValue),
                buildingType = type,
                ownerId = BuildingData.UIntToInt(sender.identity.GetComponent<ClientPlayer>().netId)
            };

            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);

            Entity building = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(building, buildingData);

            commandBuffer.AddComponent(building, new LocalTransform { Position = new float3(buildingData.position.x, buildingData.position.y, 0) });

            switch (type)
            {
                case BuildingType.Miner:
                    ///commandBuffer.AddComponent(building, new Miner { });
                    break;
                case BuildingType.SmallUnitSpawner:
                    commandBuffer.AddComponent(building,
                    new SpawnerData
                    {
                        count = 0,
                        ownerId = buildingData.ownerId,
                        position = buildingData.position,
                        unitType = UnitType.Tank
                    });
                    break;
                default:
                    Debug.LogError("BuildingType not found in WorldStateManager");
                    break;
            }

            commandBuffer.Playback(entityManager);

            //Set the tiles the building will cover to be used
            List<int2> tiles = GetTilesBuildingWillCover(positon, type);
            foreach (int2 tile in tiles)
            {
                TileNode tileNode = world.GetTile(tile);
                tileNode.used = 1;
                world.SetTile(tile, tileNode);
            }

            AddBuilding(building, buildingData.id);
        }
    }


    [Server]
    private bool CanBuildBuilding(int2 position, BuildingType type)
    {
        List<int2> tiles = GetTilesBuildingWillCover(position, type);

        foreach (int2 tile in tiles)
        {
            if (!world.GetTile(tile).isWalkable || !IsAvaliable(tile, -1) || world.GetTile(tile).isUsed)
            {
                //Debug.LogWarning($"Cannot build building at {position} because tile {tile} is not walkable ({!world.GetTile(tile).isWalkable}), is used ({world.GetTile(tile).isUsed}) or is not avaliable ({!IsAvaliable(tile, -1)}).");
                return false;
            }
        }

        return true;
    }



    [Command(requiresAuthority = false)]
    public void RequestSpawnUnit(int spawnerId, NetworkConnectionToClient sender = null)
    {
        if (Buildings.TryGetValue(spawnerId, out Entity spawner))
        {
            SpawnerData spawnerData = EntityManager.GetComponentData<SpawnerData>(spawner);
            spawnerData.count++;
            EntityManager.SetComponentData(spawner, spawnerData);
        }
        else
        {
            Debug.LogWarning($"Spawner with id {spawnerId} not found when trying to spawn unit.");
        }
    }


    //HELPER FUNCTIONS

    [Server]
    //Gets the tiles on the tilemap that the building will cover
    public List<int2> GetTilesBuildingWillCover(int2 center, BuildingType type)
    {
        List<int2> tiles = new List<int2>();

        int2 size = GetBuildingSize(type);

        int2 tilesize = new int2(Mathf.CeilToInt(WalkableTilemap.cellSize.x), Mathf.CeilToInt(WalkableTilemap.cellSize.y));

        int numberOfTilesX = size.x / tilesize.x;
        int numberOfTilesY = size.y / tilesize.y;

        int2 start = center - new int2(numberOfTilesX / 2, numberOfTilesY / 2);

        for (int x = 0; x < numberOfTilesX; x++)
        {
            for (int y = 0; y < numberOfTilesY; y++)
            {
                tiles.Add(start + new int2(x, y));
            }
        }
        Debug.Log($"Building will cover {tiles.Count} tiles");
        return tiles;
    }

    public static int2 GetBuildingSize(BuildingType type)
    {
        //Load the building sprite from the resources
        Sprite sprite = Resources.Load<Sprite>($"{type.ToString()}");
        if (sprite == null)
        {
            Debug.LogError($"Could not find sprite for building type {type}");
            return new int2(1, 1);
        }
        //Get the size of the sprite
        return new int2(Mathf.CeilToInt(sprite.rect.width / sprite.pixelsPerUnit), Mathf.CeilToInt(sprite.rect.height / sprite.pixelsPerUnit));


    }

    #endregion

}