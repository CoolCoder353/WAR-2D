using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using WAR2D.UI;

/// <summary>The UI Toolkit HUD in a real hosted match on Map_2.</summary>
public class HudTests
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

    /// <summary>The HUD's root visual element in the match scene.</summary>
    public static VisualElement HudRoot()
    {
        HudController hud = Object.FindAnyObjectByType<HudController>();
        return hud != null ? hud.GetComponent<UIDocument>().rootVisualElement : null;
    }

    /// <summary>The first number in a label's text, or NaN.</summary>
    public static float Number(string text)
    {
        Match m = Regex.Match(text ?? "", @"-?[\d,]+(\.\d+)?");
        return m.Success ? float.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture) : float.NaN;
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator TopBarShowsResourcesIncomeAndUpkeep()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);

        // A Miner facing a gem tile, anywhere it may be placed.
        WorldStateManager wsm = WorldStateManager.Instance;
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        var grid = wsm.Map.Grid;
        bool placed = false;
        for (int y = 1; y < grid.Height - 1 && !placed; y++)
        for (int x = 1; x < grid.Width - 1 && !placed; x++)
        foreach (float rotation in new[] { 0f, 90f, 180f, 270f })
        {
            var anchor = new int2(x, y);
            if (grid.TileAt(anchor + MinerRules.FacingOffset(rotation)) != TileType.Gem) continue;
            if (wsm.CheckPlacement(BuildingType.Miner, anchor, rotation, local) != PlacementResult.Ok) continue;
            wsm.TryAddBuilding(anchor, BuildingType.Miner, rotation);
            placed = true;
            break;
        }
        Assert.That(placed, Is.True, "Map_2 should have a buildable spot facing a gem");

        yield return new WaitForSeconds(2.5f);
        VisualElement root = HudRoot();
        Assert.That(root, Is.Not.Null, "Map_2 has a HUD UIDocument");
        float resources = Number(root.Q<Label>("resources-value").text);
        float income = Number(root.Q<Label>("income-value").text);
        float upkeep = Number(root.Q<Label>("upkeep-value").text);
        Assert.That(resources, Is.GreaterThan(0f));
        Assert.That(income, Is.GreaterThanOrEqualTo(10f), "passive income plus the Miner");
        Assert.That(upkeep, Is.Not.NaN);
    }
}
