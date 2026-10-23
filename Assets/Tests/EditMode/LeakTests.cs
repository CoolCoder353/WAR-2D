using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
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
        /// <summary>Building attack events received (v0.13), and the building ids they named.</summary>
        public int BuildingAttacks;
        public readonly HashSet<int> Attackers = new HashSet<int>();
        /// <summary>The targets those events named (0: unnamed, or a wall).</summary>
        public readonly List<int> AttackTargets = new List<int>();
        /// <summary>Other players' unit ids this client was ever told about.</summary>
        public readonly HashSet<int> OthersKnown = new HashSet<int>();
        private readonly SimContext sim;

        public Spy(SimContext sim, ReplicationService replication, int owner)
        {
            this.sim = sim;
            Owner = owner;
            Units = new ClientUnitStore(sim.Config.Simulation.MaxEntities, sim.Config.Simulation.TickSeconds);
            Terrain.Init(sim.Map.Grid); // before any tick: the map is still as generated
            Units.BuildingAttack += OnBuildingAttack;
            replication.AddVirtualClient(owner, Receive);
        }

        /// <summary>A building's attack (v0.13) may only come from a building this client owns or its grid sees now, and name only a target it may know.</summary>
        private void OnBuildingAttack(int building, int target)
        {
            BuildingAttacks++;
            Attackers.Add(building);
            AttackTargets.Add(target);
            SimData data = sim.Data;
            int grid = sim.VisionOf(Owner);
            int b = BuildingSlot(data, building);
            if (b < 0) { Failures.Add($"attack by building {building}, which isn't live"); return; }
            if (data.BuildingOwnerId[b] != Owner && !data.Sees(grid, data.BuildingPositions[b])) Failures.Add($"attack by building {building} sent to {Owner} while unseen");
            if (target == 0) return;
            int unit = data.SlotOf(target, sim.Clock.UnitCount);
            int tb = BuildingSlot(data, target);
            bool known = unit >= 0 ? data.OwnerId[unit] == Owner || data.Sees(grid, data.Positions[unit])
                : tb >= 0 && (data.BuildingOwnerId[tb] == Owner || data.Sees(grid, data.BuildingPositions[tb]));
            if (!known) Failures.Add($"building attack names target {target} unknown to {Owner}");
        }

        private static int BuildingSlot(SimData data, int id)
        {
            int index = NetIdAllocator.IndexOf(id);
            if (id <= 0 || index >= data.BuildingSlotOfIndex.Length) return -1;
            int b = data.BuildingSlotOfIndex[index];
            return b < data.BuildingCount[0] && data.BuildingIds[b] == id ? b : -1;
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
                if (data.OwnerId[slot] != Owner) { OthersSeen++; OthersKnown.Add(id); }
            }
        }

        public void Dispose() => Units.Dispose();
    }

    /// <summary>Every live building in the sim world, as <c>WorldStateManager.FillBuildingViews</c> reports them.</summary>
    private static void SimBuildings(SimHarness sim, List<BuildingView> views)
    {
        using EntityQuery q = sim.Em.CreateEntityQuery(typeof(BuildingData), typeof(HealthComponent), typeof(LocalTransform));
        using NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities)
        {
            BuildingData building = sim.Em.GetComponentData<BuildingData>(e);
            HealthComponent health = sim.Em.GetComponentData<HealthComponent>(e);
            if (health.currentHealth <= 0f) continue;
            float2 p = sim.Em.GetComponentData<LocalTransform>(e).Position.xy;
            views.Add(new BuildingView
            {
                Id = building.id, OwnerId = building.ownerId, Type = building.buildingType, Anchor = (int2)math.round(p),
                Health = (int)math.ceil(health.currentHealth), MaxHealth = (int)math.ceil(health.maxHealth),
                Hidden = building.buildingType == BuildingType.Bomb,
                Progress = sim.Em.HasComponent<Construction>(e) ? (byte)0 : (byte)100,
                Position = p,
            });
        }
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
    public void OnlyTheOwnerLearnsABuildingIsUnpaid()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var views = new List<BuildingView>();
        replication.CollectBuildings = list => list.AddRange(views);
        var owner = new Spy(sim.Context, replication, SimHarness.OwnerA);
        var enemy = new Spy(sim.Context, replication, OwnerC);
        sim.Spawn(SimHarness.OwnerA, new float2(20, 20));
        sim.Spawn(OwnerC, new float2(23, 20)); // C stands next to A's turret and sees it
        sim.Tick();
        var turret = new BuildingView { Id = 77, OwnerId = SimHarness.OwnerA, Type = BuildingType.GunTurret, Anchor = new int2(21, 20), Health = 350, MaxHealth = 350, Progress = 100, Position = new float2(21, 20) };
        views.Add(turret);
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.IsTrue(enemy.Buildings.Entries.ContainsKey(77), "C sees the turret");
        Assert.IsFalse(owner.Buildings.Entries[77].Unpaid);

        int enemyPayloads = enemy.BuildingPayloads;
        turret.Unpaid = true;
        views[0] = turret;
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.IsTrue(owner.Buildings.Entries[77].Unpaid, "the owner is told it went unpaid");
        Assert.IsFalse(enemy.Buildings.Entries[77].Unpaid, "C is not");
        Assert.AreEqual(enemyPayloads, enemy.BuildingPayloads, "C is sent no record at all for it");

        turret.Unpaid = false;
        views[0] = turret;
        for (int i = 0; i < 6; i++) sim.Tick();
        Assert.IsFalse(owner.Buildings.Entries[77].Unpaid, "paid again");
        Assert.AreEqual(enemyPayloads, enemy.BuildingPayloads);
        CollectionAssert.IsEmpty(owner.Failures);
        CollectionAssert.IsEmpty(enemy.Failures);
        owner.Dispose();
        enemy.Dispose();
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
    public void ArsenalCombatNeverLeaks([Values(1, 2)] int seed)
    {
        using var sim = new SimHarness(Map(), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var spies = new[] { new Spy(sim.Context, replication, SimHarness.OwnerA), new Spy(sim.Context, replication, OwnerC) };
        var random = new Unity.Mathematics.Random((uint)seed);
        UnitType[] roster = { UnitType.Tank, UnitType.Scout, UnitType.Heavy, UnitType.Artillery, UnitType.Sniper, UnitType.Flamethrower, UnitType.SuicideBomber };
        var aBoxes = new List<int[]>();
        var cBoxes = new List<int[]>();
        for (int u = 0; u < 35; u++)
        {
            UnitType type = roster[u % roster.Length];
            aBoxes.Add(sim.Spawn(SimHarness.OwnerA, new float2(random.NextFloat(4, 40), random.NextFloat(4, 92)), type));
            cBoxes.Add(sim.Spawn(OwnerC, new float2(random.NextFloat(56, 92), random.NextFloat(4, 92)), type));
        }
        sim.Tick(); // the spawns apply, filling the id boxes
        int[] a = aBoxes.ConvertAll(box => box[0]).ToArray();
        int[] c = cBoxes.ConvertAll(box => box[0]).ToArray();
        // Both sides attack-move across the wall line, so splash, walls and blasts happen at the fog's edge.
        sim.Order(SimHarness.OwnerA, OrderKind.AttackMove, new int2(80, 48), a);
        sim.Order(OwnerC, OrderKind.AttackMove, new int2(16, 48), c);
        int blasts = 0;
        sim.Context.Settled += (data, tick, count) => blasts += data.BombBlasts.Count; // read where the server reads them
        for (int t = 0; t < 400; t++)
        {
            if (t == 300) // whichever bombers are still alive go off where they stand, at the front or behind it
            {
                sim.Order(SimHarness.OwnerA, OrderKind.Detonate, default, a);
                sim.Order(OwnerC, OrderKind.Detonate, default, c);
            }
            sim.Tick();
        }
        sim.Tick(); // settles the last tick's blasts

        Assert.Greater(blasts, 0, "bombers went off");
        foreach (Spy spy in spies)
        {
            CollectionAssert.IsEmpty(spy.Failures, $"owner {spy.Owner}");
            Assert.Greater(spy.OthersSeen, 0, "the armies met");
            spy.Dispose();
        }
    }

    [Test]
    public void BlastsInTheFogAreNotShown()
    {
        using var sim = new SimHarness(Map());
        sim.Spawn(SimHarness.OwnerA, new float2(10, 10));
        sim.Spawn(OwnerC, new float2(85, 85));
        for (int t = 0; t < 8; t++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        var blasts = new List<float2> { new float2(11, 10) };
        int2 lo = new int2(0, 0), hi = new int2(95, 95);
        Assert.AreEqual(1, VisibilityRules.SeenBlasts(data, sim.Context.VisionOf(SimHarness.OwnerA), blasts, lo, hi)?.Length, "the owner sees its own blast");
        Assert.IsNull(VisibilityRules.SeenBlasts(data, sim.Context.VisionOf(OwnerC), blasts, lo, hi), "a blast in C's fog is not sent to C");
        Assert.IsNull(VisibilityRules.SeenBlasts(data, sim.Context.VisionOf(SimHarness.OwnerA), blasts, new int2(50, 50), new int2(95, 95)), "outside the view box");
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

    [Test]
    public void NoStartOptionOrPickIsEverASyncVar()
    {
        // Start points are private (v0.11): they reach a player through TargetRpcs only, never through replicated state.
        // The pick deadline is public by design (it reveals nothing about the deal).
        foreach (System.Type type in new[] { typeof(ClientPlayer), typeof(GameCore) })
        foreach (System.Reflection.MemberInfo member in type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if (member.GetCustomAttributes(typeof(Mirror.SyncVarAttribute), true).Length == 0) continue;
            string name = member.Name.ToLowerInvariant();
            if (name == "hqplacementendtime") continue;
            Assert.That(name, Does.Not.Contain("startsite").And.Not.Contain("startoption").And.Not.Contain("startpick"), $"{type.Name}.{member.Name} must not be replicated");
        }
        Assert.That(typeof(ClientPlayer).GetField("startSite"), Is.Null, "the public start-site SyncVar is gone");
    }

    /// <summary>Deals six owners (two teams of two and two Solo players) and returns each one's start plus the team map.</summary>
    private static (List<(int owner, int[] options, int pick)> starts, Dictionary<int, int> teams) DealSix(bool ffa)
    {
        var choices = new List<(int Owner, int Choice)> { (11, 0), (12, 0), (13, 1), (14, 1), (15, TeamRules.NoTeam), (16, TeamRules.NoTeam) };
        if (ffa) for (int i = 0; i < choices.Count; i++) choices[i] = (choices[i].Owner, TeamRules.NoTeam);
        Dictionary<int, int> teams = TeamRules.Assign(choices);
        var rng = new System.Random(5);
        Dictionary<int, int[]> deal = StartPickRules.Deal(20, choices.ConvertAll(c => c.Owner), 3, rng.Next);
        var starts = new List<(int owner, int[] options, int pick)>();
        foreach (var c in choices) starts.Add((c.Owner, deal[c.Owner], deal[c.Owner][1]));
        return (starts, teams);
    }

    [Test]
    public void StartOptions_ReachOnlyTheOwnersTeammates()
    {
        (List<(int owner, int[] options, int pick)> starts, Dictionary<int, int> teams) = DealSix(ffa: false);
        bool Mates(int a, int b) => a != b && teams[a] == teams[b];
        foreach ((int owner, _, _) in starts)
        {
            var view = StartPickRules.TeamView(owner, starts, Mates);
            foreach ((int other, int[] options, int pick) in view)
            {
                Assert.That(Mates(owner, other), $"owner {owner} was told non-teammate {other}'s start");
                Assert.That(options, Is.EqualTo(starts.Find(s => s.owner == other).options));
            }
            int expected = starts.FindAll(s => Mates(owner, s.owner)).Count;
            Assert.AreEqual(expected, view.Count, $"owner {owner} learns every teammate's start and nobody else's");
        }
        Assert.IsEmpty(StartPickRules.TeamView(15, starts, Mates), "a Solo player learns nobody's start");
    }

    [Test]
    public void StartOptions_InFreeForAll_NobodyLearnsAnotherStart()
    {
        (List<(int owner, int[] options, int pick)> starts, Dictionary<int, int> teams) = DealSix(ffa: true);
        foreach ((int owner, _, _) in starts)
            Assert.IsEmpty(StartPickRules.TeamView(owner, starts, (a, b) => a != b && teams[a] == teams[b]), $"owner {owner}");
    }

    // ---- v0.13 Bastion ----

    [Test]
    public void ATurretsAttackReachesOnlyClientsThatMayKnowTheTurret()
    {
        // C's Builder sees 2 tiles: the turret 6 away shoots it from C's fog.
        using var sim = new SimHarness(Map(), c => { c.Simulation.TargetSearchSliceTicks = 1; c.Units[UnitType.Builder].Sight = 2f; });
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.CollectBuildings = list => SimBuildings(sim, list);
        var a = new Spy(sim.Context, replication, SimHarness.OwnerA);
        var c = new Spy(sim.Context, replication, OwnerC);
        foreach (int owner in new[] { SimHarness.OwnerA, OwnerC }) replication.SetVirtualView(owner, new int2(0, 0), new int2(95, 95));
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(20, 20), complete: true);
        int[] victim = sim.Spawn(OwnerC, new float2(26.5f, 20.5f), UnitType.Builder);
        for (int t = 0; t < 30; t++) sim.Tick(); // about four shots: the Builder survives

        Assert.Less(sim.UnitById(victim[0]).Health, sim.Config.GetUnit(UnitType.Builder).Health, "the turret hit C's Builder");
        int index = NetIdAllocator.IndexOf(victim[0]);
        Assert.IsTrue(c.Units.IsKnown(index) && c.Units.Health01(index) < 1f, "C learns the damage through its own unit's health");
        Assert.AreEqual(0, c.BuildingAttacks, "but never the attack event");
        Assert.IsFalse(c.Buildings.Entries.ContainsKey(turret), "nor the turret");
        Assert.Greater(a.BuildingAttacks, 0, "the owner sees its turret fire");
        CollectionAssert.AreEquivalent(new[] { turret }, a.Attackers);
        foreach (Spy spy in new[] { a, c }) { CollectionAssert.IsEmpty(spy.Failures, $"owner {spy.Owner}"); spy.Dispose(); }
    }

    [Test]
    public void ATurretsAttackIsSentOnceItsTargetsSideSeesTheTurret()
    {
        using var sim = new SimHarness(Map(), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.CollectBuildings = list => SimBuildings(sim, list);
        var c = new Spy(sim.Context, replication, OwnerC);
        replication.SetVirtualView(OwnerC, new int2(0, 0), new int2(95, 95));
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(20, 20), complete: true);
        sim.Spawn(OwnerC, new float2(26.5f, 20.5f), UnitType.Heavy); // sight 7: sees the turret 6 away, outside its own range 5
        for (int t = 0; t < 60; t++) sim.Tick();
        Assert.Greater(c.BuildingAttacks, 0, "C sees the turret, so it is told the turret fired");
        CollectionAssert.AreEquivalent(new[] { turret }, c.Attackers);
        CollectionAssert.IsEmpty(c.Failures);
        c.Dispose();
    }

    /// <summary>
    /// A's Jammer at (30, 30) (radius 12) with A's and B's units inside it; C's Tank 6 tiles from them (sight 8,
    /// JamSight 3): without the jam, C would see both units.
    /// </summary>
    private static void JammedArea(SimHarness sim, ReplicationService replication, out int jammer, out int[] aUnit, out int[] bUnit, out int[] cTank)
    {
        sim.Context.TeamResolver = owner => owner == OwnerC ? 1 : owner == SimHarness.OwnerB ? 2 : 0; // three sides
        replication.CollectBuildings = list => SimBuildings(sim, list);
        jammer = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Jammer, new int2(30, 30), complete: true);
        aUnit = sim.Spawn(SimHarness.OwnerA, new float2(32.5f, 34.5f), UnitType.Builder);
        bUnit = sim.Spawn(SimHarness.OwnerB, new float2(33.5f, 27.5f), UnitType.Builder); // a third player's unit in the area
        cTank = sim.Spawn(OwnerC, new float2(38.5f, 30.5f), UnitType.Tank);
    }

    [Test]
    public void NothingInAJamBeyondJamSightReachesTheEnemyClient()
    {
        using var sim = new SimHarness(Map(), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        var spy = new Spy(sim.Context, replication, OwnerC);
        replication.SetVirtualView(OwnerC, new int2(0, 0), new int2(95, 95));
        JammedArea(sim, replication, out int jammer, out int[] aUnit, out int[] bUnit, out int[] cUnit);
        sim.Tick();
        int2 tile = new int2(33, 33);
        int tileIndex = sim.Map.Grid.Index(tile);
        for (int t = 0; t < 40; t++)
        {
            if (t == 10) sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetTile, Tile = tile, TileType = TileType.Wall });
            sim.Tick();
        }
        sim.Em.CompleteAllTrackedJobs();
        SimData data = sim.Context.Data;
        int gridC = sim.Context.VisionOf(OwnerC);
        Assert.IsTrue(data.Jammed(gridC, new float2(32.5f, 34.5f)), "the area is jammed for C");
        Assert.IsFalse(spy.OthersKnown.Contains(aUnit[0]), "no record of the Jammer owner's unit");
        Assert.IsFalse(spy.OthersKnown.Contains(bUnit[0]), "nor of a third player's unit in the area");
        Assert.IsFalse(spy.Buildings.Entries.ContainsKey(jammer), "nor of the Jammer, not even a ghost");
        Assert.AreEqual((byte)TileType.Ground, spy.Terrain.Tiles[tileIndex], "nor of a tile that changed in the jam");
        var blasts = new List<float2> { new float2(32.5f, 34.5f) };
        Assert.IsNull(VisibilityRules.SeenBlasts(data, gridC, blasts, new int2(0, 0), new int2(95, 95)), "nor an explosion there");

        var pings = new List<Alert>();
        var radars = new RadarService(0f, 0f);
        var radar = new RadarView { Id = 999, OwnerId = OwnerC, Tile = new int2(40, 30), Centre = new float2(40f, 30f), Radius = 20f };
        for (int i = 0; i < 3; i++) radars.Update(data, sim.Context.Clock.UnitCount, new[] { radar }, i, (o, alert) => pings.Add(alert));
        CollectionAssert.IsEmpty(pings, "nor a Radar ping: every contact is jammed");
        CollectionAssert.IsEmpty(spy.Failures);

        // Within JamSight the jam lets C see: the test isn't passing by accident.
        sim.Teleport(cUnit[0], new float2(34.5f, 34.5f));
        for (int t = 0; t < 12; t++) sim.Tick();
        Assert.IsTrue(spy.OthersKnown.Contains(aUnit[0]), "2 tiles away: seen");
        CollectionAssert.IsEmpty(spy.Failures);
        spy.Dispose();
    }

    [Test]
    public void AJammedTurretsAttackOnAnEnemyNeverReachesIt()
    {
        using var sim = new SimHarness(Map(), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.CollectBuildings = list => SimBuildings(sim, list);
        var spy = new Spy(sim.Context, replication, OwnerC);
        replication.SetVirtualView(OwnerC, new int2(0, 0), new int2(95, 95));
        sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.Jammer, new int2(30, 30), complete: true);
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(30, 34), complete: true);
        int[] victim = sim.Spawn(OwnerC, new float2(36.5f, 34.5f), UnitType.Heavy); // 6 from the turret: sees it but for the jam
        for (int t = 0; t < 60; t++) sim.Tick();
        Assert.Less(sim.UnitById(victim[0]).Health, sim.Config.GetUnit(UnitType.Heavy).Health, "the turret hit C's Heavy");
        Assert.AreEqual(0, spy.BuildingAttacks, "C never learns the jammed turret fired");
        Assert.IsFalse(spy.Buildings.Entries.ContainsKey(turret));
        CollectionAssert.IsEmpty(spy.Failures);
        spy.Dispose();
    }

    [Test]
    public void AnOwnerSeesItsTurretFireWhereverItsCameraIs()
    {
        using var sim = new SimHarness(Map(), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.CollectBuildings = list => SimBuildings(sim, list);
        var a = new Spy(sim.Context, replication, SimHarness.OwnerA);
        var c = new Spy(sim.Context, replication, OwnerC);
        // Both cameras far from the turret.
        foreach (int owner in new[] { SimHarness.OwnerA, OwnerC }) replication.SetVirtualView(owner, new int2(60, 60), new int2(95, 95));
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(20, 20), complete: true);
        sim.Spawn(OwnerC, new float2(26.5f, 20.5f), UnitType.Heavy); // sees the turret, outside its own range
        for (int t = 0; t < 60; t++) sim.Tick();
        Assert.Greater(a.BuildingAttacks, 0, "the owner's turret status doesn't depend on its camera");
        CollectionAssert.AreEquivalent(new[] { turret }, a.Attackers);
        Assert.AreEqual(0, c.BuildingAttacks, "an enemy who sees the turret still needs it in view");
        foreach (Spy spy in new[] { a, c }) { CollectionAssert.IsEmpty(spy.Failures, $"owner {spy.Owner}"); spy.Dispose(); }
    }

    [Test]
    public void ATurretsWallShotReachesWhoMayKnowTheTurretNamingNoTarget()
    {
        using var sim = new SimHarness(Map(), c => c.Simulation.TargetSearchSliceTicks = 1);
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.CollectBuildings = list => SimBuildings(sim, list);
        var a = new Spy(sim.Context, replication, SimHarness.OwnerA);
        var c = new Spy(sim.Context, replication, OwnerC);
        foreach (int owner in new[] { SimHarness.OwnerA, OwnerC }) replication.SetVirtualView(owner, new int2(0, 0), new int2(95, 95));
        sim.Spawn(OwnerC, new float2(80.5f, 80.5f), UnitType.Builder); // C exists, far from the turret: it can't see it
        int turret = sim.PlaceBuilding(SimHarness.OwnerA, BuildingType.GunTurret, new int2(20, 20), complete: true);
        int2 wall = new int2(17, 20);
        sim.PlaceWall(OwnerC, wall);
        sim.Tick();
        sim.ForceBuiltWall(wall);
        for (int t = 0; t < 40; t++) sim.Tick();
        Assert.Greater(a.BuildingAttacks, 0, "the owner sees its turret shoot the wall");
        CollectionAssert.AreEqual(Enumerable(0, a.AttackTargets.Count), a.AttackTargets, "with no target named");
        Assert.AreEqual(0, c.BuildingAttacks, "the wall's owner doesn't see the turret: no event");
        foreach (Spy spy in new[] { a, c }) { CollectionAssert.IsEmpty(spy.Failures, $"owner {spy.Owner}"); spy.Dispose(); }

        static List<int> Enumerable(int value, int count) { var l = new List<int>(); for (int i = 0; i < count; i++) l.Add(value); return l; }
    }

    [Test]
    public void AJammerGhostStaysAGhostUntilItsSpotIsSeen()
    {
        using var sim = new SimHarness(Map());
        using var replication = new ReplicationService(sim.Context, sim.Config);
        replication.CollectBuildings = list => SimBuildings(sim, list);
        var spy = new Spy(sim.Context, replication, SimHarness.OwnerA);
        int jammer = sim.PlaceBuilding(OwnerC, BuildingType.Jammer, new int2(30, 30), complete: true);
        int[] tank = sim.Spawn(SimHarness.OwnerA, new float2(33.5f, 31.5f)); // up close: within JamSight
        for (int t = 0; t < 8; t++) sim.Tick();
        Assert.IsTrue(spy.Buildings.Entries.ContainsKey(jammer) && !spy.Buildings.Entries[jammer].Ghost, "seen up close");

        sim.Teleport(tank[0], new float2(37.5f, 31.5f)); // 6 away: in sight 8 but for the jam
        for (int t = 0; t < 8; t++) sim.Tick();
        sim.Em.CompleteAllTrackedJobs();
        Assert.IsTrue(sim.Context.Data.Jammed(sim.Context.VisionOf(SimHarness.OwnerA), new float2(31f, 31f)));
        Assert.IsTrue(spy.Buildings.Entries[jammer].Ghost, "jammed out of sight: a ghost");
        for (int t = 0; t < 40; t++) sim.Tick();
        Assert.IsTrue(spy.Buildings.Entries.ContainsKey(jammer) && spy.Buildings.Entries[jammer].Ghost, "and it stays one, though the Tank is within its sight");

        sim.Teleport(tank[0], new float2(33.5f, 31.5f));
        for (int t = 0; t < 8; t++) sim.Tick();
        Assert.IsFalse(spy.Buildings.Entries[jammer].Ghost, "seen again up close: live");
        CollectionAssert.IsEmpty(spy.Failures);
        spy.Dispose();
    }
}
