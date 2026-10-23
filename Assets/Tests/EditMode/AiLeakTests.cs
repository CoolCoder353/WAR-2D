using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using WAR2D.AiView;
using WAR2D.Client;
using WAR2D.Net.Replication;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>
/// v0.14 leak proof for the AI's <see cref="TeamView"/> (spec §4): what is hidden from a client of the AI's owner never
/// appears in its view (enemy units, buildings, hidden bombs, wall and dig changes, explosions, Radar contacts in the fog or
/// a jam, turrets firing from a jam, other players' diplomacy), each thing appears once its side may know it, and the view
/// equals what replication sends a client of the same owner.
/// </summary>
public class AiLeakTests
{
    private static readonly int Ai = AiOwnerIds.For(0);
    private const int Enemy = SimHarness.OwnerB;
    private const int Third = 303;

    private sealed class Facts : ITeamViewFacts
    {
        public readonly List<int> Order = new List<int>();
        public GameState State { get; set; } = GameState.Playing;
        public IReadOnlyList<int> PlayerOrder => Order;
        public void Fill(int owner, TeamView view) => view.State = State;
    }

    /// <summary>A harness world with the AI's view built at every settled tick and checked against the settled world.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly SimHarness Sim;
        public readonly Facts Facts = new Facts();
        public readonly TeamViewBuilder Views;
        public readonly AlertService Alerts;
        private readonly RadarService radar = new RadarService(5f, 30f);
        public readonly List<RadarView> Radars = new List<RadarView>();
        public readonly List<BuildingView> Manual = new List<BuildingView>();
        public bool RealBuildings;
        public TeamView View;
        public readonly List<string> Failures = new List<string>();
        /// <summary>Everything the view ever held: other players' units, building ids, alerts, attacks and blasts.</summary>
        public readonly HashSet<int> OthersEver = new HashSet<int>();
        public readonly List<AiAlert> AlertsEver = new List<AiAlert>();
        public readonly List<AiAttack> AttacksEver = new List<AiAttack>();
        public readonly List<AiBlast> BlastsEver = new List<AiBlast>();
        public readonly List<int> UnderAttackAlerts = new List<int>();

        public Rig(MapStore map, Action<GameConfigData> configure = null)
        {
            Sim = new SimHarness(map, configure);
            Facts.Order.AddRange(new[] { Ai, Enemy, Third });
            Views = new TeamViewBuilder(Sim.Context, Facts)
            {
                CollectBuildings = list => { list.AddRange(Manual); if (RealBuildings) SimBuildings(Sim, list); },
                SharesVision = SharesVision,
            };
            Views.Track(Ai);
            Alerts = new AlertService(0f, 4, (owner, alerts) => { });
            Alerts.Observer = Views.ReportAlerts; // as WorldStateManager wires it
            Sim.Context.UnitDied += (id, p) => Views.ReportDeath(p); // the death list clients' explosions come from
            Sim.Context.Settled += OnSettled;
        }

        public bool SharesVision(int from, int to) =>
            Sim.Context.TrySlotOf(from, out int f) && Sim.Context.TrySlotOf(to, out int t) && Sim.Context.Diplomacy.SharesVisionWith(f, t);

        private void OnSettled(SimData data, int tick, int count)
        {
            // As the match's own Settled handler: Radar watch, damage alerts, the flush; then the AI's think.
            radar.Update(data, count, Radars, tick * 0.05, Alerts.Add);
            Alerts.CollectDamage(data, count, id =>
            {
                AttackTarget b = ReplicationService.BuildingOf(data, id);
                return (b.Id != 0, b.OwnerId, (int2)math.round(b.Position));
            }, tick);
            Alerts.Flush();
            View = Views.Build(Ai, data, tick, count);
            Check(data, count);
        }

