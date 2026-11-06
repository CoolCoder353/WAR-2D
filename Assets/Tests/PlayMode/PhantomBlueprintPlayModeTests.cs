using System.Collections;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools;
using WAR2D.Net.Replication;
using WAR2D.Sim;

/// <summary>
/// Owner playtest 2026-11-06: a blueprint may go on explored ground the player doesn't see now, judged by what the player
/// knows. Something standing there unseen isn't told: the blueprint waits on the server and is cancelled with a refund
/// once the player's side sees the spot is taken.
/// </summary>
public class PhantomBlueprintPlayModeTests
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
    public IEnumerator ABlueprintOnHiddenGroundWaitsThenIsRefundedWhenTheSpotIsSeen()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        DevApi.AllowForTests = true;
        WorldStateManager wsm = WorldStateManager.Instance;
        int me = PlayModeMatch.LocalOwner;

        // Free ground well away from the HQ's sight.
        int2 anchor = default;
        bool found = false;
        var grid = wsm.Map.Grid;
        for (int y = 4; y < grid.Height - 4 && !found; y += 3)
        for (int x = 4; x < grid.Width - 4 && !found; x += 3)
        {
            var at = new int2(x, y);
            if (math.distance(at, hq) < 45f) continue;
            found = Footprint.Tiles(at, WorldStateManager.GetBuildingSize(BuildingType.Barracks)).All(t => TileRules.IsFloor(grid.TileAt(t)) && !wsm.Map.IsUsed(t))
                    && TileRules.IsFloor(grid.TileAt(at + new int2(4, 0)));
            if (found) anchor = at;
        }
        Assert.That(found, Is.True, "free ground far from the HQ");

        // Explore it with a Scout, then lose sight of it.
        int scout = 0;
        Assert.That(wsm.DevSpawnUnit(me, (float2)anchor + new float2(4.5f, 0.5f), id => scout = id, UnitType.Scout), Is.True);
        yield return PlayModeMatch.WaitUntil(() => scout != 0 && wsm.SeesNow(me, anchor), 10f, () => "the Scout sees the spot");
        wsm.Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, Ids = new[] { scout } });
        yield return PlayModeMatch.WaitUntil(() => !wsm.SeesNow(me, anchor) && wsm.HasExplored(me, anchor), 10f, () => "explored, not seen");

        // Something the player can't see now takes the ground.
        Assert.That(wsm.DevPlaceBuilding(Bot, BuildingType.Barracks, anchor, 1e6f), Is.True);
        yield return PlayModeMatch.WaitUntil(() => wsm.Map.IsUsed(anchor), 5f);
        yield return null;
        Assert.That(ClientBuildings.Current.Entries.Values.Any(e => e.Data.ownerId == Bot), Is.False, "the client isn't told");

        // The preview and the placement say nothing of it.
        Assert.That(wsm.CheckPlacement(BuildingType.Barracks, anchor, 0f, me, out bool phantom), Is.EqualTo(PlacementResult.Ok));
        Assert.That(phantom, Is.True);
        ServerPlayer player = GameCore.Instance.GetServerPlayerById(me);
        float cost = config.GetBuilding(BuildingType.Barracks).UpfrontCost;
        float before = player.Resources;
        Assert.That(PlayerActions.PlaceBuilding(me, anchor, BuildingType.Barracks, 0f), Is.True);
        Assert.That(player.Resources, Is.EqualTo(before - cost).Within(0.01f), "paid like any blueprint");
        Assert.That(wsm.PhantomCount, Is.EqualTo(1));
        yield return PlayModeMatch.WaitUntil(() => ClientBuildings.Current.Entries.Values.Any(e => e.Data.ownerId == me && e.Data.buildingType == BuildingType.Barracks && e.Progress == 0),
            5f, () => "the owner sees a blueprint");

        // Seeing the spot again cancels it and refunds it in full.
        float paid = player.Resources;
        Assert.That(wsm.DevSpawnUnit(me, (float2)anchor + new float2(4.5f, 0.5f), null, UnitType.Scout), Is.True);
        yield return PlayModeMatch.WaitUntil(() => wsm.PhantomCount == 0, 10f, () => "the waiting blueprint is resolved");
        Assert.That(player.Resources, Is.GreaterThanOrEqualTo(paid + cost - 1f), "refunded in full");
        yield return PlayModeMatch.WaitUntil(() => !ClientBuildings.Current.Entries.Values.Any(e => e.Data.ownerId == me && e.Data.buildingType == BuildingType.Barracks),
            5f, () => "the blueprint is gone on the client");
        Assert.That(wsm.Map.IsUsed(anchor), Is.True, "the other building still stands");
    }
}
