using System;
using Unity.Collections;
using Unity.Entities.UniversalDelegates;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;


public class WorldStateManager : MonoBehaviour
{
    public static WorldStateManager Instance { get; private set; }

    public TilemapStruct tilemap { get; private set; }

    public Grid grid;
    public Tilemap WalkableTilemap;
    public Tilemap UnwalkableTilemap;

    public bool showTileMapweights = false;

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
        if (showTileMapweights)
        {
            for (int x = 0; x < tilemap.width; x++)
            {
                for (int y = 0; y < tilemap.height; y++)
                {
                    TileNode tile = tilemap.GetTile(new int2(x, y));
                    if (tile.weight == 0)
                    {
                        Gizmos.color = Color.red;
                    }
                    else
                    {
                        Gizmos.color = Color.green;
                    }
                    Gizmos.DrawWireCube(new Vector3(tile.position.x + 0.5f, tile.position.y + 0.5f, 0), new Vector3(1, 1, 0));
                }
            }
        }
    }

    private void GenerateTileMap()
    {
        BoundsInt bounds = WalkableTilemap.cellBounds;
        TileBase[] allTiles = WalkableTilemap.GetTilesBlock(bounds);

        NativeHashMap<int2, TileNode> tiles = new NativeHashMap<int2, TileNode>(bounds.size.x * bounds.size.y, Allocator.Temp);


        for (int x = 0; x < bounds.size.x; x++)
        {
            for (int y = 0; y < bounds.size.y; y++)
            {
                TileBase tile = allTiles[x + y * bounds.size.x];
                if (tile != null)
                {
                    int2 localPlace = new int2(bounds.position.x + x, bounds.position.y + y);

                    int weight = 1;

                    if (UnwalkableTilemap.GetTile(new Vector3Int(localPlace.x, localPlace.y, 0)) != null)
                    {
                        weight = 0;
                    }

                    TileNode tileNode = new TileNode
                    {
                        position = localPlace,
                        weight = weight,
                        used = 0
                    };

                    tiles[localPlace] = tileNode;
                }
            }
        }

        tilemap = new TilemapStruct(tiles, bounds.size.x, bounds.size.y);
    }




    //TODO: Change the entity to a reference to the player id the entity belongs to
    public void LockNode(int2 position, int entity = 1)
    {
        TileNode tile = tilemap.GetTile(position);
        tile.used = entity;

        tilemap.SetTile(position, tile);
    }

    public void UnlockNode(int2 position)
    {
        TileNode tile = tilemap.GetTile(position);
        tile.used = 0;

        tilemap.SetTile(position, tile);

    }

    public bool IsNodeLocked(int2 position)
    {
        TileNode tile = tilemap.GetTile(position);
        return tile.isUsed;
    }


}