        /// <summary>Everything in the view is the AI's own, or on a fog cell its grid sees this tick.</summary>
        private void Check(SimData data, int count)
        {
            int grid = Sim.Context.TrySlotOf(Ai, out int g) ? g : -1;
            foreach (AiUnit u in View.OwnUnits) if (u.OwnerId != Ai) Failures.Add($"own list holds {u.Id} of {u.OwnerId}");
            foreach (AiUnit u in View.OtherUnits)
            {
                OthersEver.Add(u.Id);
                if (!data.Sees(grid, u.Position)) Failures.Add($"unit {u.Id} of {u.OwnerId} in the view while unseen");
            }
            foreach (AiBuilding b in View.Buildings)
                if (!b.Ghost && b.OwnerId != Ai && !data.Sees(grid, (float2)b.Anchor)) Failures.Add($"building {b.Id} live in the view while unseen");
            foreach (int index in View.TerrainChanged)
                if (!data.Sees(grid, new float2(index % View.Width + 0.5f, index / View.Width + 0.5f))) Failures.Add($"terrain {index} learned while unseen");
            foreach (AiBlast blast in View.Blasts)
            {
                BlastsEver.Add(blast);
                if (!data.Sees(grid, blast.Position)) Failures.Add($"{blast.Kind} at {blast.Position} while unseen");
            }
            foreach (AiAlert alert in View.Alerts)
            {
                AlertsEver.Add(alert);
                if (alert.Kind == AlertKind.UnderAttack) UnderAttackAlerts.Add(alert.OtherOwnerId);
            }
            foreach (AiAttack attack in View.Attacks)
            {
                AttacksEver.Add(attack);
                if (!Known(data, count, grid, attack.AttackerId)) Failures.Add($"attack by unknown {attack.AttackerId}");
                if (attack.TargetId != 0 && !Known(data, count, grid, attack.TargetId)) Failures.Add($"attack names unknown target {attack.TargetId}");
            }
        }

        private static bool Known(SimData data, int count, int grid, int id)
        {
            int slot = data.SlotOf(id, count);
            if (slot >= 0) return data.OwnerId[slot] == Ai || data.Sees(grid, data.Positions[slot]);
            AttackTarget b = ReplicationService.BuildingOf(data, id);
            if (b.Id != 0) return b.OwnerId == Ai || (b.Hidden == 0 && data.Sees(grid, b.Position));
            return true; // killed by this shot: its last position was checked when it was alive
        }

        public void Ticks(int n) { for (int i = 0; i < n; i++) Sim.Tick(); }

        public bool HasOther(int id) => View.OtherUnits.Any(u => u.Id == id);

        public bool TryBuilding(int id, out AiBuilding building)
        {
            foreach (AiBuilding b in View.Buildings) if (b.Id == id) { building = b; return true; }
            building = default;
            return false;
        }

