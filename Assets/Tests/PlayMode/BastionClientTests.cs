using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Client;
using WAR2D.Net.Replication;

/// <summary>v0.13 on the client: a Gun turret shooting the local player's unit turns its head and fires a tracer there.</summary>
public class BastionClientTests
{
    private const int Bot = 1000003;
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
    public IEnumerator ATurretShootingOurUnitTurnsItsHeadAndFiresATracer()
    {
        int2 hq = default;
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        yield return PlayModeMatch.WaitUntil(() => GameCore.Instance.CurrentState == GameState.Playing, 10f);
        yield return PlayModeMatch.WaitForSight(hq);
        WorldStateManager wsm = WorldStateManager.Instance;
        DevApi.AllowForTests = true;
        GameCore.Instance.AddBot(Bot, 1e9f);
        PlaceFar(BuildingType.Base, hq, 1e7f); // keeps the bot in the match

        // An enemy turret out of range of the HQ (so our unit is its only target), and a unit of ours 4 tiles east of it
        // (inside its range of 7).
        int2 turret = default, unitAt = default;
        bool placed = false;
        (int2 min, int2 max) = wsm.MapBounds;
        for (int ring = 13; ring < 30 && !placed; ring++)
        for (int i = 0; i < 8 * ring && !placed; i++)
        {
            int2 at = Ring(hq, ring, i), east = at + new int2(4, 0);
            if (math.any(at < min + 4) || math.any(east > max - 4)) continue; // clear of the border
            if (!wsm.Map.Grid.IsWalkable(east) || !wsm.Map.Grid.IsWalkable(at)) continue;
            if (!LineClear(wsm, at, east)) continue;
            placed = wsm.DevPlaceBuilding(Bot, BuildingType.GunTurret, at);
            turret = at;
            unitAt = east;
        }
        Assert.That(placed, Is.True, "room for the turret");
        int turretId = 0;
        yield return PlayModeMatch.WaitUntil(() => (turretId = FindBuilding(BuildingType.GunTurret)) > 0, 10f);
        Camera.main.transform.position = new Vector3(turret.x + 2.5f, turret.y + 0.5f, Camera.main.transform.position.z);
        int shots = Effects.TurretShots, tracers = Effects.TurretTracers;
        // A Heavy (it outlasts the turret's fire for a while: 0.4 against heavy armour) that doesn't shoot back, so the turret stands.
        WAR2D.Sim.SimContext.Current.Commands.Enqueue(new WAR2D.Sim.SimCommand
        {
            Kind = WAR2D.Sim.SimCommandKind.SetAttack, OwnerId = PlayModeMatch.LocalOwner, TargetOwnerId = Bot, Flag = false,
        });
        int heavy = 0;
        Assert.That(wsm.DevSpawnUnit(PlayModeMatch.LocalOwner, (float2)unitAt + 0.5f, id => heavy = id, UnitType.Heavy), Is.True);

        // The client sees the turret, is told of its shots at the Heavy (units first), turns the head east and draws them.
        float end = Time.realtimeSinceStartup + 30f;
        while (!(ClientBuildings.Current.Entries.ContainsKey(turretId) &&
                 !float.IsNegativeInfinity(ClientBuildings.Current.HeadOf(turretId).FiredAt) && ClientBuildings.Current.HeadOf(turretId).Facing == 0))
        {
            if (Time.realtimeSinceStartup > end)
                Assert.Fail($"turret {turretId} at {turret}, unit at {unitAt}: known {ClientBuildings.Current.Entries.ContainsKey(turretId)}, " +
                            $"fired at {ClientBuildings.Current.HeadOf(turretId).FiredAt}, facing {ClientBuildings.Current.HeadOf(turretId).Facing}, shots {Effects.TurretShots - shots}, " +
                            $"server target {Weapon(turretId).TargetId} kind {Weapon(turretId).TargetKind}, units known {ClientWorld.Instance.KnownCount}, heavy {heavy} at {(ClientWorld.Instance.TryGet(heavy, out ClientUnitView v) ? v.Position.ToString() : "?")}, hq {hq}");
            yield return null;
        }
        yield return PlayModeMatch.WaitUntil(() => Effects.TurretShots > shots && Effects.TurretTracers > tracers, 5f);
        Assert.That(ClientBuildings.Current.HeadOf(turretId).Facing, Is.EqualTo(0), "facing east, toward the Heavy");
    }

    /// <summary>True when every tile on the row from <paramref name="a"/> to <paramref name="b"/> is open ground.</summary>
    private static bool LineClear(WorldStateManager wsm, int2 a, int2 b)
    {
        for (int x = math.min(a.x, b.x); x <= math.max(a.x, b.x); x++)
            if (!wsm.Map.Grid.IsWalkable(new int2(x, a.y))) return false;
        return true;
    }

    /// <summary>Places a bot building as far from <paramref name="from"/> as fits.</summary>
    private static void PlaceFar(BuildingType type, int2 from, float health)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        (int2 min, int2 max) = wsm.MapBounds;
        int2 far = new int2(from.x < (min.x + max.x) / 2 ? max.x - 6 : min.x + 6, from.y < (min.y + max.y) / 2 ? max.y - 6 : min.y + 6);
        for (int ring = 0; ring < 40; ring++)
        for (int i = 0; i < math.max(1, 8 * ring); i++)
            if (wsm.DevPlaceBuilding(Bot, type, ring == 0 ? far : Ring(far, ring, i), health)) return;
        Assert.Fail($"no room for the bot's {type}");
    }

    private static int2 Ring(int2 origin, int ring, int i)
    {
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

    private static BuildingWeapon Weapon(int id)
    {
        using var q = PlayModeMatch.Em.CreateEntityQuery(typeof(BuildingData), typeof(BuildingWeapon));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities)
            if (PlayModeMatch.Em.GetComponentData<BuildingData>(e).id == id) return PlayModeMatch.Em.GetComponentData<BuildingWeapon>(e);
        return new BuildingWeapon { TargetId = -2 };
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
