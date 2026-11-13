using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Net.Replication;

/// <summary>Tile → building lookup for clicks and box selection.</summary>
public class BuildingPickerTests
{
    private static int2 Size(BuildingType t) => t == BuildingType.Barracks ? new int2(2, 2) : new int2(1, 1);

    private static void Enter(ClientBuildings b, int id, BuildingType type, int2 anchor) =>
        b.Enter(new BuildingData { id = id, buildingType = type, position = anchor, ownerId = 1 },
            new HealthComponent { entityId = id, currentHealth = 10, maxHealth = 10 });

    [Test]
    public void EveryFootprintTilePicksItsBuilding()
    {
        var buildings = new ClientBuildings();
        var picker = new BuildingPicker(Size);
        picker.Attach(buildings);
        Enter(buildings, 7, BuildingType.Barracks, new int2(10, 10));
        foreach (int2 tile in Footprint.Tiles(new int2(10, 10), new int2(2, 2)))
        {
            Assert.IsTrue(picker.TryPick(tile, out int id), $"tile {tile}");
            Assert.AreEqual(7, id);
        }
        Assert.IsFalse(picker.TryPick(new int2(30, 30), out _));
    }

    [Test]
    public void RemovedBuildingsStopPickingAndAttachSeesExistingOnes()
    {
        var buildings = new ClientBuildings();
        Enter(buildings, 3, BuildingType.Miner, new int2(5, 5));
        var picker = new BuildingPicker(Size);
        picker.Attach(buildings);
        Assert.IsTrue(picker.TryPick(new int2(5, 5), out _), "known before Attach");
        buildings.Remove(3);
        Assert.IsFalse(picker.TryPick(new int2(5, 5), out _));
    }

    [Test]
    public void BoxFindsEachBuildingOnce()
    {
        var buildings = new ClientBuildings();
        var picker = new BuildingPicker(Size);
        picker.Attach(buildings);
        Enter(buildings, 1, BuildingType.Barracks, new int2(0, 0));
        Enter(buildings, 2, BuildingType.Miner, new int2(8, 8));
        var found = new List<int>();
        picker.InBox(new int2(0, 0), new int2(4, 4), found);
        CollectionAssert.AreEquivalent(new[] { 1 }, found);
    }

    /// <summary>A double-click on a building: every own building of its type on screen, or anywhere, blueprints included.</summary>
    [Test]
    public void OfTypeFindsOwnSeenBuildingsOfTheTypeInTheBoxOrEverywhere()
    {
        var buildings = new ClientBuildings();
        var picker = new BuildingPicker(Size);
        picker.Attach(buildings);
        Enter(buildings, 1, BuildingType.Barracks, new int2(0, 0));
        Enter(buildings, 2, BuildingType.Barracks, new int2(6, 6));   // only its corner is in the box
        Enter(buildings, 3, BuildingType.Barracks, new int2(40, 40)); // off screen
        Enter(buildings, 4, BuildingType.Miner, new int2(3, 3));      // another type
        Enter(buildings, 5, BuildingType.Barracks, new int2(2, 4));   // a blueprint
        Enter(buildings, 6, BuildingType.Barracks, new int2(4, 0));   // a ghost
        buildings.Enter(new BuildingData { id = 7, buildingType = BuildingType.Barracks, position = new int2(0, 4), ownerId = 2 },
            new HealthComponent { entityId = 7, currentHealth = 10, maxHealth = 10 }); // another player's
        ClientBuildings.Entry blueprint = buildings.Entries[5];
        blueprint.Progress = 10;
        buildings.Entries[5] = blueprint;
        ClientBuildings.Entry ghost = buildings.Entries[6];
        ghost.Ghost = true;
        buildings.Entries[6] = ghost;

        var found = new List<int>();
        picker.OfType(BuildingType.Barracks, 1, new int2(0, 0), new int2(6, 6), false, found);
        CollectionAssert.AreEquivalent(new[] { 1, 2, 5 }, found, "on screen");

        found.Clear();
        picker.OfType(BuildingType.Barracks, 1, new int2(0, 0), new int2(6, 6), true, found);
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 5 }, found, "anywhere on the map");

        found.Clear();
        picker.OfType(BuildingType.Radar, 1, new int2(0, 0), new int2(6, 6), true, found);
        Assert.IsEmpty(found, "none of that type");
    }
}
