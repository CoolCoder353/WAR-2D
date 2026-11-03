using System.Collections;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>v0.17 Heartland in a hosted match: a Miner facing a rich gem earns MiningRate × RichGemMultiplier.</summary>
public class RichGemPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        config.Match.Map.Size = 256; // a generated map: Map_2's authored tilemaps have no rich gems
        config.Match.Map.Seed = 3;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.TearDown();
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator AMinerOnARichGemEarnsTheRichRate()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);

        // A Miner facing a rich gem, anywhere it may be placed (by the dev API, so unexplored ground will do).
        WorldStateManager wsm = WorldStateManager.Instance;
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        var grid = wsm.Map.Grid;
        bool placed = false;
        for (int y = 1; y < grid.Height - 1 && !placed; y++)
        for (int x = 1; x < grid.Width - 1 && !placed; x++)
        foreach (float rotation in new[] { 0f, 90f, 180f, 270f })
        {
            var anchor = new int2(x, y);
            if (grid.TileAt(anchor + MinerRules.FacingOffset(rotation)) != TileType.RichGem) continue;
            PlacementResult result = wsm.CheckPlacement(BuildingType.Miner, anchor, rotation, local);
            if (result != PlacementResult.Ok && result != PlacementResult.Unexplored) continue;
            Assert.That(PlayModeMatch.PlaceBuilt(BuildingType.Miner, anchor, rotation), Is.True);
            placed = true;
            break;
        }
        Assert.That(placed, Is.True, "a 256 map on seed 3 has a buildable spot facing a rich gem");

        int owner = BuildingData.UIntToInt(local.netId);
        MatchStats stats = GameCore.Instance.Stats;
        // Start counting at the first payout, then count whole payouts over four seconds.
        float first = stats.Of(owner).Mined;
        float waited = 0f;
        while (stats.Of(owner).Mined <= first && waited < 5f) { waited += Time.deltaTime; yield return null; }
        float start = stats.Of(owner).Mined, startAt = Time.time;
        yield return new WaitForSeconds(4f);
        float rate = (stats.Of(owner).Mined - start) / (Time.time - startAt);
        float expected = config.Resources.MiningRate * config.Resources.RichGemMultiplier;
        Assert.That(rate, Is.InRange(expected * 0.75f, expected * 1.25f), $"mined {rate:0.0}/s, expected about {expected}/s");
    }
}
