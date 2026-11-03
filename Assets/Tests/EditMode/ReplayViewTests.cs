using NUnit.Framework;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Client.Replay;
using WAR2D.Net.Replay;
using WAR2D.Net.Replication;

/// <summary>v0.18 replay format 3: team fog round-trips; the per-player view's units, ghosts and bombs; seek-bar marks.</summary>
public class ReplayViewTests
{
    private const int W = 8, H = 8, Cell = 2, Me = 11, Them = 22;

    private static byte[] Encode(byte[] visible, byte[] explored, NativeArray<byte> last, int owner = Me)
    {
        using var v = new NativeArray<byte>(visible, Allocator.TempJob);
        using var e = new NativeArray<byte>(explored, Allocator.TempJob);
        using var owners = new NativeArray<int>(new[] { owner }, Allocator.TempJob);
        using var output = new NativeList<byte>(64, Allocator.TempJob);
        new TeamFogEncodeJob { Visible = v, Explored = e, OwnerBySlot = owners, Last = last, Out = output, FogCells = W * H, Slots = 1 }.Run();
        return output.AsArray().ToArray();
    }

    [Test]
    public void TheFogRoundTripsAndOnlyChangesAreWritten()
    {
        var visible = new byte[W * H];
        var explored = new byte[W * H];
        visible[3] = 1; visible[4] = 1; explored[3] = 1; explored[4] = 1; explored[40] = 1;
        using var last = new NativeArray<byte>(W * H, Allocator.TempJob);
        byte[] first = Encode(visible, explored, last);
        var grid = new byte[W * H];
        Assert.IsTrue(TeamFogCodec.Apply(new ArraySegment<byte>(first), W * H, _ => grid));
        Assert.AreEqual((byte)FogState.Visible, grid[3]);
        Assert.AreEqual((byte)FogState.Visible, grid[4]);
        Assert.AreEqual((byte)FogState.Explored, grid[40]);
        Assert.AreEqual((byte)FogState.Unexplored, grid[5]);

        Assert.IsEmpty(Encode(visible, explored, last), "nothing changed: nothing written");

        visible[3] = 0;
        byte[] second = Encode(visible, explored, last);
        Assert.IsTrue(TeamFogCodec.Apply(new ArraySegment<byte>(second), W * H, _ => grid));
        Assert.AreEqual((byte)FogState.Explored, grid[3], "left sight: explored");
        Assert.AreEqual((byte)FogState.Visible, grid[4]);
    }

    [Test]
    public void AMalformedPayloadIsRefused()
    {
        Assert.IsFalse(TeamFogCodec.Apply(new ArraySegment<byte>(new byte[] { 1, 2 }), W * H, _ => new byte[W * H]));
        byte[] tooFar = { 11, 0, 0, 0, 1, 0, 200, 1, 2 }; // a run past the grid
        Assert.IsFalse(TeamFogCodec.Apply(new ArraySegment<byte>(tooFar), W * H, _ => new byte[W * H]));
    }

    private static ClientBuildings.Entry Building(int id, int owner, BuildingType type, float x, float y) => new ClientBuildings.Entry
    {
        Data = new BuildingData { id = id, ownerId = owner, buildingType = type, position = new float2(x, y) },
    };

    private static ReplayView ViewWithSight(params int[] visibleCells)
    {
        var view = new ReplayView(W, H, Cell);
        byte[] grid = view.GridOf(Me);
        foreach (int c in visibleCells) grid[c] = (byte)FogState.Visible;
        return view;
    }

    private static readonly ArraySegment<byte> Nothing = new ArraySegment<byte>(new byte[0]);

    [Test]
    public void UnitsShowOnlyWhereTheTeamSees()
    {
        ReplayView view = ViewWithSight(0);
        Assert.IsTrue(view.ShowsUnit(Me, Them, new float2(1, 1)), "in sight");
        Assert.IsFalse(view.ShowsUnit(Me, Them, new float2(9, 9)), "in fog");
        Assert.IsTrue(view.ShowsUnit(Me, Me, new float2(9, 9)), "own units always");
        Assert.IsTrue(view.ShowsUnit(0, Them, new float2(9, 9)), "Everything");
    }

    [Test]
    public void ABuildingSeenThenHiddenIsAGhostAsLastSeen()
    {
        ReplayView view = ViewWithSight(0);
        var all = new ClientBuildings();
        all.Entries[5] = Building(5, Them, BuildingType.Barracks, 1, 1);
        view.Fog(Nothing, all);
        view.GridOf(Me)[0] = (byte)FogState.Explored;
        var shown = new ClientBuildings();
        view.Fog(Nothing, all);
        view.BuildingsFor(Me, all, shown);
        Assert.IsTrue(shown.Entries[5].Ghost);
        all.Entries.Remove(5); // destroyed out of sight: the ghost stays
        view.Fog(Nothing, all);
        view.BuildingsFor(Me, all, shown);
        Assert.IsTrue(shown.Entries.ContainsKey(5));
        view.GridOf(Me)[0] = (byte)FogState.Visible; // and goes when they see the spot
        view.Fog(Nothing, all);
        view.BuildingsFor(Me, all, shown);
        Assert.IsFalse(shown.Entries.ContainsKey(5));
    }

    [Test]
    public void ABuildingNeverSeenIsNotShown()
    {
        ReplayView view = ViewWithSight();
        var all = new ClientBuildings();
        all.Entries[5] = Building(5, Them, BuildingType.Barracks, 9, 9);
        view.Fog(Nothing, all);
        var shown = new ClientBuildings();
        view.BuildingsFor(Me, all, shown);
        Assert.IsEmpty(shown.Entries);
    }

    [Test]
    public void AnotherPlayersBombIsNeverShownEvenInSight()
    {
        ReplayView view = ViewWithSight(0);
        var all = new ClientBuildings();
        all.Entries[5] = Building(5, Them, BuildingType.Bomb, 1, 1);
        all.Entries[6] = Building(6, Me, BuildingType.Bomb, 9, 9);
        view.Fog(Nothing, all);
        var shown = new ClientBuildings();
        view.BuildingsFor(Me, all, shown);
        Assert.IsFalse(shown.Entries.ContainsKey(5));
        Assert.IsTrue(shown.Entries.ContainsKey(6), "their own");
    }

    [Test]
    public void MarkersSeekALittleBeforeTheirEvent()
    {
        var data = new SummaryData
        {
            Times = new float[0], Players = new SummaryPlayer[0], MapSize = 100, HqOwners = new[] { Them }, HqX = new[] { 7f }, HqY = new[] { 8f },
            Eliminations = new[] { new SummaryElimination { Time = 100f, Owner = Them, Cause = EliminationCause.Surrendered } },
            Fights = new[] { new SummaryFight { Start = 3f, End = 63f, CentreX = 50f, CentreY = 50f, Owners = new[] { Me }, UnitsLost = new[] { 1 }, WorthLost = new[] { 100f }, WorthKilled = new[] { 0f } } },
        };
        List<ReplayMarker> marks = ReplayView.Markers(data, o => o == Me ? "Vex" : "Mako");
        Assert.AreEqual(2, marks.Count);
        Assert.IsTrue(marks[0].Fight);
        Assert.AreEqual(60f, marks[0].Length);
        StringAssert.StartsWith("0:03 · Big fight in the middle", marks[0].Text);
        Assert.AreEqual(0f, ReplayView.SeekTime(marks[0], 5f), "never before the start");
        Assert.AreEqual(95f, ReplayView.SeekTime(marks[1], 5f));
        Assert.AreEqual(new float2(7f, 8f), marks[1].At, "an elimination centres on their HQ");
    }
}