        public void Dispose()
        {
            Views.Dispose();
            Sim.Dispose();
        }
    }

    /// <summary>Every live building in the harness world, as <c>WorldStateManager.FillBuildingViews</c> reports them.</summary>
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

    /// <summary>A 96² map with a broken rock column at x 47–48 (rows where r % 12 &lt; 8).</summary>
    private static MapStore Map()
    {
        var rows = new string[96];
        for (int r = 0; r < 96; r++)
            rows[r] = r == 0 || r == 95 ? new string('B', 96) : "B" + new string('.', 46) + (r % 12 < 8 ? "##" : "..") + new string('.', 46) + "B";
        return MapStore.FromAscii(rows);
    }

    [Test]
    public void EnemyUnitsAppearOnlyOnceSeen()
    {
        using var rig = new Rig(SimHarness.OpenMap(96));
        int[] mine = rig.Sim.Spawn(Ai, new float2(8.5f, 8.5f), UnitType.Scout);
        int[] enemy = rig.Sim.Spawn(Enemy, new float2(30.5f, 40.5f), UnitType.Builder);
        rig.Ticks(12);
        Assert.That(rig.View.OwnUnits.Any(u => u.Id == mine[0] && u.Type == UnitType.Scout), Is.True, "its own unit, always");
        Assert.That(rig.OthersEver, Has.No.Member(enemy[0]), "an enemy in the fog never appears");

        rig.Sim.Teleport(mine[0], new float2(30.5f, 34.5f));
        rig.Ticks(10);
        Assert.That(rig.HasOther(enemy[0]), Is.True, "seen: it appears");
        AiUnit seen = rig.View.OtherUnits.First(u => u.Id == enemy[0]);
        Assert.That((seen.OwnerId, seen.Type, seen.HasOrder, seen.Stance), Is.EqualTo((Enemy, UnitType.Builder, false, (byte)0)), "no orders for another's unit");

        rig.Sim.Teleport(mine[0], new float2(8.5f, 8.5f));
        rig.Ticks(10);
        Assert.That(rig.HasOther(enemy[0]), Is.False, "out of sight again: gone from the view");
        CollectionAssert.IsEmpty(rig.Failures);
    }

    [Test]
    public void AnEnemyBuildingIsHiddenThenAGhostUntilItsSpotIsSeen()
    {
        using var rig = new Rig(SimHarness.OpenMap(96));
        int[] scout = rig.Sim.Spawn(Ai, new float2(20, 80));
        rig.Sim.Spawn(Enemy, new float2(80, 80)); // gives the enemy a slot
        rig.Ticks(2);
        rig.Manual.Add(new BuildingView { Id = 99, OwnerId = Enemy, Type = BuildingType.Miner, Anchor = new int2(22, 20), Health = 50, MaxHealth = 50, Progress = 100, Position = new float2(22, 20) });
        rig.Ticks(8);
        Assert.That(rig.TryBuilding(99, out _), Is.False, "in the fog: unknown");

        rig.Sim.Teleport(scout[0], new float2(20, 20));
        rig.Ticks(8);
        Assert.That(rig.TryBuilding(99, out AiBuilding seen), Is.True, "seen");
        Assert.That((seen.Ghost, seen.Type, seen.OwnerId, seen.Anchor), Is.EqualTo((false, BuildingType.Miner, Enemy, new int2(22, 20))));

        rig.Sim.Teleport(scout[0], new float2(20, 80));
        rig.Ticks(8);
        Assert.That(rig.TryBuilding(99, out AiBuilding ghost) && ghost.Ghost, Is.True, "out of sight: a ghost");

        rig.Manual.Clear(); // destroyed out of sight
        rig.Ticks(8);
        Assert.That(rig.TryBuilding(99, out ghost) && ghost.Ghost, Is.True, "a ghost stays a ghost until its spot is seen");

        rig.Sim.Teleport(scout[0], new float2(20, 20));
        rig.Ticks(8);
        Assert.That(rig.TryBuilding(99, out _), Is.False, "seeing the spot again clears it");
        CollectionAssert.IsEmpty(rig.Failures);
    }

    [Test]
    public void AHiddenBombAppearsOnlyToWhomItsOwnerSharesVisionWith()
    {
        using var rig = new Rig(SimHarness.OpenMap(96));
        rig.Sim.Spawn(Ai, new float2(20, 20));
        rig.Sim.Spawn(Enemy, new float2(80, 80));
        rig.Ticks(2);
        rig.Manual.Add(new BuildingView { Id = 55, OwnerId = Enemy, Type = BuildingType.Bomb, Anchor = new int2(22, 20), Health = 10, MaxHealth = 10, Progress = 100, Hidden = true, Position = new float2(22, 20) });
        rig.Ticks(10);
        Assert.That(rig.TryBuilding(55, out _), Is.False, "seen ground, but a hidden bomb");

        rig.Sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = Enemy, TargetOwnerId = Ai, Flag = true });
        rig.Ticks(6);
        Assert.That(rig.TryBuilding(55, out AiBuilding bomb) && !bomb.Ghost, Is.True, "its owner shares vision: revealed");

        rig.Sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SetShareVision, OwnerId = Enemy, TargetOwnerId = Ai, Flag = false });
        rig.Ticks(6);
        Assert.That(rig.TryBuilding(55, out _), Is.False, "sharing stopped: gone at once, never a ghost of a bomb");
        CollectionAssert.IsEmpty(rig.Failures);
    }

    [Test]
    public void WallAndDigChangesAppearOnlyOnceTheirCellIsSeen()
    {
        using var rig = new Rig(Map());
        int[] scout = rig.Sim.Spawn(Ai, new float2(8.5f, 80.5f));
        rig.Sim.Spawn(Enemy, new float2(80, 80));
        var wall = new int2(30, 20);
        var rock = new int2(47, 2);
        while (rig.Sim.Map.Grid.TileAt(rock) != TileType.Wall) rock.y++; // the column's first rock
        int wallIndex = rig.Sim.Map.Grid.Index(wall), rockIndex = rig.Sim.Map.Grid.Index(rock);
        Assert.That(rig.Sim.Map.Grid.TileAt(rock), Is.EqualTo(TileType.Wall));
        rig.Ticks(2);

        rig.Sim.PlaceWall(Enemy, wall);
        rig.Ticks(2);
        rig.Sim.ForceBuiltWall(wall);
        // A dig: the rock turns to floor (as a Digger's finished tile does at the boundary).
        rig.Sim.Em.CompleteAllTrackedJobs();
        Assert.That(rig.Sim.Map.SetTile(rock, TileType.Ground), Is.True);
        SimData data = rig.Sim.Context.Data;
        data.RefreshTerrain(rock);
        data.RebuildOpaqueSum();
        rig.Ticks(8);
        Assert.That(rig.View.Tiles[wallIndex], Is.EqualTo((byte)TileType.Ground), "a wall raised in the fog is unknown");
        Assert.That(rig.View.Tiles[rockIndex], Is.EqualTo((byte)TileType.Wall), "a dig in the fog is unknown");

        rig.Sim.Teleport(scout[0], new float2(30.5f, 16.5f));
        rig.Ticks(8);
        Assert.That(rig.View.Tiles[wallIndex], Is.EqualTo((byte)TileType.PlayerWall), "seen: the wall appears");
        Assert.That(rig.View.Tiles[rockIndex], Is.EqualTo((byte)TileType.Wall), "the dig is still out of sight");

        rig.Sim.Teleport(scout[0], new float2(44.5f, rock.y + 0.5f));
        bool learned = false;
        for (int i = 0; i < 8; i++) { rig.Ticks(1); learned |= rig.View.TerrainChanged.Contains(rockIndex); }
        Assert.That(rig.View.Tiles[rockIndex], Is.EqualTo((byte)TileType.Ground), "seen: the dig appears");
        Assert.That(learned, Is.True, "and is reported as a change");
        CollectionAssert.IsEmpty(rig.Failures);
    }

    [Test]
    public void ExplosionsAndBlastsInTheFogNeverAppear()
    {
        using var rig = new Rig(SimHarness.OpenMap(96));
        int[] scout = rig.Sim.Spawn(Ai, new float2(8.5f, 8.5f), UnitType.Builder); // unarmed: nothing dies but what the test kills
        int[] victim = rig.Sim.Spawn(Enemy, new float2(70.5f, 70.5f), UnitType.Builder);
        int[] bomber = rig.Sim.Spawn(Enemy, new float2(60.5f, 70.5f), UnitType.SuicideBomber);
        rig.Ticks(4);
        rig.Sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, OwnerId = Enemy, Ids = new[] { victim[0] } });
        rig.Sim.Order(Enemy, OrderKind.Detonate, int2.zero, bomber[0]);
        rig.Ticks(10);
        Assert.That(rig.Sim.Units().Any(u => u.Id == victim[0] || u.Id == bomber[0]), Is.False, "both went off in the fog");
        CollectionAssert.IsEmpty(rig.BlastsEver, "nothing it can't see");

        // The same in sight: both appear.
        rig.Sim.Teleport(scout[0], new float2(30.5f, 30.5f));
        int[] seenVictim = rig.Sim.Spawn(Enemy, new float2(32.5f, 30.5f), UnitType.Builder);
        int[] seenBomber = rig.Sim.Spawn(Enemy, new float2(30.5f, 33.5f), UnitType.SuicideBomber);
        rig.Ticks(6);
        rig.Sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.KillUnits, OwnerId = Enemy, Ids = new[] { seenVictim[0] } });
        rig.Sim.Order(Enemy, OrderKind.Detonate, int2.zero, seenBomber[0]);
        rig.Ticks(6);
        Assert.That(rig.BlastsEver.Any(b => b.Kind == AiBlastKind.Death), Is.True, "a death in sight");
        Assert.That(rig.BlastsEver.Any(b => b.Kind == AiBlastKind.Bomber), Is.True, "a bomber's blast in sight");
        CollectionAssert.IsEmpty(rig.Failures);
    }

    [Test]
    public void RadarContactsAndJamsNeverRevealTheEnemy()
    {
        using var rig = new Rig(SimHarness.OpenMap(96), c => c.Simulation.TargetSearchSliceTicks = 1);
        rig.RealBuildings = true;
        int[] far = rig.Sim.Spawn(Ai, new float2(8.5f, 88.5f), UnitType.Builder); // a slot, far from everything
        // A Radar contact in the fog: an alert with the Radar's own tile, and nothing about the enemy.
        rig.Radars.Add(new RadarView { Id = 1, OwnerId = Ai, Tile = new int2(20, 20), Centre = new float2(21, 21), Radius = 10f });
        int[] contact = rig.Sim.Spawn(Enemy, new float2(25.5f, 25.5f), UnitType.Builder);
        rig.Ticks(12);
        Assert.That(rig.AlertsEver.Any(a => a.Kind == AlertKind.EnemySpotted && a.Tile.Equals(new int2(20, 20))), Is.True, "the Radar pings its owner");
        Assert.That(rig.AlertsEver.All(a => a.OtherOwnerId == 0), Is.True, "naming nobody");
        Assert.That(rig.OthersEver, Has.No.Member(contact[0]), "the contact itself stays hidden");

        // A jam (v0.13): the enemy's Jammer hides its cells beyond JamSight. A Radar doesn't ping in it, the AI's grid
        // doesn't see it, and a turret firing from it at the AI's unit is never named.
        rig.Sim.PlaceBuilding(Enemy, BuildingType.Jammer, new int2(60, 60), complete: true);
        int turret = rig.Sim.PlaceBuilding(Enemy, BuildingType.GunTurret, new int2(62, 64), complete: true);
        int[] jammed = rig.Sim.Spawn(Enemy, new float2(64.5f, 62.5f), UnitType.Builder);
        rig.Radars.Add(new RadarView { Id = 2, OwnerId = Ai, Tile = new int2(64, 70), Centre = new float2(64.5f, 70.5f), Radius = 10f });
        int[] probe = rig.Sim.Spawn(Ai, new float2(62.5f, 70.5f), UnitType.Heavy); // 6 from the turret: in its range, beyond JamSight
        rig.Ticks(30);
        Assert.That(rig.Sim.Context.Data.Jammed(rig.Sim.Context.VisionOf(Ai), new float2(64.5f, 62.5f)), Is.True, "the enemy stands in the jam");
        Assert.That(rig.AlertsEver.Any(a => a.Kind == AlertKind.EnemySpotted && a.Tile.Equals(new int2(64, 70))), Is.False, "no Radar ping in a jam");
        Assert.That(rig.OthersEver, Has.No.Member(jammed[0]), "a unit in the jam stays hidden");
        Assert.That(rig.UnderAttackAlerts, Is.Not.Empty, "the turret fired at the AI's unit (its owner is told it is hit)");
        Assert.That(rig.AttacksEver.Any(a => a.ByBuilding && a.AttackerId == turret), Is.False, "but never which turret fired");
        Assert.That(rig.TryBuilding(turret, out _), Is.False, "nor that it is there");

        // Within JamSight it is seen, like any other cell.
        rig.Sim.Teleport(probe[0], new float2(63.5f, 63.5f));
        rig.Ticks(12);
        Assert.That(rig.OthersEver, Has.Member(jammed[0]), "up close: revealed");
        Assert.That(rig.TryBuilding(turret, out AiBuilding seen) && !seen.Ghost, Is.True, "the turret too");
        Assert.That(rig.AttacksEver.Any(a => a.ByBuilding && a.AttackerId == turret), Is.True, "and its shots are named");
        Assert.That(far[0], Is.GreaterThan(0));
        CollectionAssert.IsEmpty(rig.Failures);
    }

    [Test]
    public void OnlyItsOwnDiplomacyRowAppears()
    {
        using var rig = new Rig(SimHarness.OpenMap(96));
        rig.Sim.Spawn(Ai, new float2(10, 10));
        rig.Sim.Spawn(Enemy, new float2(80, 10));
        rig.Sim.Spawn(Third, new float2(10, 80));
        rig.Ticks(4);
        Assert.That(rig.View.Attacking.ToArray(), Is.EquivalentTo(new[] { Enemy, Third }), "it starts attacking every other team");
        int[] attacking = rig.View.Attacking.ToArray(), sharing = rig.View.SharingWith.ToArray(), shared = rig.View.SharedWithMe.ToArray();

        // Changes between two other players never reach it.
        void Set(SimCommandKind kind, int from, int to, bool on) =>
            rig.Sim.Context.Commands.Enqueue(new SimCommand { Kind = kind, OwnerId = from, TargetOwnerId = to, Flag = on });
        Set(SimCommandKind.SetAttack, Enemy, Third, false);
        Set(SimCommandKind.SetShareVision, Enemy, Third, true);
        Set(SimCommandKind.SetShareVision, Third, Enemy, true);
        Set(SimCommandKind.SetAttack, Enemy, Ai, false); // whom others attack is private to them too
        rig.Ticks(4);
        Assert.That(rig.View.Attacking.ToArray(), Is.EqualTo(attacking));
        Assert.That(rig.View.SharingWith.ToArray(), Is.EqualTo(sharing));
        Assert.That(rig.View.SharedWithMe.ToArray(), Is.EqualTo(shared));

        // Its own row, and who shares with it, do.
        Set(SimCommandKind.SetShareVision, Enemy, Ai, true);
        Set(SimCommandKind.SetAttack, Ai, Third, false);
        rig.Ticks(4);
        Assert.That(rig.View.SharedWithMe.ToArray(), Is.EqualTo(new[] { Enemy }));
        Assert.That(rig.View.Attacking.ToArray(), Is.EqualTo(new[] { Enemy }));
        Assert.That(rig.View.IsEnemy(Third), Is.False);
        // And nothing in the view type could hold another player's row.
        foreach (var field in typeof(TeamView).GetFields())
            Assert.That(field.Name, Does.Not.Contain("Mask").And.Not.Contain("Row"), "the view carries owner lists from its own row only");
    }

    [Test]
    public void TheViewEqualsWhatAClientOfTheSameOwnerIsSent([Values(1, 2)] int seed)
    {
        using var rig = new Rig(Map());
        using var replication = new ReplicationService(rig.Sim.Context, rig.Sim.Config) { CollectBuildings = list => list.AddRange(rig.Manual), SharesVision = rig.SharesVision };
        var units = new ClientUnitStore(rig.Sim.Config.Simulation.MaxEntities, rig.Sim.Config.Simulation.TickSeconds);
        var buildings = new ClientBuildings();
        var terrain = new ClientTerrain();
        terrain.Init(rig.Sim.Map.Grid);
        replication.AddVirtualClient(Ai, (payload, channel) =>
        {
            if (channel == ReplicationService.BuildingSinkChannel) Assert.IsTrue(BuildingCodec.Apply(buildings, payload));
            else if (channel == ReplicationService.TerrainSinkChannel) Assert.IsTrue(TerrainCodec.Apply(terrain, payload));
            else if (channel >= 0) Assert.IsTrue(units.Apply(payload, 0));
        });
        var mismatches = new List<string>();
        // Compared after both have run on the same settled tick (replication subscribed before this handler).
        rig.Sim.Context.Settled += (data, tick, count) =>
        {
            var known = new HashSet<int>();
            for (int k = 0; k < units.Count; k++) known.Add(units.IdOf(units.IndexAt(k)));
            var viewed = new HashSet<int>(rig.View.OwnUnits.Select(u => u.Id).Concat(rig.View.OtherUnits.Select(u => u.Id)));
            if (!known.SetEquals(viewed)) mismatches.Add($"tick {tick}: units {string.Join(",", viewed.Except(known))} vs {string.Join(",", known.Except(viewed))}");
            var clientBuildings = buildings.Entries.Values.Select(e => (e.Data.id, e.Ghost)).OrderBy(b => b.id).ToArray();
            var viewBuildings = rig.View.Buildings.Select(b => (b.Id, b.Ghost)).OrderBy(b => b.Id).ToArray();
            if (!clientBuildings.SequenceEqual(viewBuildings)) mismatches.Add($"tick {tick}: buildings differ");
            // Replication walks terrain on vision ticks (when what a grid sees can change); a view looks at every think.
            if ((tick - 1) % data.VisionInterval == 0 && !terrain.Tiles.SequenceEqual(rig.View.Tiles)) mismatches.Add($"tick {tick}: terrain differs");
        };

        var random = new Unity.Mathematics.Random((uint)seed);
        int[] owners = { Ai, Enemy, Third };
        var ids = new List<int[]>[3] { new List<int[]>(), new List<int[]>(), new List<int[]>() };
        for (int o = 0; o < 3; o++)
            for (int u = 0; u < 20; u++)
                ids[o].Add(rig.Sim.Spawn(owners[o], new float2(random.NextFloat(2, 94), random.NextFloat(2, 94))));
        for (int b = 0; b < 6; b++)
        {
            float2 p = new float2(random.NextInt(4, 92), random.NextInt(4, 92));
            rig.Manual.Add(new BuildingView { Id = 900 + b, OwnerId = owners[b % 3], Type = BuildingType.Miner, Anchor = (int2)p, Health = 50, MaxHealth = 50, Progress = 100, Hidden = b == 4, Position = p });
        }
        for (int t = 0; t < 120; t++)
        {
            if (t % 20 == 0)
            {
                int from = owners[random.NextInt(3)], to = owners[random.NextInt(3)];
                SimCommandKind kind = random.NextBool() ? SimCommandKind.SetShareVision : SimCommandKind.SetAttack;
                rig.Sim.Context.Commands.Enqueue(new SimCommand { Kind = kind, OwnerId = from, TargetOwnerId = to, Flag = random.NextBool() });
            }
            if (t % 15 == 0)
                for (int o = 0; o < 3; o++)
                {
                    var some = new List<int>();
                    foreach (int[] id in ids[o]) if (id[0] > 0 && random.NextBool()) some.Add(id[0]);
                    rig.Sim.Move(owners[o], new int2(random.NextInt(2, 94), random.NextInt(2, 94)), some.ToArray());
                }
            if (t == 60) rig.Manual.RemoveAt(1); // one dies, wherever it is
            if (t == 30) { rig.Sim.PlaceWall(Enemy, new int2(random.NextInt(4, 40), random.NextInt(4, 92))); }
            rig.Sim.Tick();
        }
        units.Dispose();
        CollectionAssert.IsEmpty(mismatches);
        CollectionAssert.IsEmpty(rig.Failures);
        Assert.That(rig.OthersEver, Is.Not.Empty, "it saw others along the way");
    }
}
