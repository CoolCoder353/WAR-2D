using System;
using System.Collections.Generic;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Net.Replication;

/// <summary>Construction progress on the building wire.</summary>
public class BuildingProgressCodecTests
{
    private static BuildingView View(byte progress) => new BuildingView
    {
        Id = 5, OwnerId = 1, Type = BuildingType.Miner, Anchor = new int2(3, 3), Health = 10, MaxHealth = 50, Progress = progress, Position = new float2(3, 3),
    };

    [Test]
    public void EnterCarriesProgressAndProgressRecordsUpdateIt()
    {
        var interest = new BuildingInterest();
        var client = new ClientBuildings();
        var writer = new NetworkWriter();
        var present = new HashSet<int> { 5 };

        interest.Update(new List<BuildingView> { View(20) }, present, 1, _ => true, writer);
        Assert.IsTrue(BuildingCodec.Apply(client, writer.ToArraySegment()));
        Assert.AreEqual(20, client.Entries[5].Progress);

        writer.Position = 0;
        Assert.AreEqual(0, interest.Update(new List<BuildingView> { View(22) }, present, 1, _ => true, writer), "under the step: nothing sent");
        Assert.AreEqual(1, interest.Update(new List<BuildingView> { View(40) }, present, 1, _ => true, writer));
        Assert.IsTrue(BuildingCodec.Apply(client, writer.ToArraySegment()));
        Assert.AreEqual(40, client.Entries[5].Progress);

        writer.Position = 0;
        Assert.AreEqual(1, interest.Update(new List<BuildingView> { View(100) }, present, 1, _ => true, writer), "completion is always sent");
    }

    [Test]
    public void AHealthRecordCannotTakeABuildingPastItsRange()
    {
        var interest = new BuildingInterest();
        var client = new ClientBuildings();
        var writer = new NetworkWriter();
        interest.Update(new List<BuildingView> { View(100) }, new HashSet<int> { 5 }, 1, _ => true, writer);
        Assert.IsTrue(BuildingCodec.Apply(client, writer.ToArraySegment()));
        client.SetHealth(5, 100000);
        Assert.AreEqual(50f, client.Entries[5].Health.currentHealth, "clamped to the building's max health");
        client.SetHealth(5, -7);
        Assert.AreEqual(0f, client.Entries[5].Health.currentHealth);
    }

    [Test]
    public void MalformedProgressIsRejected()
    {
        var writer = new NetworkWriter();
        BuildingCodec.WriteEnter(writer, View(101));
        Assert.IsFalse(BuildingCodec.Apply(new ClientBuildings(), writer.ToArraySegment()));
        writer.Position = 0;
        BuildingCodec.WriteId(writer, BuildingRecord.Progress, 5, 200);
        Assert.IsFalse(BuildingCodec.Apply(new ClientBuildings(), writer.ToArraySegment()));
    }

    [Test]
    public void TheUnpaidRecordRoundTripsAndRejectsOtherValues()
    {
        var writer = new NetworkWriter();
        BuildingCodec.WriteEnter(writer, View(100));
        BuildingCodec.WriteId(writer, BuildingRecord.Unpaid, View(100).Id, 1);
        var into = new ClientBuildings();
        Assert.IsTrue(BuildingCodec.Apply(into, writer.ToArraySegment()));
        Assert.IsTrue(into.Entries[View(100).Id].Unpaid);
        writer.Position = 0;
        BuildingCodec.WriteId(writer, BuildingRecord.Unpaid, View(100).Id, 2);
        Assert.IsFalse(BuildingCodec.Apply(into, writer.ToArraySegment()));
    }
}
