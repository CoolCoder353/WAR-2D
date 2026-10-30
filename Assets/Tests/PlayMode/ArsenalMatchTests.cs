using System.Collections;
using System.Collections.Generic;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.TestTools;

/// <summary>v0.12 in a hosted match: every producer makes each of its units, and a Suicide bomber run on an enemy building.</summary>
public class ArsenalMatchTests
{
    private static readonly int Bot = AiOwnerIds.For(2);
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        return PlayModeMatch.TearDown();
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator EveryProducerMakesEachOfItsUnits()
    {
        yield return HostWithHQ();
        WorldStateManager wsm = WorldStateManager.Instance;
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        BuildingType[] producers = { BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.SiegeWorkshop };
        var ids = new Dictionary<BuildingType, int>();
        foreach (BuildingType producer in producers)
        {
            Assert.That(wsm.TryFindBuildableAnchor(producer, 0f, local, out int2 anchor), Is.True, $"room for a {producer}");
            Assert.That(PlayModeMatch.PlaceBuilt(producer, anchor), Is.True);
            yield return PlayModeMatch.WaitUntil(() => FindBuilding(producer) > 0, 10f);
            ids[producer] = FindBuilding(producer);
        }

        // Round r sets each producer to its r-th type and queues one; the three run side by side.
        for (int round = 0; round < 3; round++)
        {
            var expected = new List<UnitType>();
            foreach (BuildingType producer in producers)
            {
                UnitType[] makes = SpawnerRules.Produces(producer);
                if (round >= makes.Length) continue;
                UnitType type = makes[round];
                expected.Add(type);
                // Commands arrive in order, so the queued unit is of the new type. (A producer already on the
                // type gets no reply to the switch, so don't wait on the client's copy here.)
                wsm.CmdSetProduction(new[] { ids[producer] }, (byte)type);
                wsm.CmdChangeQueues(new[] { ids[producer] }, 1);
            }
            foreach (UnitType type in expected)
            {
                float end = UnityEngine.Time.realtimeSinceStartup + 20f;
                while (CountUnits(PlayModeMatch.LocalOwner, type) < 1)
                {
                    if (UnityEngine.Time.realtimeSinceStartup > end)
                        Assert.Fail($"no {type} after 20 s");
                    yield return null;
                }
            }
        }
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator ABomberRunDamagesAnEnemyBuilding()
    {
        int2 hq = default;
        yield return HostWithHQ(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;
        DevApi.AllowForTests = true;
        GameCore.Instance.DevAddScriptedAi(Bot, 1e9f);
        PlaceFar(BuildingType.Base, hq, 1e7f); // keeps the bot in the match

        // An enemy Heavy Factory about 16 tiles from the local HQ, and a bomber 5 tiles from it.
        int2 factory = default;
        bool placed = false;
        for (int ring = 16; ring < 30 && !placed; ring++)
        for (int i = 0; i < 8 * ring && !placed; i++)
        {
            factory = Ring(hq, ring, i);
            placed = wsm.DevPlaceBuilding(Bot, BuildingType.HeavyFactory, factory);
        }
        Assert.That(placed, Is.True, "room for the enemy factory");
        yield return PlayModeMatch.WaitUntil(() => FindBuilding(BuildingType.HeavyFactory) > 0, 10f);
        Entity target = BuildingEntity(BuildingType.HeavyFactory);
        float before = PlayModeMatch.Em.GetComponentData<HealthComponent>(target).currentHealth;

        bool spawned = false;
        for (int i = 0; i < 40 && !spawned; i++)
        {
            int2 tile = Ring(factory, 5, i);
            if (!wsm.Map.Grid.IsWalkable(tile)) continue;
            spawned = wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, null, UnitType.SuicideBomber);
        }
        Assert.That(spawned, Is.True, "room for the bomber");
        yield return PlayModeMatch.WaitUntil(() => CountUnits(PlayModeMatch.LocalOwner, UnitType.SuicideBomber) == 1, 5f);

        // It sees the factory, runs at it and blows up on contact: 150 blast damage, x2 against buildings.
        yield return PlayModeMatch.WaitUntil(() => CountUnits(PlayModeMatch.LocalOwner, UnitType.SuicideBomber) == 0, 20f);
        yield return PlayModeMatch.WaitUntil(() => PlayModeMatch.Em.GetComponentData<HealthComponent>(target).currentHealth < before, 5f);
        Assert.That(PlayModeMatch.Em.GetComponentData<HealthComponent>(target).currentHealth, Is.EqualTo(before - 300f).Within(0.01f));
    }

    private IEnumerator HostWithHQ(System.Action<int2> anchorOut = null)
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        WorldStateManager wsm = WorldStateManager.Instance;
        Assert.That(wsm.TryFindBuildableAnchor(BuildingType.Base, 0f, local, out int2 hq), Is.True);
        wsm.TryAddBuilding(hq, BuildingType.Base, 0f);
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);
        yield return PlayModeMatch.WaitForSight(hq);
        anchorOut?.Invoke(hq);
    }

    /// <summary>Places a bot building as far from <paramref name="from"/> as fits.</summary>
    private static void PlaceFar(BuildingType type, int2 from, float health)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        (int2 min, int2 max) = wsm.MapBounds;
        int2 far = new int2(from.x < (min.x + max.x) / 2 ? max.x - 6 : min.x + 6, from.y < (min.y + max.y) / 2 ? max.y - 6 : min.y + 6);
        for (int ring = 0; ring < 40; ring++)
        for (int i = 0; i < math.max(1, 8 * ring); i++)
            if (wsm.DevPlaceBuilding(Bot, type, Ring(far, ring, i), health)) return;
        Assert.Fail($"no room for the bot's {type}");
    }

    private static int2 Ring(int2 origin, int ring, int i)
    {
        if (ring == 0) return origin;
        int side = 2 * ring;
        i %= 8 * ring;
        if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
        i -= side;
        if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
        i -= side;
        if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
        i -= side;
        return new int2(origin.x - ring, origin.y + ring - i);
    }

    private static int CountUnits(int owner, UnitType type)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(WAR2D.Sim.Unit));
        using var units = q.ToComponentDataArray<WAR2D.Sim.Unit>(Allocator.Temp);
        int n = 0;
        foreach (WAR2D.Sim.Unit u in units) if (u.OwnerId == owner && u.Type == (byte)type && u.Health > 0f) n++;
        return n;
    }

    private static int FindBuilding(BuildingType type)
    {
        Entity e = BuildingEntity(type);
        return e == Entity.Null ? -1 : PlayModeMatch.Em.GetComponentData<BuildingData>(e).id;
    }

    private static Entity BuildingEntity(BuildingType type)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities) if (PlayModeMatch.Em.GetComponentData<BuildingData>(e).buildingType == type) return e;
        return Entity.Null;
    }
}
