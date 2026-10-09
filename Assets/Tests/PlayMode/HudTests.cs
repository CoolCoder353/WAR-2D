using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using WAR2D.Client;
using WAR2D.Net.Replication;
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
    public IEnumerator TearDown()
    {
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.TearDown();
    }

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

    /// <summary>Clicks a UI Toolkit button as keyboard submit does (runs its clicked handlers).</summary>
    public static void Click(Button button)
    {
        Assert.That(button, Is.Not.Null);
        Assert.That(button.enabledInHierarchy, Is.True, $"{button.name} should be enabled");
        using (NavigationSubmitEvent e = NavigationSubmitEvent.GetPooled())
        {
            e.target = button;
            button.SendEvent(e);
        }
    }

    /// <summary>Starts a match, places the HQ and a spawner, and returns the spawner as the client knows it.</summary>
    private static IEnumerator WithSpawner(GameConfigData config, System.Action<BuildingData> spawner)
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        WorldStateManager wsm = WorldStateManager.Instance;
        ClientPlayer local = NetworkClient.localPlayer.GetComponent<ClientPlayer>();
        Assert.That(wsm.TryFindBuildableAnchor(BuildingType.SmallUnitSpawner, 0f, local, out int2 anchor), Is.True);
        wsm.TryAddBuilding(anchor, BuildingType.SmallUnitSpawner, 0f);
        BuildingData found = default;
        yield return PlayModeMatch.WaitUntil(() =>
        {
            foreach (GameObject go in UnitCommander.Instance.buildingGameObjects.Values)
            {
                BuildingData data = go.GetComponent<BuildingDataClient>().buildingData;
                if (data.buildingType != BuildingType.SmallUnitSpawner) continue;
                found = data;
                return true;
            }
            return false;
        }, 10f);
        spawner(found);
    }

    /// <summary>The local player's units the client knows, anywhere on the map.</summary>
    private static List<int> OwnUnits()
    {
        var ids = new List<int>();
        (int2 min, int2 max) = WorldStateManager.Instance.MapBounds;
        WAR2D.Client.ClientWorld.Instance?.QueryBox(min, max + 1, PlayModeMatch.LocalOwner, ids);
        return ids;
    }

    /// <summary>Queues units with the production + button and waits until the client knows them.</summary>
    private static IEnumerator ProduceUnits(VisualElement root, BuildingData spawner, int count)
    {
        UnitCommander.Instance.SelectBuilding(spawner);
        yield return PlayModeMatch.WaitUntil(() => root.Q<Label>("command-title").text == "Commands: Spawner selected", 5f);
        for (int i = 0; i < count; i++)
        {
            Click(root.Q<Button>("production-plus"));
            yield return null;
        }
        yield return PlayModeMatch.WaitUntil(() => OwnUnits().Count >= count, 20f);
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator ProductionPlusMinusChangesQueue()
    {
        BuildingData spawner = default;
        yield return WithSpawner(config, s => spawner = s);
        ServerPlayer server = GameCore.Instance.GetServerPlayerById(PlayModeMatch.LocalOwner);
        bool Broke() { server.TrySpend(server.Resources); return true; } // nothing can spawn, so the queue only changes by the buttons

        VisualElement root = HudRoot();
        UnitCommander.Instance.SelectBuilding(spawner);
        yield return PlayModeMatch.WaitUntil(() => Broke() && root.Q<Label>("command-title").text == "Commands: Spawner selected", 5f);
        Assert.That(root.Q<Button>("production-minus").enabledSelf, Is.False, "an empty queue can't go lower");

        for (int i = 0; i < 3; i++)
        {
            Click(root.Q<Button>("production-plus"));
            yield return null;
        }
        yield return PlayModeMatch.WaitUntil(() => Broke() && root.Q<Label>("production-count").text == "3", 5f);
        Click(root.Q<Button>("production-minus"));
        yield return PlayModeMatch.WaitUntil(() => Broke() && root.Q<Label>("production-count").text == "2", 5f);

        // Random and foreign ids are refused without changing anything.
        var rng = new System.Random(5);
        for (int i = 0; i < 20; i++) WorldStateManager.Instance.CmdDequeueUnit(rng.Next(int.MinValue, int.MaxValue));
        yield return new WaitForSeconds(0.5f);
        Broke();
        Assert.That(SpawnerCount(spawner.id), Is.EqualTo(2), "the server's queue matches the HUD");
        Assert.That(root.Q<Label>("production-count").text, Is.EqualTo("2"));
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator CommandCardStopHalts()
    {
        BuildingData spawner = default;
        yield return WithSpawner(config, s => spawner = s);
        VisualElement root = HudRoot();
        yield return ProduceUnits(root, spawner, 2);

        Selection selection = UnitCommander.Instance.Selection;
        selection.SelectIds(OwnUnits());
        (int2 min, int2 max) = WorldStateManager.Instance.MapBounds;
        float2 start = Centre(selection);
        int2 far = start.x < (min.x + max.x) / 2 ? new int2(max.x - 2, (int)start.y) : new int2(min.x + 2, (int)start.y);
        selection.Order(OrderKind.Move, false, far);
        yield return PlayModeMatch.WaitUntil(() => math.distance(Centre(selection), start) > 1f, 10f);

        yield return PlayModeMatch.WaitUntil(() => root.Q<Button>("cmd-stop").enabledInHierarchy, 2f);
        Click(root.Q<Button>("cmd-stop"));
        yield return new WaitForSeconds(1f); // the order reaches the server and the corrections come back
        float2 stopped = Centre(selection);
        yield return new WaitForSeconds(1f);
        Assert.That(math.distance(Centre(selection), stopped), Is.LessThan(0.3f), "Stop halts the selection");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator SquadBarShowsCounts()
    {
        BuildingData spawner = default;
        yield return WithSpawner(config, s => spawner = s);
        VisualElement root = HudRoot();
        Assert.That(root.Q<Label>("squad-count-0").text, Is.EqualTo("–"), "an empty squad shows a dash");
        yield return ProduceUnits(root, spawner, 2);

        Selection selection = UnitCommander.Instance.Selection;
        selection.SelectIds(OwnUnits().GetRange(0, 2));
        selection.AssignSquad(0);
        yield return PlayModeMatch.WaitUntil(() => root.Q<Label>("squad-count-0").text == "2", 5f);
        Assert.That(root.Q<Label>("squad-count-1").text, Is.EqualTo("–"));

        selection.SelectIds(new int[0]);
        Click(root.Q<Button>("squad-0"));
        yield return null;
        Assert.That(selection.ActiveSquad, Is.EqualTo(0), "clicking a squad selects it");
    }

    private static float2 Centre(Selection selection)
    {
        float2 sum = 0f;
        int n = 0;
        foreach (int id in selection.Selected)
        {
            if (!WAR2D.Client.ClientWorld.Instance.TryGet(id, out WAR2D.Client.ClientUnitView view)) continue;
            sum += view.Position;
            n++;
        }
        return n > 0 ? sum / n : float2.zero;
    }

    private static int SpawnerCount(int buildingId)
    {
        EntityManager em = PlayModeMatch.Em;
        using EntityQuery q = em.CreateEntityQuery(typeof(BuildingData), typeof(SpawnerData));
        using Unity.Collections.NativeArray<Entity> entities = q.ToEntityArray(Unity.Collections.Allocator.Temp);
        foreach (Entity e in entities)
            if (em.GetComponentData<BuildingData>(e).id == buildingId) return em.GetComponentData<SpawnerData>(e).count;
        Assert.Fail("spawner not found");
        return -1;
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator MinimapClickMovesCamera()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        MinimapController minimap = Object.FindAnyObjectByType<HudController>().Minimap;
        yield return PlayModeMatch.WaitUntil(() => minimap.Texture != null, 10f);
        yield return null;

        VisualElement image = HudRoot().Q("minimap-image");
        Vector2 size = image.layout.size;
        Assert.That(size.x, Is.GreaterThan(0f), "the minimap image is laid out");
        var local = new Vector2(size.x * 0.25f, size.y * 0.75f); // lower-left quarter
        minimap.Click(local, 0, false);
        float2 expected = MinimapTexture.ToWorld(local, size, minimap.WorldSize);
        Vector3 cam = Camera.main.transform.position;
        Assert.That(math.distance(new float2(cam.x, cam.y), expected), Is.LessThan(1.5f), "the camera centres on the clicked point");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator MinimapRightClickOrdersSelection()
    {
        BuildingData spawner = default;
        yield return WithSpawner(config, s => spawner = s);
        VisualElement root = HudRoot();
        yield return ProduceUnits(root, spawner, 1);
        MinimapController minimap = Object.FindAnyObjectByType<HudController>().Minimap;
        yield return PlayModeMatch.WaitUntil(() => minimap.Texture != null, 10f);

        Selection selection = UnitCommander.Instance.Selection;
        selection.SelectIds(OwnUnits());
        float2 start = Centre(selection);
        Vector2 size = root.Q("minimap-image").layout.size;
        // A point 12 tiles away on the map, as a minimap position.
        (int2 min, int2 max) = WorldStateManager.Instance.MapBounds;
        float2 goal = start.x < (min.x + max.x) / 2f ? start + new float2(12, 0) : start - new float2(12, 0);
        minimap.Click(MinimapTexture.ToLocal(goal, size, minimap.WorldSize), 1, false);
        yield return PlayModeMatch.WaitUntil(() => math.distance(Centre(selection), goal) < math.distance(start, goal) - 2f, 10f);
    }

    private const int Bot = 1000002;

    /// <summary>Adds a hostile bot with an indestructible HQ as far from <paramref name="from"/> as fits; returns its anchor.</summary>
    private static int2 AddBotWithHQ(int2 from)
    {
        DevApi.AllowForTests = true;
        GameCore.Instance.AddBot(Bot, 1e9f);
        WorldStateManager wsm = WorldStateManager.Instance;
        (int2 min, int2 max) = wsm.MapBounds;
        int2 far = new int2(from.x < (min.x + max.x) / 2 ? max.x - 6 : min.x + 6, from.y < (min.y + max.y) / 2 ? max.y - 6 : min.y + 6);
        for (int ring = 0; ring < 40; ring++)
        for (int i = 0; i < math.max(1, 8 * ring); i++)
        {
            int2 anchor = Ring(far, ring, i);
            if (wsm.DevPlaceBuilding(Bot, BuildingType.Base, anchor, 1e7f)) return anchor;
        }
        Assert.Fail("no room for the bot's HQ");
        return default;
    }

    /// <summary>Spawns units for an owner on walkable tiles in rings around a point (from ring 2, clear of a 3×3 building).</summary>
    private static void SpawnAround(int owner, int2 centre, int count)
    {
        WorldStateManager wsm = WorldStateManager.Instance;
        int placed = 0;
        for (int ring = 2; placed < count && ring < 20; ring++)
        for (int i = 0; i < 8 * ring && placed < count; i++)
        {
            int2 tile = Ring(centre, ring, i);
            if (!wsm.Map.Grid.IsWalkable(tile)) continue;
            wsm.DevSpawnUnit(owner, (float2)tile + 0.5f);
            placed++;
        }
    }

    private static int2 Ring(int2 origin, int ring, int i)
    {
        if (ring == 0) return origin;
        int side = 2 * ring;
        if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
        i -= side;
        if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
        i -= side;
        if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
        i -= side;
        return new int2(origin.x - ring, origin.y + ring - i);
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator UnderAttackAlertAndPing()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        AddBotWithHQ(hq);
        SpawnAround(Bot, hq, 6); // the bot's units attack the HQ

        VisualElement root = HudRoot();
        yield return PlayModeMatch.WaitUntil(() => root.Q("alert-feed").childCount > 0, 15f);
        StringAssert.StartsWith("Under attack", root.Q("alert-feed")[0].Q<Label>("alert-text").text);
        Assert.That(root.Q("minimap-pings").childCount, Is.GreaterThan(0), "the attack is pinged on the minimap");

        Click(root.Q("alert-feed")[0].Q<Button>("alert-dismiss"));
        yield return null;
        Assert.That(root.Q("alert-feed").childCount, Is.EqualTo(0), "× dismisses the alert");
    }

    [UnityTest, Timeout(90000)]
    public IEnumerator DiplomacyToggleStopsMyUnitsAttacking()
    {
        yield return PlayModeMatch.StartMatchAsHost(config);
        GameCore.Instance.DiplomacyEnabled = true;
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        int2 botHQ = AddBotWithHQ(hq);
        int me = PlayModeMatch.LocalOwner;
        SpawnAround(me, botHQ, 8); // my units attack the bot's HQ, which has no units to fight back with

        int mine = 0;
        WAR2D.Sim.SimContext sim = WorldStateManager.Instance.Sim;
        System.Action<WAR2D.Sim.SimData, int, int> count = (data, tick, n) =>
        {
            foreach (int2 attack in data.AttackEvents)
            {
                int slot = data.SlotOf(attack.x, n);
                if (slot >= 0 && data.OwnerId[slot] == me) mine++;
            }
        };
        sim.Settled += count;
        try
        {
            yield return PlayModeMatch.WaitUntil(() => mine > 0, 15f);

            VisualElement root = HudRoot();
            HudController hud = Object.FindAnyObjectByType<HudController>();
            yield return PlayModeMatch.WaitUntil(() => !root.Q<Button>("diplomacy-button").ClassListContains("top-bar__hidden"), 5f);
            Click(root.Q<Button>("diplomacy-button"));
            yield return null;
            Assert.That(hud.Diplomacy.IsOpen, Is.True, "the Diplomacy button opens the panel");

            // The bot has no player object, so its row shows as eliminated with its switches disabled;
            // send the attack switch's event the way an enabled switch does.
            // Bots join after match start, so resend everyone's row once the bot has a slot.
            yield return PlayModeMatch.WaitUntil(() => sim.TrySlotOf(Bot, out _), 5f);
            GameCore.Instance.SendAllDiplomacy();
            int botBit = 1 << GameCore.Instance.PlayerOrder.IndexOf(Bot);
            yield return PlayModeMatch.WaitUntil(() => (hud.Model.AttackMask & botBit) != 0, 5f);
            Assert.That(hud.Model.AttackMask & botBit, Is.Not.Zero, "starts attacking the bot");
            hud.SetAttack(Bot, false);
            yield return PlayModeMatch.WaitUntil(() => (hud.Model.AttackMask & botBit) == 0, 5f);
            yield return new WaitForSeconds(1f);
            mine = 0;
            yield return new WaitForSeconds(1f);
            Assert.That(mine, Is.EqualTo(0), "my units stop firing once I stop attacking");
            Assert.That(GameCore.Instance.CurrentState, Is.EqualTo(GameState.Playing), "the bot still attacks me, so nobody is at peace");
        }
        finally
        {
            sim.Settled -= count;
        }
    }
}
