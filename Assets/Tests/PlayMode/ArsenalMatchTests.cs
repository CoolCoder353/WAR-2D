using System.Collections;
using System.Collections.Generic;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.TestTools;

/// <summary>v0.12 in a hosted match: every producer makes each of its units, and a Bomber run on an enemy building.</summary>
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
                var deadline = new PlayModeMatch.Deadline(20f);
                while (CountUnits(PlayModeMatch.LocalOwner, type) < 1)
                {
                    if (deadline.Passed)
                        Assert.Fail($"no {type} after 20 s of game time ({deadline.Describe()})");
                    yield return null;
                }
            }
        }
    }

    /// <summary>
    /// A built Barracks and a Barracks blueprint under one selection: both take the type and the queue, the built one
    /// produces at once, and the blueprint keeps its queue and starts on it once Builders finish it.
    /// </summary>
    [UnityTest, Timeout(180000)]
    public IEnumerator ABlueprintKeepsItsProductionAndStartsWhenBuilt()
    {
        yield return HostWithHQ();
        WorldStateManager wsm = WorldStateManager.Instance;
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        int me = PlayModeMatch.LocalOwner;
        Assert.That(wsm.TryFindBuildableAnchor(BuildingType.Barracks, 0f, local, out int2 builtAt), Is.True, "room for the built Barracks");
        Assert.That(PlayModeMatch.PlaceBuilt(BuildingType.Barracks, builtAt), Is.True);
        yield return PlayModeMatch.WaitUntil(() => BarracksIds(false).Count == 1, 10f);
        Assert.That(wsm.TryFindBuildableAnchor(BuildingType.Barracks, 0f, local, out int2 blueprintAt), Is.True, "room for the blueprint");
        wsm.TryAddBuilding(blueprintAt, BuildingType.Barracks, 0f); // a normal placement: a blueprint
        yield return PlayModeMatch.WaitUntil(() => BarracksIds(true).Count == 1, 10f, () => "the blueprint was never placed");
        int built = BarracksIds(false)[0], blueprint = BarracksIds(true)[0];

        // One selection of both: the type, then one unit each.
        int[] both = { built, blueprint };
        wsm.CmdSetProduction(both, (byte)UnitType.Tank);
        wsm.CmdChangeQueues(both, 1);
        yield return PlayModeMatch.WaitUntil(() => CountUnits(me, UnitType.Tank) == 1, 30f, () => "the built Barracks made no Tank");
        Assert.That(BarracksIds(true), Does.Contain(blueprint), "still a blueprint");
        SpawnerData waiting = PlayModeMatch.Em.GetComponentData<SpawnerData>(BlueprintEntity(wsm, blueprint));
        Assert.That((UnitType)waiting.unitType, Is.EqualTo(UnitType.Tank), "the blueprint took the type");
        Assert.That(waiting.count, Is.EqualTo(1), "the blueprint keeps its queue until it is built");
        Assert.That(local.SpawnerType(blueprint), Is.EqualTo(UnitType.Tank), "its owner was told the type");
        Assert.That(local.SpawnerQueue(blueprint), Is.EqualTo(1), "its owner was told the queue");

        // Builders finish it (idle ones take the blueprint on their own): its queued Tank then comes out.
        DevApi.AllowForTests = true;
        for (int i = 0; i < 4; i++) Assert.That(SpawnNear(me, blueprintAt, 3, UnitType.Builder), Is.True, "room for a Builder");
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.WaitUntil(() => BarracksIds(true).Count == 0, 60f, () => "the blueprint was never finished");
        Assert.That(CountUnits(me, UnitType.Tank), Is.EqualTo(1), "nothing was made while it was a blueprint");
        yield return PlayModeMatch.WaitUntil(() => CountUnits(me, UnitType.Tank) == 2, 30f, () => "the finished Barracks never made its queued Tank");
        Assert.That(PlayModeMatch.Em.GetComponentData<SpawnerData>(BlueprintEntity(wsm, blueprint)).count, Is.EqualTo(0));
    }

    private static Entity BlueprintEntity(WorldStateManager wsm, int id)
    {
        Assert.That(wsm.TryGetBuilding(id, out Entity entity), Is.True, "the building is gone");
        return entity;
    }

    /// <summary>The local player's Barracks ids: the blueprints, or the finished ones.</summary>
    private static List<int> BarracksIds(bool blueprints)
    {
        var ids = new List<int>();
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities)
        {
            BuildingData data = PlayModeMatch.Em.GetComponentData<BuildingData>(e);
            if (data.buildingType == BuildingType.Barracks && data.ownerId == PlayModeMatch.LocalOwner && PlayModeMatch.Em.HasComponent<Construction>(e) == blueprints) ids.Add(data.id);
        }
        return ids;
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
            spawned = wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)tile + 0.5f, null, UnitType.Bomber);
        }
        Assert.That(spawned, Is.True, "room for the bomber");
        yield return PlayModeMatch.WaitUntil(() => CountUnits(PlayModeMatch.LocalOwner, UnitType.Bomber) == 1, 5f);

        // It sees the factory, runs at it and blows up on contact: its blast damage, times the table's number against buildings.
        yield return PlayModeMatch.WaitUntil(() => CountUnits(PlayModeMatch.LocalOwner, UnitType.Bomber) == 0, 20f);
        yield return PlayModeMatch.WaitUntil(() => PlayModeMatch.Em.GetComponentData<HealthComponent>(target).currentHealth < before, 5f);
        Assert.That(PlayModeMatch.Em.GetComponentData<HealthComponent>(target).currentHealth, Is.EqualTo(before - config.GetUnit(UnitType.Bomber).BlastDamage * config.Damage.Multiplier(UnitType.Bomber, TargetClass.Building)).Within(0.01f));
    }

    // ---------- v0.20 Onslaught in a hosted match (spec §13) ----------

    /// <summary>A Flamethrower sets an enemy Barracks burning, and the host's client is told it burns.</summary>
    [UnityTest, Timeout(120000)]
    public IEnumerator AFlamethrowerSetsAnEnemyBarracksBurning()
    {
        int2 hq = default;
        yield return HostWithHQ(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;
        DevApi.AllowForTests = true;
        GameCore.Instance.DevAddScriptedAi(Bot, 1e9f);
        PlaceFar(BuildingType.Base, hq, 1e7f);
        int2 barracks = PlaceNear(BuildingType.Barracks, hq, 14);
        yield return PlayModeMatch.WaitUntil(() => FindBuilding(BuildingType.Barracks) > 0, 10f);
        int id = FindBuilding(BuildingType.Barracks);
        Assert.That(SpawnNear(PlayModeMatch.LocalOwner, barracks, 4, UnitType.Flamethrower), Is.True, "room for the Flamethrower");
        yield return PlayModeMatch.WaitUntil(() => WAR2D.Net.Replication.ClientBuildings.Current.Burning.Contains(id), 20f, () => "the client never saw the Barracks burn");
    }

    /// <summary>A Sniper aims (its side is told), then one shot takes most of a Heavy.</summary>
    [UnityTest, Timeout(120000)]
    public IEnumerator ASniperAimsAndHitsAHeavyHard()
    {
        int2 hq = default;
        yield return HostWithHQ(a => hq = a);
        DevApi.AllowForTests = true;
        GameCore.Instance.DevAddScriptedAi(Bot, 1e9f);
        PlaceFar(BuildingType.Base, hq, 1e7f);
        int2 at = default;
        bool spawned = false;
        for (int i = 0; i < 80 && !spawned; i++) { at = Ring(hq, 12, i); spawned = WorldStateManager.Instance.Map.Grid.IsWalkable(at) && WorldStateManager.Instance.DevSpawnUnit(Bot, (float2)at + 0.5f, null, UnitType.Heavy); }
        Assert.That(spawned, Is.True, "room for the Heavy");
        Assert.That(SpawnNear(PlayModeMatch.LocalOwner, at, 9, UnitType.Sniper), Is.True, "room for the Sniper");
        bool aimed = false;
        System.Action<WAR2D.Net.Replication.CombatNews> onNews = news => aimed |= news.Aims.Count > 0;
        WAR2D.Net.Replication.ClientCombat.Received += onNews;
        try
        {
            yield return PlayModeMatch.WaitUntil(() => CountUnits(PlayModeMatch.LocalOwner, UnitType.Sniper) == 1 && CountUnits(Bot, UnitType.Heavy) == 1, 5f);
            // One aimed shot: 160 × 2 less the Heavy's armour (an unscreened Sniper then loses to the Heavy closing in: it needs a screen).
            float hit = config.GetUnit(UnitType.Sniper).Damage * config.Damage.Multiplier(UnitType.Sniper, TargetClass.HeavyUnit) - config.GetUnit(UnitType.Heavy).FlatArmour;
            yield return PlayModeMatch.WaitUntil(() => HealthOf(Bot, UnitType.Heavy) <= config.GetUnit(UnitType.Heavy).Health - hit + 0.01f, 30f,
                () => $"no aimed shot: {Describe(Bot, UnitType.Heavy)}, {Describe(PlayModeMatch.LocalOwner, UnitType.Sniper)}");
            Assert.That(aimed, Is.True, "the Sniper's side was told of its aim");
        }
        finally { WAR2D.Net.Replication.ClientCombat.Received -= onNews; }
    }

    /// <summary>Artillery fires at a target 30 tiles off, past its own sight, on a Scout's sight, and the shell's flight reaches its side.</summary>
    [UnityTest, Timeout(120000)]
    public IEnumerator ArtilleryShellsATargetOnAScoutsSight()
    {
        int2 hq = default;
        yield return HostWithHQ(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;
        DevApi.AllowForTests = true;
        GameCore.Instance.DevAddScriptedAi(Bot, 1e9f);
        PlaceFar(BuildingType.Base, hq, 1e7f);
        // A straight open line of 31 tiles: the gun at one end, a Scout 8 tiles short of the other, the target at the end.
        int2 gun = default, step = default;
        bool found = false;
        int2[] directions = { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1) };
        for (int ring = 6; ring < 30 && !found; ring++)
        for (int i = 0; i < 8 * ring && !found; i++)
        foreach (int2 d in directions)
        {
            int2 start = Ring(hq, ring, i);
            bool open = true;
            for (int k = 0; k <= 31 && open; k++) open = wsm.Map.Grid.IsWalkable(start + d * k) && wsm.Map.Grid.IsWalkable(start + d * k + new int2(d.y, d.x)) && wsm.Map.Grid.IsWalkable(start + d * k - new int2(d.y, d.x));
            if (!open) continue;
            gun = start; step = d; found = true;
            break;
        }
        Assert.That(found, Is.True, "an open 31-tile line");
        int full = 0;
        System.Action<WAR2D.Net.Replication.CombatNews> onNews = news => { foreach (var shell in news.Shells) if (shell.Full) full++; };
        WAR2D.Net.Replication.ClientCombat.Received += onNews;
        try
        {
            Assert.That(wsm.DevSpawnUnit(Bot, (float2)(gun + step * 30) + 0.5f, null, UnitType.Tank), Is.True);
            Assert.That(wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)(gun + step * 22) + 0.5f, null, UnitType.Scout), Is.True);
            Assert.That(wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)gun + 0.5f, null, UnitType.Artillery), Is.True);
            yield return PlayModeMatch.WaitUntil(() => full > 0, 30f, () => "no shell was fired");
        }
        finally { WAR2D.Net.Replication.ClientCombat.Received -= onNews; }
    }

    private static float HealthOf(int owner, UnitType type)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(WAR2D.Sim.Unit));
        using var units = q.ToComponentDataArray<WAR2D.Sim.Unit>(Allocator.Temp);
        foreach (WAR2D.Sim.Unit u in units) if (u.OwnerId == owner && u.Type == (byte)type) return u.Health;
        return 0f;
    }

    private static string Describe(int owner, UnitType type)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(WAR2D.Sim.Unit));
        using var units = q.ToComponentDataArray<WAR2D.Sim.Unit>(Allocator.Temp);
        foreach (WAR2D.Sim.Unit u in units) if (u.OwnerId == owner && u.Type == (byte)type) return $"{type} at {u.Position} hp {u.Health} target {u.TargetId} stance {u.Stance}";
        return $"no {type}";
    }

    private static int2 PlaceNear(BuildingType type, int2 from, int distance)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        for (int ring = distance; ring < distance + 16; ring++)
        for (int i = 0; i < 8 * ring; i++)
        {
            int2 at = Ring(from, ring, i);
            if (wsm.DevPlaceBuilding(Bot, type, at)) return at;
        }
        Assert.Fail($"no room for the bot's {type}");
        return default;
    }

    private static bool SpawnNear(int owner, int2 at, int distance, UnitType type)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        for (int i = 0; i < 8 * distance; i++)
        {
            int2 tile = Ring(at, distance, i);
            if (wsm.Map.Grid.IsWalkable(tile) && wsm.DevSpawnUnit(owner, (float2)tile + 0.5f, null, type)) return true;
        }
        return false;
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
