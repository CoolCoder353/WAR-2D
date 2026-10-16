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
        public readonly ClientTerrain Terrain = new ClientTerrain();
        public readonly List<string> Failures = new List<string>();
        public int UnitsSeen, OthersSeen, FogPayloads, BuildingPayloads, TerrainPayloads;
        private readonly SimContext sim;

        public Spy(SimContext sim, ReplicationService replication, int owner)
        {
            this.sim = sim;
            Owner = owner;
            Units = new ClientUnitStore(sim.Config.Simulation.MaxEntities, sim.Config.Simulation.TickSeconds);
            Terrain.Init(sim.Map.Grid); // before any tick: the map is still as generated
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
            if (channel == ReplicationService.TerrainSinkChannel)
            {
                TerrainPayloads++;
                var cells = new List<int2>();
                Terrain.Changed += Collect;
                bool ok = TerrainCodec.Apply(Terrain, payload);
                Terrain.Changed -= Collect;
                if (!ok) { Failures.Add("malformed terrain"); return; }
                foreach (int2 tile in cells)
                    if (!data.Sees(grid, (float2)tile + 0.5f)) Failures.Add($"terrain {tile} sent while unseen");
                return;
                void Collect(IReadOnlyList<int2> changed) => cells.AddRange(changed);
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
    public void OnlyASpectatorSeesTheWholeMap()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        bool bDefeated = false;
        replication.Spectating = owner => owner == SimHarness.OwnerB && bDefeated;
        var b = new Spy(sim.Context, replication, SimHarness.OwnerB);
        var c = new Spy(sim.Context, replication, OwnerC);
        sim.Spawn(SimHarness.OwnerB, new float2(10, 10));
        sim.Spawn(OwnerC, new float2(10, 85));
        for (int u = 0; u < 5; u++) sim.Spawn(SimHarness.OwnerA, new float2(80, 80 - u));
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual(0, b.OthersSeen, "before defeat, B sees only its own sight");
        CollectionAssert.IsEmpty(b.Failures);

        bDefeated = true;
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.Greater(b.OthersSeen, 0, "a spectator is sent every unit");
        foreach (byte cell in b.Fog.State) Assert.AreEqual((byte)FogState.Visible, cell, "and a clear fog");
        CollectionAssert.IsEmpty(c.Failures, "a live player never gets more than its own grid");
        Assert.AreEqual(0, c.OthersSeen);
        b.Dispose();
        c.Dispose();
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

    /// <summary>A fight between A and C with an alert service on the settled world, checking every alert as it is sent.</summary>
    private static List<string> FightWithAlerts(int seed, out int sent)
    {
        using var sim = new SimHarness(Map());
        var failures = new List<string>();
        int count = 0;
        SimData settled = default;
        int units = 0;
        var alerts = new AlertService(0f, 4, (receiver, list) =>
        {
            foreach (Alert alert in list)
            {
                count++;
                if (alert.Kind == AlertKind.UnderAttack && alert.OtherOwnerId != 0) failures.Add($"alert to {receiver} names {alert.OtherOwnerId}");
                bool own = false;
                for (int slot = 0; slot < units && !own; slot++)
                    own = settled.OwnerId[slot] == receiver && math.all((int2)math.floor(settled.Positions[slot]) == alert.Tile);
                if (!own) failures.Add($"alert to {receiver} at {alert.Tile} is not on one of its units");
            }
        });
        sim.Context.Settled += (data, tick, n) =>
        {
            settled = data;
            units = n;
            alerts.CollectDamage(data, n, _ => (false, 0, default), tick);
            alerts.Flush();
        };
        var random = new Unity.Mathematics.Random((uint)seed);
        for (int u = 0; u < 30; u++)
        {
            sim.Spawn(SimHarness.OwnerA, new float2(40 + random.NextFloat(-3, 3), 48 + random.NextFloat(-3, 3)));
            sim.Spawn(OwnerC, new float2(44 + random.NextFloat(-3, 3), 48 + random.NextFloat(-3, 3)));
        }
        for (int t = 0; t < 60; t++) sim.Tick();
        sent = count;
        return failures;
    }

    [Test]
    public void AlertsCarryOnlyOwnEntities([Values(1, 2)] int seed)
    {
        List<string> failures = FightWithAlerts(seed, out int sent);
        Assert.Greater(sent, 0, "the fight raised alerts");
        CollectionAssert.IsEmpty(failures);
    }

    [Test]
    public void AlertsNeverNameAttackers()
    {
        var damaged = new List<(int owner, int2 tile)> { (1, new int2(3, 3)), (2, new int2(9, 9)) };
        var throttle = new AlertThrottle(0f, 1);
        AlertRules.UnderAttack(damaged, throttle, 0, (receiver, alert) =>
        {
            Assert.AreEqual(0, alert.OtherOwnerId, "an UnderAttack alert never names anyone");
            Assert.AreEqual(AlertKind.UnderAttack, alert.Kind);
        });
        string text = WAR2D.UI.AlertFeedModel.Text(new Alert { Kind = AlertKind.UnderAttack, OtherOwnerId = 42 }, owner => "ATTACKER", null);
        StringAssert.DoesNotContain("ATTACKER", text, "the feed text has no attacker even if an id slipped in");
        Assert.IsEmpty(FightWithAlerts(3, out _));
    }

    [Test]
    public void StatsNeverSentBeforeGameOver()
    {
        foreach (GameState state in System.Enum.GetValues(typeof(GameState)))
            Assert.AreEqual(state == GameState.GameOver, StatsRules.MaySend(state), $"stats in {state}");
    }

    [Test]
    public void DeathsInFogNeverExplodeBeforeGameOver()
    {
        foreach (GameState state in System.Enum.GetValues(typeof(GameState)))
        {
            Assert.IsTrue(VisibilityRules.ShowsDeath(state, gridSees: true), $"a seen death always shows ({state})");
            Assert.AreEqual(state == GameState.GameOver, VisibilityRules.ShowsDeath(state, gridSees: false), $"a death in fog in {state}");
        }
    }

    [Test]
    public void GiftNoticeOnlyToSenderAndRecipient()
    {
        CollectionAssert.AreEquivalent(new[] { 7, 9 }, GiftRules.Recipients(7, 9));
    }

    private static void SetTile(SimHarness sim, int2 tile, TileType type) =>
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = tile, TileType = type });

    [Test]
    public void TerrainChangedUnderFogIsNotSentUntilSeen()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var spy = new Spy(sim.Context, replication, SimHarness.OwnerA);
        int[] scout = sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Tick();
        int2 tile = new int2(80, 80);
        int index = sim.Map.Grid.Index(tile);
        SetTile(sim, tile, TileType.Wall);
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual((byte)TileType.Ground, spy.Terrain.Tiles[index], "changed far out of sight: still floor to A");

        sim.Teleport(scout[0], new float2(78, 80));
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual((byte)TileType.Wall, spy.Terrain.Tiles[index], "seen now: sent");
        int payloads = spy.TerrainPayloads;
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.AreEqual(payloads, spy.TerrainPayloads, "sent once, not every tick");
        CollectionAssert.IsEmpty(spy.Failures);
        spy.Dispose();
    }

    [Test]
    public void TerrainChangesReachOnlyThePlayersWhoSeeThem()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var a = new Spy(sim.Context, replication, SimHarness.OwnerA);
        var b = new Spy(sim.Context, replication, SimHarness.OwnerB);
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Spawn(SimHarness.OwnerB, new float2(80, 80));
        sim.Tick();
        SetTile(sim, new int2(82, 80), TileType.Wall);
        for (int i = 0; i < 10; i++) sim.Tick();
        Assert.Greater(b.TerrainPayloads, 0, "B sees the tile");
        Assert.AreEqual(0, a.TerrainPayloads, "A doesn't");
        CollectionAssert.IsEmpty(a.Failures);
        CollectionAssert.IsEmpty(b.Failures);
        a.Dispose();
        b.Dispose();
    }

    [Test]
    public void BombsReachOnlyTheirOwnerAndPlayersTheyShareVisionWith()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        bool aSharesWithB = false;
        replication.SharesVision = (from, to) => from == SimHarness.OwnerA && to == SimHarness.OwnerB && aSharesWithB;
        var views = new List<BuildingView>();
        replication.CollectBuildings = list => list.AddRange(views);
        var a = new Spy(sim.Context, replication, SimHarness.OwnerA);
        var b = new Spy(sim.Context, replication, SimHarness.OwnerB);
        var c = new Spy(sim.Context, replication, OwnerC);
        sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        sim.Spawn(SimHarness.OwnerB, new float2(21, 21)); // right next to the bomb: B sees the tile
        sim.Spawn(OwnerC, new float2(22, 19));            // so does C
        sim.Tick();
        views.Add(new BuildingView { Id = 500, OwnerId = SimHarness.OwnerA, Type = BuildingType.Bomb, Anchor = new int2(21, 20), Health = 40, MaxHealth = 40, Position = new float2(21, 20), Hidden = true, Progress = 100 });
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsTrue(a.Buildings.Entries.ContainsKey(500), "the owner knows its bomb");
        Assert.IsFalse(b.Buildings.Entries.ContainsKey(500), "B sees the tile but A doesn't share vision with B");
        Assert.IsFalse(c.Buildings.Entries.ContainsKey(500));

        aSharesWithB = true;
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsTrue(b.Buildings.Entries.ContainsKey(500), "shared vision: B may know it");

        aSharesWithB = false;
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsFalse(b.Buildings.Entries.ContainsKey(500), "sharing stopped: gone, not a ghost");
        Assert.IsFalse(c.Buildings.Entries.ContainsKey(500));
        foreach (Spy s in new[] { a, b, c }) { CollectionAssert.IsEmpty(s.Failures); s.Dispose(); }
    }

    [Test]
    public void OnlyTheRecorderSeesEveryBombNotASpectator()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.Spectating = o => o == OwnerC; // an eliminated player watching the whole map
        var views = new List<BuildingView>();
        replication.CollectBuildings = list => list.AddRange(views);
        var recorder = new ClientBuildings();
        var spectator = new ClientBuildings();
        replication.AddVirtualClient(int.MinValue + 1, (p, ch) => { if (ch == ReplicationService.BuildingSinkChannel) BuildingCodec.Apply(recorder, p); }, seesAll: true);
        replication.AddVirtualClient(OwnerC, (p, ch) => { if (ch == ReplicationService.BuildingSinkChannel) BuildingCodec.Apply(spectator, p); });
        sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        sim.Tick();
        views.Add(new BuildingView { Id = 500, OwnerId = SimHarness.OwnerA, Type = BuildingType.Bomb, Anchor = new int2(21, 20), Health = 40, MaxHealth = 40, Position = new float2(21, 20), Hidden = true, Progress = 100 });
        for (int i = 0; i < 8; i++) sim.Tick();
        Assert.IsTrue(recorder.Entries.ContainsKey(500), "the recorder sees every bomb");
        Assert.IsFalse(spectator.Entries.ContainsKey(500), "a spectator sees the map, never another player's bombs");
    }

    [Test]
    public void NoReplayOfferOrChunkBeforeGameOver()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "w2d-leak-" + Guid.NewGuid().ToString("N") + WAR2D.Net.Replay.ReplayFile.Extension);
        System.IO.File.WriteAllBytes(path, new byte[50_000]);
        var sender = new WAR2D.Net.Replay.ReplaySender(1, path, 1 << 20, 1f);
        int offers = 0, chunks = 0;
        sender.SendOffer = (c, m) => offers++;
        sender.SendChunk = (c, m) => chunks++;
        var conn = new Mirror.NetworkConnectionToClient(42);
        foreach (GameState s in Enum.GetValues(typeof(GameState)))
        {
            if (s == GameState.GameOver) continue;
            sender.Offer(conn, s);
            sender.Accept(conn, 1, s);
            sender.Update(1f, s);
        }
        Assert.AreEqual(0, offers);
        Assert.AreEqual(0, chunks);
        Assert.IsFalse(sender.Busy, "an accept before GameOver is ignored");
        sender.Offer(conn, GameState.GameOver);
        Assert.AreEqual(1, offers);
        System.IO.File.Delete(path);
    }
}
