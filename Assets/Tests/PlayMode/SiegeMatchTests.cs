using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.TestTools;
using WAR2D.Net.Replication;

/// <summary>A hosted solo match through the v0.7 siege loop: the HQ makes a Builder, which raises a wall.</summary>
public class SiegeMatchTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown() => PlayModeMatch.TearDown();

    [UnityTest, Timeout(120000)]
    public IEnumerator HqBuilder_BuildsAWallTile()
    {
        config.Match.Map.Size = 256; // terrain deltas reach clients of generated maps
        config.Match.Map.Seed = 5;
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;

        // The HQ is a blueprint until a Builder finishes it, so start from a finished one.
        int hqId = -1;
        yield return Wait("HQ entity", () => TryFindBuilding(BuildingType.Base, out hqId), 10f);
        wsm.CmdChangeQueues(new[] { hqId }, 1);

        int builder = -1;
        float2 at = default;
        yield return Wait("Builder unit", () => TryFindBuilder(out builder, out at), 30f);

        // A free ground tile a few steps from the Builder.
        int2 wall = default;
        bool found = false;
        for (int r = 3; r <= 8 && !found; r++)
            for (int dx = -r; dx <= r && !found; dx++)
            {
                int2 t = (int2)math.floor(at) + new int2(dx, r);
                if (wsm.Map.Grid.TileAt(t) == TileType.Ground && !wsm.Map.IsUsed(t)) { wall = t; found = true; }
            }
        Assert.That(found, Is.True, "a free ground tile near the Builder");

        wsm.CmdPlaceWall(wall, wall);
        int index = wall.y * wsm.Map.Grid.Width + wall.x;
        yield return Wait("wall blueprint on client", () => ClientTerrain.Current.Tiles.Length > index && ClientTerrain.Current.Tiles[index] == (byte)TileType.WallBlueprint, 10f);

        var ids = new System.Collections.Generic.List<int> { builder };
        foreach (byte[] chunk in OrderIdCodec.EncodeChunks(ids))
            wsm.CmdOrderChunk(1, chunk, true, (byte)OrderKind.Build, false, wall);

        yield return Wait("wall built on client", () => ClientTerrain.Current.Tiles[index] == (byte)TileType.PlayerWall, 60f);
        Assert.That(ClientTerrain.Current.WallOwner(index), Is.EqualTo(PlayModeMatch.LocalOwner));
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator DiggersClearADraggedArea()
    {
        config.Match.Map.Size = 256; // terrain deltas reach clients of generated maps
        config.Match.Map.Seed = 5;
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;

        // A rock tile with open ground below it, near the HQ: the Digger starts on the open tile.
        int2 rock = default, stand = default;
        bool found = false;
        for (int r = 4; r <= 40 && !found; r++)
            for (int dx = -r; dx <= r && !found; dx++)
                foreach (int dy in new[] { -r, r })
                {
                    int2 t = hq + new int2(dx, dy);
                    if (!wsm.Map.Grid.Contains(t + 2) || !wsm.Map.Grid.Contains(t - 2)) continue;
                    if (wsm.Map.Grid.TileAt(t) != TileType.Wall || !wsm.Map.IsWalkable(t - new int2(0, 1))) continue;
                    rock = t; stand = t - new int2(0, 1); found = true;
                    break;
                }
        Assert.That(found, Is.True, "rock beside open ground near the HQ");

        int digger = -1;
        wsm.Sim.Commands.Enqueue(new WAR2D.Sim.SimCommand { Kind = WAR2D.Sim.SimCommandKind.SpawnUnit, OwnerId = PlayModeMatch.LocalOwner, Position = (float2)stand + 0.5f, UnitType = UnitType.Digger, OnSpawned = id => digger = id });
        yield return Wait("Digger", () => digger > 0, 10f);

        // A 2×1 box over the rock and its neighbour: only the rock tiles in it are dug.
        int2 from = rock, to = rock + new int2(1, 0);
        var box = new System.Collections.Generic.List<int>();
        for (int x = from.x; x <= to.x; x++)
            if (TileRules.IsDiggable(wsm.Map.Grid.TileAt(new int2(x, rock.y))) && wsm.Map.Grid.TileAt(new int2(x, rock.y)) != TileType.PlayerWall) box.Add(rock.y * wsm.Map.Grid.Width + x);
        foreach (byte[] chunk in OrderIdCodec.EncodeChunks(new System.Collections.Generic.List<int> { digger }))
            wsm.CmdDigAreaChunk(2, chunk, true, false, from, to);

        yield return Wait("the area dug out on the client", () =>
        {
            foreach (int index in box) if (!TileRules.IsFloor((TileType)ClientTerrain.Current.Tiles[index])) return false;
            return true;
        }, 60f);
        yield return Wait("the area dropped once clear", () => wsm.Sim.DigAreas.Count == 0, 10f);
    }

    private static IEnumerator Wait(string what, System.Func<bool> condition, float seconds)
    {
        float end = UnityEngine.Time.realtimeSinceStartup + seconds;
        while (!condition())
        {
            if (UnityEngine.Time.realtimeSinceStartup > end) Assert.Fail("Timed out waiting for " + what);
            yield return null;
        }
    }

    private static bool TryFindBuilding(BuildingType type, out int id)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData));
        using var data = q.ToComponentDataArray<BuildingData>(Unity.Collections.Allocator.Temp);
        foreach (BuildingData b in data)
            if (b.buildingType == type) { id = b.id; return true; }
        id = -1;
        return false;
    }

    private static bool TryFindBuilder(out int id, out float2 position)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(WAR2D.Sim.Unit));
        using var units = q.ToComponentDataArray<WAR2D.Sim.Unit>(Unity.Collections.Allocator.Temp);
        foreach (WAR2D.Sim.Unit u in units)
            if (u.Type == (byte)UnitType.Builder) { id = u.Id; position = u.Position; return true; }
        id = -1;
        position = default;
        return false;
    }
}
