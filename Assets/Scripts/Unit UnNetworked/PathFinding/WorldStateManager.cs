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
    public TilemapStruct world { get; private set; }

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
            DrawTileMap(world.tiles);
        }
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

        //Add 2 to the size to account for the border of the chunk
        NativeHashMap<int2, TileNode> tiles = new NativeHashMap<int2, TileNode>((maxChunkSize + 1) * (maxChunkSize + 1), Allocator.Persistent);

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
            width = maxChunkSize + 1,
            height = maxChunkSize + 1
        };


        world = tilemap;
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
        return world.GetTile(position);
    }
    public void SetTile(int2 position, TileNode tile)
    {
        int2 worldToChunk = WorldToChunk(position);
        world.SetTile(position, tile);
    }



}