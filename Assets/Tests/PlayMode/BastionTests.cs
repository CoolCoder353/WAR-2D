using System.Collections;
using System.Collections.Generic;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Client;
using WAR2D.Sim;

/// <summary>
/// v0.13 in a hosted 2-player match (the host and a bot): a Gun turret holds a choke against Scouts but falls to
/// Heavies, and a Jammer hides a base from the enemy's Radar until an enemy unit comes within <c>JamSight</c>.
/// </summary>
public class BastionTests
{
    private const int Bot = 1000003;
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256; // a generated map: Map_2 has no open ground this wide away from the start sites
        config.Match.Map.Seed = 7;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        return PlayModeMatch.TearDown();
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator ATurretHoldsAChokeAgainstScouts()
    {
        var result = new ChokeResult();
        yield return Choke(UnitType.Scout, 6, result);
        yield return PlayModeMatch.WaitUntil(() => CountUnits(Bot, UnitType.Scout) == 0, 30f);
        Assert.That(BuildingHealth(result.Turret), Is.GreaterThan(0f), "the turret stands");
        Assert.That(BuildingHealth(result.Turret), Is.GreaterThan(config.GetBuilding(BuildingType.GunTurret).Health / 2f), "and has most of its health");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator HeaviesBreakAChokeTurret()
    {
        var result = new ChokeResult();
        yield return Choke(UnitType.Heavy, 3, result);
        yield return PlayModeMatch.WaitUntil(() => BuildingHealth(result.Turret) <= 0f, 40f);
        Assert.That(CountUnits(Bot, UnitType.Heavy), Is.GreaterThan(0), "the Heavies outlast it");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator AJammerHidesABaseFromTheEnemyRadarUntilAUnitComesClose()
    {
        int2 hq = default;
        yield return HostWithBot(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;
        // The bot's units stand still and don't shoot, so the only change is what the host's side sees.
        SimContext.Current.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetAttack, OwnerId = Bot, TargetOwnerId = PlayModeMatch.LocalOwner, Flag = false });

        // Our Radar at r; the bot's Jammer 14 tiles east (its jam radius is 12), its units around the Jammer, and a
        // control unit 10 tiles west of the Radar, outside the jam. All far from our HQ's sight.
        int2 radar = default;
        bool found = false;
        (int2 min, int2 max) = wsm.MapBounds;
        for (int ring = 30; ring < 120 && !found; ring++)
        for (int i = 0; i < 8 * ring && !found; i++)
        {
            int2 r = Ring(hq, ring, i);
            if (math.any(r - 12 < min + 2) || math.any(r + new int2(20, 6) > max - 2)) continue;
            if (!Open(wsm, r + new int2(-10, -1), r + new int2(-8, 1)) || !Open(wsm, r, r + 1) || !Open(wsm, r + new int2(12, -3), r + new int2(19, 4))) continue;
            if (math.distance(r + new int2(-10, 0), hq) < 30f || math.distance(r, hq) < 30f || math.distance(r + new int2(14, 0), hq) < 30f) continue;
            found = true;
            radar = r;
        }
        Assert.That(found, Is.True, "room for the layout");
        int2 jammer = radar + new int2(14, 0);
        Assert.That(wsm.DevPlaceBuilding(Bot, BuildingType.Jammer, jammer), Is.True);
        yield return PlayModeMatch.WaitUntil(() => FindBuilding(BuildingType.Jammer) > 0, 10f);
        yield return new WaitForSeconds(0.6f); // a few vision passes: the jam mask is built

        float2 jamCentre = Footprint.VisualCenter(jammer, WorldStateManager.GetBuildingSize(BuildingType.Jammer));
        var hidden = new List<int>();
        float2[] around = { new float2(-2.5f, 0f), new float2(3.5f, 0f), new float2(0.5f, 3f), new float2(0.5f, -2.5f) };
        foreach (float2 offset in around)
            Assert.That(wsm.DevSpawnUnit(Bot, jamCentre + offset, id => { if (id > 0) hidden.Add(id); }, UnitType.Tank), Is.True);
        yield return PlayModeMatch.WaitUntil(() => hidden.Count == around.Length, 5f);

        int pings = 0;
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        void OnAlerts(Alert[] alerts)
        {
            foreach (Alert a in alerts) if (a.Kind == AlertKind.EnemySpotted) pings++;
        }
        local.AlertsReceived += OnAlerts;
        try
        {
            Assert.That(PlayModeMatch.PlaceBuilt(BuildingType.Radar, radar), Is.True);
            DevApi.AllowForTests = true; // PlaceBuilt turns it off
            yield return PlayModeMatch.WaitUntil(() => FindBuilding(BuildingType.Radar) > 0, 10f);
            yield return new WaitForSeconds(3f);
            Assert.That(pings, Is.EqualTo(0), "no Radar ping for contacts in the jam");
            foreach (int id in hidden) Assert.That(ClientWorld.Instance.TryGet(id, out _), Is.False, $"bot unit {id} in the jam is unknown to us");

            // The control: a bot unit in Radar range outside the jam is pinged and seen, so the Radar works.
            int control = 0;
            Assert.That(wsm.DevSpawnUnit(Bot, (float2)(radar + new int2(-9, 0)) + 0.5f, id => control = id, UnitType.Tank), Is.True);
            yield return PlayModeMatch.WaitUntil(() => pings > 0 && control > 0 && ClientWorld.Instance.TryGet(control, out _), 10f);
            foreach (int id in hidden) Assert.That(ClientWorld.Instance.TryGet(id, out _), Is.False, $"bot unit {id} is still hidden");

            // A Scout of ours within JamSight of one of them: that one shows up.
            int scout = 0;
            float2 near = jamCentre + around[0] + new float2(-1.5f, 0f);
            Assert.That(wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, near, id => scout = id, UnitType.Scout), Is.True);
            yield return PlayModeMatch.WaitUntil(() => ClientWorld.Instance.TryGet(hidden[0], out _), 10f);
            Assert.That(scout, Is.GreaterThan(0));
        }
        finally
        {
            local.AlertsReceived -= OnAlerts;
        }
    }

