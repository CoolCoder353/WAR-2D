using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Net.Replication;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>
/// Leak tests: everything the server serialises to a client, decoded the way the client would, must be
/// about the client's own entities, or about something its grid (its sight plus vision shared with it)
/// sees right now. Never another player's fog, diplomacy, or units or buildings it cannot see.
/// </summary>
public class LeakTests
{
    private const int OwnerC = 303;

    /// <summary>One virtual client: decodes its streams and checks them on the settled world as they arrive.</summary>
    private sealed class Spy : IDisposable
    {
        public readonly int Owner;
        public readonly ClientUnitStore Units;
        public readonly ClientFog Fog = new ClientFog();
        public readonly ClientBuildings Buildings = new ClientBuildings();
        public readonly List<string> Failures = new List<string>();
        public int UnitsSeen, OthersSeen, FogPayloads, BuildingPayloads;
        private readonly SimContext sim;

        public Spy(SimContext sim, ReplicationService replication, int owner)
        {
            this.sim = sim;
            Owner = owner;
            Units = new ClientUnitStore(sim.Config.Simulation.MaxEntities, sim.Config.Simulation.TickSeconds);
            replication.AddVirtualClient(owner, Receive);
        }

        private void Receive(ArraySegment<byte> payload, int channel)
        {
            SimData data = sim.Data;
            int grid = sim.VisionOf(Owner);
            int count = sim.Clock.UnitCount;
            if (channel == ReplicationService.FogSinkChannel)
            {
                FogPayloads++;
                if (!FogCodec.Apply(Fog, payload)) { Failures.Add("malformed fog"); return; }
                for (int i = 0; i < Fog.State.Length; i++)
                {
                    bool visible = data.Visible[grid * data.FogCells + i] != 0;
                    if (visible != (Fog.State[i] == (byte)FogState.Visible)) { Failures.Add($"fog cell {i} is not this player's"); return; }
                }
                return;
            }
            if (channel == ReplicationService.BuildingSinkChannel)
            {
                BuildingPayloads++;
                if (!BuildingCodec.Apply(Buildings, payload)) { Failures.Add("malformed buildings"); return; }
                foreach (ClientBuildings.Entry e in Buildings.Entries.Values)
                    if (!e.Ghost && e.Data.ownerId != Owner && !data.Sees(grid, e.Data.position))
                        Failures.Add($"building {e.Data.id} known while unseen");
                return;
            }
            if (!Units.Apply(payload, 0)) { Failures.Add("malformed units"); return; }
            for (int k = 0; k < Units.Count; k++)
            {
                int id = Units.IdOf(Units.IndexAt(k));
                int slot = data.SlotOf(id, count);
                if (slot < 0) continue; // died this boundary: its Leave is in this payload or the next
                UnitsSeen++;
                if (data.OwnerId[slot] != Owner && !data.Sees(grid, data.Positions[slot]))
                    Failures.Add($"unit {id} of owner {data.OwnerId[slot]} known to {Owner} while unseen");
                if (data.OwnerId[slot] != Owner) OthersSeen++;
            }
        }

        public void Dispose() => Units.Dispose();
    }

    private static MapStore Map()
    {
        var rows = new string[96];
        for (int r = 0; r < 96; r++)
            rows[r] = r == 0 || r == 95 ? new string('B', 96) : "B" + new string('.', 46) + (r % 12 < 8 ? "##" : "..") + new string('.', 46) + "B";
        return MapStore.FromAscii(rows);
    }

    [Test]
    public void NothingHiddenIsEverSerialised([Values(1, 2, 3)] int seed)
    {
        using var sim = new SimHarness(Map());
        sim.Context.TeamResolver = owner => owner == OwnerC ? 1 : 0; // A and B are allies, C is alone
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var spies = new[] { new Spy(sim.Context, replication, SimHarness.OwnerA), new Spy(sim.Context, replication, SimHarness.OwnerB), new Spy(sim.Context, replication, OwnerC) };
        var random = new Unity.Mathematics.Random((uint)seed);
        int[] owners = { SimHarness.OwnerA, SimHarness.OwnerB, OwnerC };
        var ids = new List<int[]>[3] { new List<int[]>(), new List<int[]>(), new List<int[]>() };
        for (int o = 0; o < 3; o++)
            for (int u = 0; u < 40; u++)
                ids[o].Add(sim.Spawn(owners[o], new float2(random.NextFloat(2, 94), random.NextFloat(2, 94))));

        for (int t = 0; t < 120; t++)
        {
            if (t % 20 == 0) // a random diplomacy change every second
            {
                int from = owners[random.NextInt(3)], to = owners[random.NextInt(3)];
                SimCommandKind kind = random.NextBool() ? SimCommandKind.SetShareVision : SimCommandKind.SetAttack;
                sim.Context.Commands.Enqueue(new SimCommand { Kind = kind, OwnerId = from, TargetOwnerId = to, Flag = random.NextBool() });
            }
            if (t % 15 == 0)
                for (int o = 0; o < 3; o++)
                {
                    var some = new List<int>();
                    foreach (int[] id in ids[o]) if (id[0] > 0 && random.NextBool()) some.Add(id[0]);
                    sim.Move(owners[o], new int2(random.NextInt(2, 94), random.NextInt(2, 94)), some.ToArray());
                }
            sim.Tick();
        }

        foreach (Spy spy in spies)
        {
            CollectionAssert.IsEmpty(spy.Failures, $"owner {spy.Owner}");
            Assert.Greater(spy.FogPayloads, 0, "fog was sent");
            Assert.Greater(spy.UnitsSeen, 0, "units were sent");
            spy.Dispose();
        }
    }

