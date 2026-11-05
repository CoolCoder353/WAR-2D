using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Ai;
using WAR2D.AiView;
using WAR2D.Client.Overlays;
using WAR2D.Sim;
using WAR2D.UI;
using WAR2D.World;

/// <summary>The Large Radar and Large Jammer do what the small ones do, over 1.5 times the radius, by the same rules.</summary>
public class LargeSensorTests
{
    [TearDown]
    public void TearDown() => ConfigLoader.ResetForTests();

    private static readonly int2 At = new int2(40, 40);

    /// <summary>The Large Radar's centre (a 3×3 footprint at <see cref="At"/>) and points 25 and 35 tiles east of it.</summary>
    private static readonly float2 LargeCentre = Footprint.VisualCenter(At, new int2(3, 3)), Tiles25 = LargeCentre + new float2(25f, 0f), Tiles35 = LargeCentre + new float2(35f, 0f);

    private static void Pass(SimHarness sim, int ticks = 8)
    {
        for (int i = 0; i < ticks; i++) sim.Tick();
    }

    private static bool Sees(SimHarness sim, int owner, float2 p)
    {
        sim.Em.CompleteAllTrackedJobs();
        return sim.Context.Data.Sees(sim.Context.VisionOf(owner), p);
    }

    private static bool Jammed(SimHarness sim, int owner, float2 p)
    {
        sim.Em.CompleteAllTrackedJobs();
        return sim.Context.Data.Jammed(sim.Context.VisionOf(owner), p);
    }

    private static void SetUnpaid(SimHarness sim, int building, bool unpaid)
    {
        Unity.Entities.Entity e = sim.BuildingEntity(building);
        UpkeepComponent upkeep = sim.Em.GetComponentData<UpkeepComponent>(e);
        upkeep.unpaid = unpaid;
        sim.Em.SetComponentData(e, upkeep);
    }

    [Test]
    public void ShippedNumbersScaleTheSmallOnesAndLeaveThemAlone()
    {
        ConfigLoader.ResetForTests();
        GameConfigData c = ConfigLoader.LoadConfig();
        Assert.IsTrue(ConfigLoader.IsValid);
        BuildingConfig radar = c.GetBuilding(BuildingType.Radar), jammer = c.GetBuilding(BuildingType.Jammer);
        BuildingConfig largeRadar = c.GetBuilding(BuildingType.LargeRadar), largeJammer = c.GetBuilding(BuildingType.LargeJammer);
        Assert.AreEqual((200, 2, 2, 250f, 5f, 20f), (radar.Health, radar.Width, radar.Height, radar.UpfrontCost, radar.RunningCost, radar.Sight), "the Radar is unchanged");
        Assert.AreEqual((200, 2, 2, 200f, 6f, 6f, 12f), (jammer.Health, jammer.Width, jammer.Height, jammer.UpfrontCost, jammer.RunningCost, jammer.Sight, c.Siege.JamRadius), "the Jammer is unchanged");
        Assert.AreEqual((3, 3), (largeRadar.Width, largeRadar.Height));
        Assert.AreEqual((3, 3), (largeJammer.Width, largeJammer.Height));
        Assert.AreEqual(1.5f * radar.Sight, largeRadar.Sight);
        Assert.AreEqual(1.5f * c.Siege.JamRadius, c.Siege.LargeJamRadius);
        Assert.AreEqual(jammer.Sight, largeJammer.Sight, "its own sight matches the Jammer's");
        Assert.AreEqual((2.5f * radar.UpfrontCost, 2.5f * radar.RunningCost), (largeRadar.UpfrontCost, largeRadar.RunningCost));
        Assert.AreEqual((2.5f * jammer.UpfrontCost, 2.5f * jammer.RunningCost), (largeJammer.UpfrontCost, largeJammer.RunningCost));
        Assert.AreEqual(450, largeRadar.Health, "health, and so build work, scales with the footprint");
        Assert.AreEqual(450, largeJammer.Health);
    }

