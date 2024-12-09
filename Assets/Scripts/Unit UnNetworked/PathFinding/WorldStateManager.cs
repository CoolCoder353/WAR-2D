using System;
using Unity.Collections;
using Unity.Entities;
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
        //Also check if the game is running
        if (showTileMapweights && Application.isPlaying)
        {

            foreach (KVPair<int2, TileNode> tilepair in tilemap.tiles)
            {
                TileNode tile = tilepair.Value;
                if (tile.weight == 0 && tile.used == 0)
                {
                    Gizmos.color = Color.red;
                }
                else if (tile.used != 0)
                {
                    Gizmos.color = Color.blue;
                }
                else
                {
                    Gizmos.color = Color.green;
                }
                Gizmos.DrawWireCube(new Vector3(tile.position.x + 0.5f, tile.position.y + 0.5f, 0), new Vector3(1, 1, 0));
            }
        }
    }

    private void GenerateTileMap()
    {
        BoundsInt bounds = WalkableTilemap.cellBounds;
        TileBase[] allTiles = WalkableTilemap.GetTilesBlock(bounds);

        NativeHashMap<int2, TileNode> tiles = new NativeHashMap<int2, TileNode>(bounds.size.x * bounds.size.y, Allocator.Persistent);

        for (int x = 0; x < bounds.size.x; x++)
        {
            for (int y = 0; y < bounds.size.y; y++)
            {
                TileBase tile = allTiles[x + y * bounds.size.x];
                if (tile != null)
                {
                    Vector3Int localPlace = new Vector3Int(bounds.position.x + x, bounds.position.y + y, 0);
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