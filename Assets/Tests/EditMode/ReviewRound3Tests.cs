using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>Bug review 2026-11-05 fixes on the sim side: an eliminated owner's walls, and an unpaid Radar.</summary>
public class ReviewRound3Tests
{
    [Test]
    public void AnEliminatedOwnersWallsGoAndNobodyElses()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(32));
        int2 mine = new int2(10, 10), theirs = new int2(14, 10);
        sim.PlaceWall(SimHarness.OwnerA, mine);
        sim.PlaceWall(SimHarness.OwnerB, theirs);
        sim.Tick();
        sim.ForceBuiltWall(mine);
        sim.ForceBuiltWall(theirs);

        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillOwner, OwnerId = SimHarness.OwnerA });
        sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        Assert.AreEqual(WallStates.None, data.WallState[sim.Map.Grid.Index(mine)], "the eliminated owner's wall is gone");
        Assert.AreEqual(TileType.Ground, sim.Map.Grid.TileAt(mine), "and its tile is ground again");
        Assert.AreEqual(WallStates.Built, data.WallState[sim.Map.Grid.Index(theirs)], "the other owner's wall stays");
        Assert.AreEqual(TileType.PlayerWall, sim.Map.Grid.TileAt(theirs));
    }

    [Test]
    public void AnUnpaidRadarDoesNotScan()
    {
        using var sim = new SimHarness(SimHarness.OpenMap(48));
        int radar = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Radar, new int2(20, 20), true);
        sim.Tick();
        sim.Tick();
        Assert.Greater(SightOf(sim, radar, out byte paidFlags), 0f);
        Assert.AreNotEqual(0, paidFlags & global::BuildingFlags.SeesThroughWalls);

        Unity.Entities.Entity e = sim.BuildingEntity(radar);
        var upkeep = new UpkeepComponent { ownerId = SimHarness.OwnerA, runningCostPerSecond = 1f, unpaid = true };
        if (sim.Em.HasComponent<UpkeepComponent>(e)) sim.Em.SetComponentData(e, upkeep);
        else sim.Em.AddComponentData(e, upkeep);
        sim.Tick();
        sim.Tick();
        Assert.AreEqual(ConstructionRules.BlueprintSight, SightOf(sim, radar, out byte unpaidFlags), "an unpaid Radar keeps no 20-tile sight");
        Assert.AreEqual(0, unpaidFlags & global::BuildingFlags.SeesThroughWalls);
    }

    private static float SightOf(SimHarness sim, int id, out byte flags)
    {
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        for (int b = 0; b < data.BuildingCount[0]; b++)
            if (data.BuildingIds[b] == id)
            {
                flags = data.BuildingFlag[b];
                return data.BuildingSight[b];
            }
        Assert.Fail("the building isn't in the sim");
        flags = 0;
        return 0f;
    }
}