    [TestCase("<LargeJamRadius>0</LargeJamRadius>")]
    [TestCase("")]
    public void TheLargeJamRadiusIsRequiredAndPositive(string replacement)
    {
        var errors = new List<string>();
        ConfigParser.Parse(Regex.Replace(ConfigParserTests.ValidXml, "<LargeJamRadius>[^<]*</LargeJamRadius>", replacement), errors);
        Assert.That(errors, Has.Some.Contains("LargeJamRadius"));
    }

    [Test]
    public void AppendedSoTheBytesOnTheWireKeepTheirMeaning()
    {
        Assert.AreEqual((9, 10, 11), ((int)BuildingType.Jammer, (int)BuildingType.LargeRadar, (int)BuildingType.LargeJammer));
    }

    [Test]
    public void ALargeRadarSeesFogBeyondTheSmallRadius()
    {
        using (var small = new SimHarness(SimHarness.OpenMap(96)))
        {
            small.PlaceBuilding(SimHarness.OwnerA, BuildingType.Radar, At, complete: true);
            Pass(small);
            Assert.IsFalse(Sees(small, SimHarness.OwnerA, Tiles25), "the control: a Radar doesn't reach 25 tiles");
        }
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        int radar = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.LargeRadar, At);
        Pass(sim);
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, Tiles25), "a blueprint sees nothing");
        sim.AddWork(radar, 10000f);
        Pass(sim);
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, Tiles25), "finished: seen at 25");
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, Tiles35), "not at 35");
    }

    [Test]
    public void ALargeRadarSeesOverWalls()
    {
        var rows = new string[64];
        for (int r = 0; r < 64; r++)
            rows[r] = r == 0 || r == 63 ? new string('B', 64) : "B" + new string('.', 19) + "##" + new string('.', 41) + "B";
        using var sim = new SimHarness(MapStore.FromAscii(rows)); // wall at x = 20..21
        int radar = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.LargeRadar, new int2(14, 32), complete: true);
        Pass(sim);
        Assert.IsTrue(Sees(sim, SimHarness.OwnerA, new float2(40.5f, 32.5f)), "behind the wall, 25 tiles away");
        SetUnpaid(sim, radar, true);
        Pass(sim);
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, new float2(40.5f, 32.5f)), "unpaid: it sees like any building, not over walls");
    }

    [Test]
    public void ALargeJammerJamsBeyondTheSmallRadius()
    {
        float2 tiles15 = LargeCentre + new float2(15f, 0f), tiles21 = LargeCentre + new float2(21f, 0f);
        using (var small = new SimHarness(SimHarness.OpenMap(96)))
        {
            small.Spawn(SimHarness.OwnerA, new float2(10, 10));
            small.PlaceBuilding(SimHarness.OwnerB, BuildingType.Jammer, At, complete: true);
            Pass(small);
            Assert.IsFalse(Jammed(small, SimHarness.OwnerA, tiles15), "the control: a Jammer doesn't reach 15 tiles");
        }
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        int jammer = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.LargeJammer, At);
        Pass(sim);
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, LargeCentre), "a blueprint doesn't jam");
        sim.AddWork(jammer, 10000f);
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, tiles15), "finished: jammed at 15");
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, tiles21), "not at 21");
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerB, tiles15), "never for its owner");
        SetUnpaid(sim, jammer, true);
        Pass(sim);
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, tiles15), "unpaid: cleared");
    }

    [Test]
    public void BothRadiiComeFromTheConfig()
    {
        // Without the larger numbers the Large ones reach no farther than the small ones.
        using var sim = new SimHarness(SimHarness.OpenMap(96), c =>
        {
            c.Siege.LargeJamRadius = c.Siege.JamRadius;
            c.GetBuilding(BuildingType.LargeRadar).Sight = c.GetBuilding(BuildingType.Radar).Sight;
        });
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.LargeRadar, At, complete: true);
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.LargeJammer, new int2(40, 80), complete: true);
        Pass(sim);
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, Tiles25), "sight 20: not seen at 25");
        Assert.IsFalse(Jammed(sim, SimHarness.OwnerA, Footprint.VisualCenter(new int2(40, 80), new int2(3, 3)) + new float2(15f, 0f)), "radius 12: not jammed at 15");
    }

    [Test]
    public void ALargeRadarIsBlindInALargeJamAndPingsOnlyOutsideIt()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(96));
        var radarAt = new int2(10, 40);
        int radar = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.LargeRadar, radarAt, complete: true);
        float2 centre = Footprint.VisualCenter(radarAt, new int2(3, 3));
        float2 contact = centre + new float2(25f, 0f);
        sim.Spawn(SimHarness.OwnerB, contact);
        sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.LargeJammer, new int2(48, 40), complete: true); // 13 tiles from the contact: beyond a small Jammer
        Pass(sim);
        Assert.IsTrue(Jammed(sim, SimHarness.OwnerA, contact));
        Assert.IsFalse(Sees(sim, SimHarness.OwnerA, contact), "jammed: the Radar sees nothing there");

        sim.Em.CompleteAllTrackedJobs();
        var pings = new List<Alert>();
        var radars = new RadarService(0f, 0f);
        var view = new RadarView { Id = radar, OwnerId = SimHarness.OwnerA, Tile = radarAt, Centre = centre, Radius = 30f };
        radars.Update(sim.Context.Data, sim.Context.Clock.UnitCount, new[] { view }, 0, (owner, alert) => pings.Add(alert));
        CollectionAssert.IsEmpty(pings, "the only contact is jammed");

        sim.Spawn(SimHarness.OwnerB, new float2(centre.x, centre.y + 25f)); // in range, outside the jam
        Pass(sim);
        sim.Em.CompleteAllTrackedJobs();
        radars.Update(sim.Context.Data, sim.Context.Clock.UnitCount, new[] { view }, 10, (owner, alert) => pings.Add(alert));
        Assert.AreEqual(1, pings.Count, "this one pings");
    }

    private static readonly (GameState state, bool hasHq)[] Situations =
    {
        (GameState.PlacingHQ, false), (GameState.PlacingHQ, true), (GameState.Playing, false), (GameState.Playing, true), (GameState.Countdown, true),
    };

    [TestCase(BuildingType.LargeRadar, BuildingType.Radar)]
    [TestCase(BuildingType.LargeJammer, BuildingType.Jammer)]
    public void PlacementRefusesAndAllowsLikeTheSmallOne(BuildingType large, BuildingType small)
    {
        PlacementResult Check(BuildingType type, GameState state, bool hasHq, System.Func<int2, bool> used) =>
            PlacementRules.Check(type, At, 0f, ConfigLoader.LoadConfig().GetBuilding(type).Size, state, hasHq, _ => TileType.Ground, used, _ => false);

        foreach ((GameState state, bool hasHq) in Situations)
            Assert.AreEqual(Check(small, state, hasHq, _ => false), Check(large, state, hasHq, _ => false), $"{state}, HQ placed: {hasHq}");
        Assert.AreEqual(PlacementResult.Ok, Check(large, GameState.Playing, true, _ => false));
        Assert.AreEqual(PlacementResult.HQNotPlacedYet, Check(large, GameState.Playing, false, _ => false));

        // A tile in the 3×3 footprint but outside the 2×2 one blocks only the large building.
        List<int2> two = Footprint.Tiles(At, new int2(2, 2));
        int2 onlyLarge = Footprint.Tiles(At, new int2(3, 3)).First(t => !two.Contains(t));
        Assert.AreEqual(PlacementResult.Ok, Check(small, GameState.Playing, true, t => t.Equals(onlyLarge)));
        Assert.AreEqual(PlacementResult.TileBlocked, Check(large, GameState.Playing, true, t => t.Equals(onlyLarge)));
    }

    [Test]
    public void TheRangeRingsShowTheLargerReach()
    {
        GameConfigData c = ConfigLoader.LoadConfig();
        RingSet radar = RangeRules.OfBuilding(BuildingType.LargeRadar, c.GetBuilding(BuildingType.LargeRadar), SensorRules.JamRadius(BuildingType.LargeRadar, c.Siege), float2.zero, 7);
        Assert.AreEqual((30f, 0f), (radar.Range, radar.Sight), "the ring is its sight");
        RingSet jammer = RangeRules.OfBuilding(BuildingType.LargeJammer, c.GetBuilding(BuildingType.LargeJammer), SensorRules.JamRadius(BuildingType.LargeJammer, c.Siege), float2.zero, 7);
        Assert.AreEqual((18f, 6f), (jammer.Range, jammer.Sight));
    }

    [Test]
    public void TheSelectionPanelAndAlertsNameThem()
    {
        var row = new SelectionModel.BuildingRow { Type = BuildingType.LargeJammer };
        Assert.AreEqual("Jamming: radius 18", SelectionModel.StatusOf(new[] { row }, 9f, 18f).text);
        row.Unpaid = true;
        Assert.AreEqual("Unpaid: not jamming", SelectionModel.StatusOf(new[] { row }, 9f, 18f).text);
        Assert.AreEqual("Large Radar", AlertFeedModel.BuildingName(BuildingType.LargeRadar));
        Assert.AreEqual("Large Jammer", AlertFeedModel.BuildingName(BuildingType.LargeJammer));
        Assert.IsTrue(SelectionPanelController.TryIconOf(BuildingType.LargeRadar, out IconKind radar));
        Assert.AreEqual(IconKind.LargeRadar, radar);
        Assert.IsTrue(SelectionPanelController.TryIconOf(BuildingType.LargeJammer, out IconKind jammer));
        Assert.AreEqual(IconKind.LargeJammer, jammer);
    }

    [Test]
    public void TheManualListsThemWithTheirReach()
    {
        var manual = new ManualModel(ConfigLoader.LoadConfig(), action => action);
        foreach ((BuildingType type, string title, string stat, string value) in new[]
        {
            (BuildingType.LargeRadar, "Large Radar", "Radar range", "30 tiles"),
            (BuildingType.LargeJammer, "Large Jammer", "Jam radius", "18 tiles"),
        })
        {
            string id = ManualModel.PageOf(type);
            Assert.That(manual.Pages["buildings"].Cards, Does.Contain(id), title);
            Assert.AreEqual(title, manual.Pages[id].Title);
            Assert.That(manual.Pages[id].Stats, Has.Some.Matches<(string label, string value, string link)>(s => s.label == stat && s.value == value), title);
            Assert.That(manual.Pages[id].Stats, Has.Some.Matches<(string label, string value, string link)>(s => s.label == "Time for one Builder" && s.value == "18 s"), title);
        }
    }

    [Test]
    public void AnAiIgnoresThemWithoutBreaking()
    {
        var world = new AiSandbox(256, 5);
        world.MakeBrain(AiDifficulty.Hard, AiStyle.Balanced);
        world.ThinkNow();
        world.Run(2.0);
        world.State = GameState.Playing;
        int2 hq = world.Buildings.First(b => b.OwnerId == world.Ai && b.Type == BuildingType.Base).Anchor;
        world.AddBuilding(world.Enemy, BuildingType.LargeRadar, hq + new int2(14, 0));
        world.AddBuilding(world.Enemy, BuildingType.LargeJammer, hq + new int2(-14, 0));
        world.RevealAll();
        world.Run(120.0);
        Assert.That(world.View.Buildings.Any(b => b.Type == BuildingType.LargeRadar), Is.True, "it saw the Large Radar");
        Assert.That(world.Buildings.Any(b => b.OwnerId == world.Ai && (b.Type == BuildingType.LargeRadar || b.Type == BuildingType.LargeJammer)), Is.False, "AI players don't build them");
        CollectionAssert.IsEmpty(world.IllegalPlacements);
    }
}