    [Test]
    public void EnemyBuildingIsHiddenThenGhostedNotRemoved()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var views = new List<BuildingView>();
        replication.CollectBuildings = list => list.AddRange(views);
        var spy = new Spy(sim.Context, replication, SimHarness.OwnerA);
        int[] scout = sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        sim.Spawn(OwnerC, new float2(80, 80)); // gives C a slot, so its team is known
        sim.Tick();
        views.Add(new BuildingView { Id = 99, OwnerId = OwnerC, Type = BuildingType.Miner, Anchor = new int2(22, 20), Health = 50, MaxHealth = 50, Position = new float2(22, 20) });
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.IsTrue(spy.Buildings.Entries.ContainsKey(99), "seen while the scout is next to it");
        Assert.IsFalse(spy.Buildings.Entries[99].Ghost);

        sim.Teleport(scout[0], new float2(20, 80));
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsTrue(spy.Buildings.Entries[99].Ghost, "out of sight: a ghost");

        views.Clear(); // destroyed out of sight
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsTrue(spy.Buildings.Entries.ContainsKey(99), "the client must not learn of an unseen death");

        sim.Teleport(scout[0], new float2(20, 20));
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsFalse(spy.Buildings.Entries.ContainsKey(99), "seeing the spot again clears the ghost");
        CollectionAssert.IsEmpty(spy.Failures);
        spy.Dispose();
    }

    [Test]
    public void InterestNeverExceedsReceiverGrid()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var spy = new Spy(sim.Context, replication, SimHarness.OwnerB);
        sim.Spawn(SimHarness.OwnerB, new float2(10, 10));
        for (int u = 0; u < 5; u++) sim.Spawn(SimHarness.OwnerA, new float2(80, 80 - u));
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual(0, spy.OthersSeen, "A's units are out of B's sight");

        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = SimHarness.OwnerA, TargetOwnerId = SimHarness.OwnerB, Flag = true });
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.Greater(spy.OthersSeen, 0, "shared vision lets B know A's units");

        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = SimHarness.OwnerA, TargetOwnerId = SimHarness.OwnerB, Flag = false });
        for (int i = 0; i < 10; i++) sim.Tick();
        spy.OthersSeen = 0;
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual(0, spy.OthersSeen, "unsharing ends it");
        CollectionAssert.IsEmpty(spy.Failures);
        spy.Dispose();
    }

    [Test]
    public void NoClientReceivesAnotherPlayersAttackMask([Values(1, 2, 3)] int seed)
    {
        // TargetDiplomacy is the only message carrying masks, and it carries RowFor(receiver). Changing any
        // pair the receiver is not part of must leave the receiver's message unchanged.
        var random = new Unity.Mathematics.Random((uint)seed);
        var diplomacy = new Diplomacy();
        var slotByIndex = new List<int> { 0, 1, 2, 3, 4 };
        for (int s = 0; s < 5; s++)
        {
            var others = new List<(int, int)>();
            for (int t = 0; t < s; t++) others.Add((t, t % 2));
            diplomacy.Join(s, s % 2, others);
        }
        for (int step = 0; step < 200; step++)
        {
            int receiver = random.NextInt(5);
            var before = diplomacy.RowFor(receiver, slotByIndex);
            int from = random.NextInt(5), to = random.NextInt(5);
            if (from == receiver || to == receiver) { diplomacy.SetAttack(from, to, random.NextBool()); continue; }
            diplomacy.SetAttack(from, to, random.NextBool());
            diplomacy.SetShareVision(from, to, random.NextBool());
            Assert.AreEqual(before, diplomacy.RowFor(receiver, slotByIndex), $"receiver {receiver} learned about {from} -> {to}");
        }
    }
}