    private sealed class ChokeResult
    {
        public int Turret;
    }

    /// <summary>
    /// Our turret behind a 3-wide gap in a wall line 4 tiles east of it; <paramref name="count"/> bot units of
    /// <paramref name="type"/> start 12 tiles east and attack-move onto the turret through the gap.
    /// </summary>
    private IEnumerator Choke(UnitType type, int count, ChokeResult result)
    {
        int2 hq = default;
        yield return HostWithBot(a => hq = a);
        WorldStateManager wsm = WorldStateManager.Instance;

        int2 at = default;
        bool found = false;
        (int2 min, int2 max) = wsm.MapBounds;
        for (int ring = 20; ring < 120 && !found; ring++)
        for (int i = 0; i < 8 * ring && !found; i++)
        {
            int2 c = Ring(hq, ring, i);
            if (math.any(c - new int2(3, 8) < min + 1) || math.any(c + new int2(16, 8) > max - 1)) continue;
            if (!Open(wsm, c - new int2(3, 8), c + new int2(16, 8))) continue;
            found = true;
            at = c;
        }
        Assert.That(found, Is.True, "room for the choke");

        for (int y = -8; y <= 8; y++)
            if (math.abs(y) > 1)
                SimContext.Current.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = at + new int2(4, y), TileType = TileType.Wall });
        Assert.That(PlayModeMatch.PlaceBuilt(BuildingType.GunTurret, at), Is.True);
        DevApi.AllowForTests = true; // PlaceBuilt turns it off
        yield return PlayModeMatch.WaitUntil(() => FindBuilding(BuildingType.GunTurret) > 0, 10f);
        result.Turret = FindBuilding(BuildingType.GunTurret);
        yield return PlayModeMatch.WaitUntil(() => !wsm.Map.Grid.IsWalkable(at + new int2(4, 2)), 5f);

        var ids = new List<int>();
        for (int k = 0; k < count; k++)
        {
            float2 p = (float2)(at + new int2(12 + k % 3, k / 3 * 2 - 2)) + 0.5f;
            Assert.That(wsm.DevSpawnUnit(Bot, p, id => { if (id > 0) ids.Add(id); }, type), Is.True);
        }
        yield return PlayModeMatch.WaitUntil(() => ids.Count == count, 5f);
        SimContext.Current.Commands.Enqueue(new SimCommand
        {
            Kind = SimCommandKind.OrderUnits, Order = OrderKind.AttackMove, OwnerId = Bot, Tile = at, Ids = ids.ToArray(),
        });
    }

    private IEnumerator HostWithBot(System.Action<int2> anchorOut)
    {
        int2 hq = default;
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        DevApi.AllowForTests = true;
        GameCore.Instance.AddBot(Bot, 1e9f);
        PlaceFar(BuildingType.Base, hq, 1e7f); // keeps the bot in the match
        anchorOut(hq);
    }

    /// <summary>True when every tile in the box from <paramref name="lo"/> to <paramref name="hi"/> (inclusive) is open ground.</summary>
    private static bool Open(WorldStateManager wsm, int2 lo, int2 hi)
    {
        for (int y = lo.y; y <= hi.y; y++)
        for (int x = lo.x; x <= hi.x; x++)
            if (!wsm.Map.Grid.IsWalkable(new int2(x, y))) return false;
        return true;
    }

    /// <summary>Places a bot building as far from <paramref name="from"/> as fits.</summary>
    private static void PlaceFar(BuildingType type, int2 from, float health)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        (int2 min, int2 max) = wsm.MapBounds;
        int2 far = new int2(from.x < (min.x + max.x) / 2 ? max.x - 6 : min.x + 6, from.y < (min.y + max.y) / 2 ? max.y - 6 : min.y + 6);
        // A generated map's corner can be deep rock: search outward until open ground (far from our HQ) turns up.
        for (int ring = 0; ring < 200; ring++)
        for (int i = 0; i < math.max(1, 8 * ring); i++)
        {
            int2 at = ring == 0 ? far : Ring(far, ring, i);
            if (math.any(at < min) || math.any(at > max) || math.distance(at, from) < 40f) continue;
            if (wsm.DevPlaceBuilding(Bot, type, at, health)) return;
        }
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
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(Unit));
        using var units = q.ToComponentDataArray<Unit>(Allocator.Temp);
        int n = 0;
        foreach (Unit u in units) if (u.OwnerId == owner && u.Type == (byte)type && u.Health > 0f) n++;
        return n;
    }

    /// <summary>The building's health, 0 once it is gone.</summary>
    private static float BuildingHealth(int id)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData), typeof(HealthComponent));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities)
            if (PlayModeMatch.Em.GetComponentData<BuildingData>(e).id == id) return PlayModeMatch.Em.GetComponentData<HealthComponent>(e).currentHealth;
        return 0f;
    }

    private static int FindBuilding(BuildingType type)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities)
        {
            BuildingData d = PlayModeMatch.Em.GetComponentData<BuildingData>(e);
            if (d.buildingType == type) return d.id;
        }
        return -1;
    }
}
