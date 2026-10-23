using System.Collections;
using System.Collections.Generic;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// v0.14 <see cref="PlayerActions"/>, called with an AI owner id: each refuses what its Command refused (another
/// owner's things, the wrong state, out-of-range values) and accepts a valid action, so the AI passes the same checks.
/// </summary>
public class PlayerActionsTests
{
    private static readonly int Ai = AiOwnerIds.For(3);
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

    [UnityTest, Timeout(60000)]
    public IEnumerator NothingActsBeforePlaying()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        DevApi.AllowForTests = true;
        Assert.That(GameCore.Instance.DevAddScriptedAi(Ai, 10000f), Is.Not.Null);
        DevApi.AllowForTests = false;
        Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.PlacingHQ));
        Assert.That(PlayerActions.MayAct(Ai), Is.False);
        Assert.That(PlayerActions.Order(Ai, new[] { 1 }, OrderKind.Move, false, new int2(5, 5)), Is.False);
        Assert.That(PlayerActions.SetProduction(Ai, new[] { 1 }, UnitType.Tank), Is.False);
        Assert.That(PlayerActions.Surrender(Ai), Is.False, "only while Playing");
        Assert.That(PlayerActions.Gift(Ai, PlayModeMatch.LocalOwner, 10f), Is.False);
        Assert.That(WorldStateManager.Instance.TryFindBuildableAnchor(BuildingType.Base, 0f, Ai, out int2 hq), Is.True);
        Assert.That(PlayerActions.PlaceBuilding(Ai, hq, BuildingType.Barracks, 0f), Is.False, "no blueprint before the HQ");
        Assert.That(PlayerActions.PlaceHq(Ai, hq), Is.True, "the HQ goes up in PlacingHQ");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator EachActionRefusesWhatItsCommandRefused()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        int me = PlayModeMatch.LocalOwner;
        GameCore core = GameCore.Instance;
        WorldStateManager world = WorldStateManager.Instance;
        DevApi.AllowForTests = true;
        AiSeat seat = core.DevAddScriptedAi(Ai, 10000f);
        int2 aiHq = AiSeatPlayModeTests.FarFrom(world, hq, hq, Ai, BuildingType.Base);
        Assert.That(world.DevPlaceBuilding(Ai, BuildingType.Base, aiHq, 1e6f), Is.True);
        int2 aiBarracks = AiSeatPlayModeTests.FarFrom(world, hq, aiHq, Ai);
        Assert.That(world.DevPlaceBuilding(Ai, BuildingType.Barracks, aiBarracks, 0f), Is.True);
        Assert.That(world.TryFindBuildableAnchor(BuildingType.Barracks, 0f, me, out int2 myBarracks), Is.True);
        Assert.That(world.DevPlaceBuilding(me, BuildingType.Barracks, myBarracks, 0f), Is.True);
        var myUnit = new List<int>();
        Assert.That(world.TryFindSpawnTile(hq, out int2 myTile), Is.True);
        Assert.That(world.DevSpawnUnit(me, (float2)myTile + 0.5f, id => myUnit.Add(id)), Is.True);
        var aiUnit = new List<int>();
        Assert.That(world.TryFindSpawnTile(aiHq, out int2 aiTile), Is.True);
        Assert.That(world.DevSpawnUnit(Ai, (float2)aiTile + 0.5f, id => aiUnit.Add(id)), Is.True);
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.WaitUntil(() => myUnit.Count == 1 && aiUnit.Count == 1
            && AiSeatPlayModeTests.HasBuilding(Ai, BuildingType.Barracks) && AiSeatPlayModeTests.HasBuilding(me, BuildingType.Barracks), 10f);
        int[] mine = { AiSeatPlayModeTests.BuildingId(me, BuildingType.Barracks) };
        int[] own = { AiSeatPlayModeTests.BuildingId(Ai, BuildingType.Barracks) };
        Assert.That(PlayerActions.MayAct(Ai), Is.True);
        (int2 min, int2 max) = world.MapBounds;

        // Production: another owner's producer, unknown or wrong types, other steps, too many ids.
        Assert.That(PlayerActions.SetProduction(Ai, mine, UnitType.Scout), Is.False, "not its producer");
        Assert.That(PlayerActions.SetProduction(Ai, own, (byte)0), Is.False, "no type");
        Assert.That(PlayerActions.SetProduction(Ai, own, (byte)200), Is.False, "an unknown type");
        Assert.That(PlayerActions.SetProduction(Ai, own, UnitType.Heavy), Is.False, "a Barracks doesn't make Heavies");
        Assert.That(PlayerActions.SetProduction(Ai, new int[SpawnerRules.MaxSpawnersPerCommand + 1], UnitType.Scout), Is.False, "too many ids");
        Assert.That(PlayerActions.SetProduction(Ai, own, UnitType.Tank), Is.True, "a Barracks starts on Scouts");
        Assert.That(PlayerActions.SetProduction(Ai, own, UnitType.Tank), Is.False, "already making it");
        Assert.That(PlayerActions.ChangeQueues(Ai, mine, 1), Is.False, "not its producer");
        Assert.That(PlayerActions.ChangeQueues(Ai, own, 7), Is.False, "not a queue step");
        Assert.That(PlayerActions.ChangeQueues(Ai, own, 1), Is.True);
        Assert.That(PlayerActions.SetRally(Ai, own, max + 1, false), Is.False, "off the map");
        Assert.That(PlayerActions.SetRally(Ai, mine, aiHq, false), Is.False, "not its spawner");
        Assert.That(PlayerActions.SetRally(Ai, own, aiHq, false), Is.True);

        // Buildings: the fog, another owner's, the HQ, a too-long wall.
        Assert.That(PlayerActions.PlaceBuilding(Ai, myBarracks + new int2(0, 4), BuildingType.Miner, 0f), Is.False, "ground its side hasn't seen");
        Assert.That(PlayerActions.CancelBuildings(Ai, mine), Is.False, "not its building");
        Assert.That(PlayerActions.CancelBuildings(Ai, new[] { AiSeatPlayModeTests.BuildingId(Ai, BuildingType.Base) }), Is.False, "never the HQ");
        Assert.That(PlayerActions.PlaceWall(Ai, aiHq, aiHq + new int2(config.Siege.MaxWallLine + 5, 0)), Is.False, "too long");
        Assert.That(PlayerActions.PlaceHq(Ai, aiHq + new int2(10, 10)), Is.False, "one HQ, and only in PlacingHQ");

        // Orders: bad kinds and goals, an empty squad; another owner's units are ignored by the simulation.
        Assert.That(PlayerActions.Order(Ai, aiUnit, (OrderKind)99, false, aiHq), Is.False, "unknown order");
        Assert.That(PlayerActions.Order(Ai, aiUnit, OrderKind.Move, false, max + 5), Is.False, "goal off the map");
        Assert.That(PlayerActions.Order(Ai, new int[0], OrderKind.Move, false, aiHq), Is.False, "no units");
        Assert.That(PlayerActions.AssignSquad(Ai, 10, aiUnit), Is.False, "squads are 0..9");
        Assert.That(PlayerActions.OrderSquad(Ai, 3, OrderKind.Move, false, aiHq), Is.False, "an empty squad");
        Assert.That(PlayerActions.DigArea(Ai, aiUnit, false, min - 100, min - 50), Is.False, "a dig area off the map");
        Assert.That(PlayerActions.AssignSquad(Ai, 3, aiUnit), Is.True);
        Assert.That(PlayerActions.OrderSquad(Ai, 3, OrderKind.Stop, false, int2.zero), Is.True);

        float2 before = default, after = default;
        bool sampled = false;
        System.Action<WAR2D.Sim.SimData, int, int> sample = (data, tick, n) =>
        {
            int slot = data.SlotOf(myUnit[0], n);
            if (slot < 0) return;
            if (!sampled) { before = data.Positions[slot]; sampled = true; }
            after = data.Positions[slot];
        };
        world.Sim.Settled += sample;
        try
        {
            yield return PlayModeMatch.WaitUntil(() => sampled, 5f);
            int2 far = hq.x < (min.x + max.x) / 2 ? new int2(max.x - 3, hq.y) : new int2(min.x + 3, hq.y);
            Assert.That(PlayerActions.Order(Ai, myUnit, OrderKind.Move, false, far), Is.True, "queued: the simulation filters by owner");
            yield return new WaitForSeconds(1.5f);
            Assert.That(math.distance(before, after), Is.LessThan(0.5f), "the AI can't move my unit");
        }
        finally
        {
            world.Sim.Settled -= sample;
        }

        // Diplomacy: off in this match; then on, with its cooldown and no self-targeting.
        Assert.That(PlayerActions.SetAttack(Ai, me, false), Is.False, "the match has Diplomacy off");
        core.DiplomacyEnabled = true;
        Assert.That(PlayerActions.SetAttack(Ai, Ai, false), Is.False, "not itself");
        Assert.That(PlayerActions.SetAttack(Ai, me, false), Is.True);
        Assert.That(PlayerActions.SetAttack(Ai, me, true), Is.False, "the cooldown");
        Assert.That(PlayerActions.SetShareVision(Ai, me, true), Is.True, "a separate cooldown per command");

        // Gifts: to itself, more than it has, the cooldown.
        Assert.That(PlayerActions.Gift(Ai, Ai, 10f), Is.False, "not to itself");
        Assert.That(PlayerActions.Gift(Ai, me, seat.Player.Resources + 1000f), Is.False, "more than it has");
        Assert.That(PlayerActions.Gift(Ai, me, -5f), Is.False, "a negative amount");
        Assert.That(PlayerActions.Gift(Ai, me, 100f), Is.True);
        Assert.That(PlayerActions.Gift(Ai, me, 100f), Is.False, "one gift per cooldown");

        // Surrender: once; afterwards it may not act at all.
        Assert.That(PlayerActions.Surrender(Ai), Is.True);
        Assert.That(PlayerActions.Surrender(Ai), Is.False, "already out");
        Assert.That(PlayerActions.MayAct(Ai), Is.False);
        Assert.That(PlayerActions.ChangeQueues(Ai, own, 1), Is.False, "an eliminated player can't act");
        Assert.That(PlayerActions.Order(Ai, aiUnit, OrderKind.Stop, false, int2.zero), Is.False);
    }
}
