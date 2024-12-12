using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;


public class WorldStateManager : MonoBehaviour
{
    public static WorldStateManager Instance { get; private set; }

    [Tooltip("The maximum amount of tiles that can be in a chunk, side length")]
    public int maxChunkSize = 10;
    public NativeHashMap<int2, WorldChunk> world { get; private set; }

    public Grid grid;
    public Tilemap WalkableTilemap;
    public Tilemap UnwalkableTilemap;

    private BoundsInt tilemapbounds;

    public bool showTileMapweights = false;

    public Vector3 visualOffset = new Vector3(0.5f, 0.5f, 0);

    public bool DrawAllChunks = false;
    public bool DrawTiles = false;
    public int DrawChunkX = 0;
    public int DrawChunkY = 0;

    private void Awake()
    {

        int initalWorldSize = WalkableTilemap.cellBounds.size.x * WalkableTilemap.cellBounds.size.y / (maxChunkSize * maxChunkSize);

        world = new NativeHashMap<int2, WorldChunk>(initalWorldSize, Allocator.Persistent);
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
    public void OnDrawGizmos()
    {
        //Also check if the game is running
        if (showTileMapweights && Application.isPlaying)
        {
            if (DrawAllChunks)
            {
                DrawAllChunksGizmos();
            }
            else
            {
                DrawChunkGizmos(DrawChunkX, DrawChunkY);
            }
        }
    }

    private void DrawAllChunksGizmos()
    {
        foreach (KVPair<int2, WorldChunk> chunkpair in world)
        {
            DrawChunkGizmos(chunkpair.Key.x, chunkpair.Key.y);
        }
    }

    private void DrawChunkGizmos(int drawChunkX, int drawChunkY)
    {
        WorldChunk chunk = world[new int2(drawChunkX, drawChunkY)];
        if (DrawTiles && Application.isPlaying) DrawTileMap(chunk.tilemap.tiles);
        float2 worldpos = ChunkToWorld(chunk.position);


        Vector3 position = new Vector3(worldpos.x, worldpos.y, 0) + visualOffset;
        int chunkSize = maxChunkSize + 1;

        //Draw lines on the permemeter instead of a wirecube as these overlap in funny ways
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(position, new Vector3(chunkSize, chunkSize, 0));
    }

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

    private void GenerateTileMap()
    {
        BoundsInt bounds = WalkableTilemap.cellBounds;
        tilemapbounds = bounds;
        TileBase[] allTiles = WalkableTilemap.GetTilesBlock(bounds);


        int chunksWidth = Mathf.CeilToInt((float)bounds.size.x / (float)maxChunkSize);
        int chunksHeight = Mathf.CeilToInt((float)bounds.size.y / (float)maxChunkSize);

        int numberOfChunks = chunksWidth * chunksHeight;
        for (int x = 0; x < chunksWidth; x++)
        {
            for (int y = 0; y < chunksHeight; y++)
            {
                int2 chunkPosition = new int2(x, y);

                //Add 2 to the size to account for the border of the chunk
                NativeHashMap<int2, TileNode> tiles = new NativeHashMap<int2, TileNode>((maxChunkSize + 1) * (maxChunkSize + 1), Allocator.Persistent);

                //Go through all the tiles in the chunk
                for (int i = 0; i < maxChunkSize + 1; i++)
                {
                    for (int j = 0; j < maxChunkSize + 1; j++)
                    {
                        int tilex = i + x * maxChunkSize + bounds.position.x - 1;
                        int tiley = j + y * maxChunkSize + bounds.position.y - 1;
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
                    width = maxChunkSize + 1,
                    height = maxChunkSize + 1
                };

                WorldChunk chunk = new WorldChunk
                {
                    position = chunkPosition,
                    tilemap = tilemap,

                };

                world.Add(chunkPosition, chunk);
            }
        }




    }

    public int2 WorldToChunk(float2 worldPositionFloat)
    {
        int2 worldPosition = new int2(Mathf.RoundToInt(worldPositionFloat.x), Mathf.RoundToInt(worldPositionFloat.y));

        return new int2(worldPosition.x / maxChunkSize - tilemapbounds.position.x - maxChunkSize / 2, worldPosition.y / maxChunkSize - tilemapbounds.position.y - maxChunkSize / 2);
    }

    public float2 ChunkToWorld(int2 chunkPosition)
    {
        //Remove one to center the chunk, 
        return new float2(chunkPosition.x * maxChunkSize + tilemapbounds.position.x + maxChunkSize / 2f, chunkPosition.y * maxChunkSize + tilemapbounds.position.y + maxChunkSize / 2f) - new float2(1, 1);
    }
    public TileNode GetTile(int2 position)
    {
        int2 worldToChunk = WorldToChunk(position);
        return world[worldToChunk].tilemap.GetTile(position);
    }
    public void SetTile(int2 position, TileNode tile)
    {
        int2 worldToChunk = WorldToChunk(position);
        world[worldToChunk].tilemap.SetTile(position, tile);
    }


    //TODO: Change the entity to a reference to the player id the entity belongs to
    public void LockNode(int2 position, int entity = 1)
    {
        TileNode tile = GetTile(position);
        tile.used = entity;

        SetTile(position, tile);
    }

    public void UnlockNode(int2 position)
    {
        TileNode tile = GetTile(position);
        tile.used = 0;

        SetTile(position, tile);

    }

    public bool IsNodeLocked(int2 position)
    {
        TileNode tile = GetTile(position);
        return tile.isUsed;
    }


}