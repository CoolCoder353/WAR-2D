using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Config;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Sim;

/// <summary>
/// The measurement behind the Artillery's three blast radii: 4 guns against an attack-moving swarm or a building, with shells and
/// with the old instant hit (<c>ShellSpeed</c> 0 runs the old code path). Not a pass or fail: run it by name after changing the radii
/// and read the "[balance]" lines in the log, as the sim is not bit-repeatable.
/// </summary>
public class ArtilleryBalanceTests
{
    private const int Guns = 4;
    private const int Seeds = 3;
    private static readonly float2 GunsAt = new float2(40f, 64f);

    /// <summary>How a swarm meets the guns.</summary>
    private enum Mode
    {
        /// <summary>It attack-moves at them, and they can die: the real fight, over in a salvo or two.</summary>
        Fight,
        /// <summary>The same, but the guns can't die: many salvos, at a swarm first driving, then stopped and shooting.</summary>
        Protected,
        /// <summary>It attack-moves past 10 tiles off, too far to stop and fight: every shell at a swarm on the move.</summary>
        MarchPast,
    }

    private struct Row
    {
        public float Dead, Credited, Damage, GunsLost, Shells;
        public static Row operator +(Row a, Row b) => new Row { Dead = a.Dead + b.Dead, Credited = a.Credited + b.Credited, Damage = a.Damage + b.Damage, GunsLost = a.GunsLost + b.GunsLost, Shells = a.Shells + b.Shells };
        public Row Over(float n) => new Row { Dead = Dead / n, Credited = Credited / n, Damage = Damage / n, GunsLost = GunsLost / n, Shells = Shells / n };
    }

    /// <summary>One fight: what the swarm has lost after <paramref name="window"/> seconds, and the guns it has killed after 30.</summary>
    private static Row Swarm(UnitType enemy, int count, Mode mode, float window, int layout, int seed, Action<GameConfigData> tune)
    {
        using var sim = new SimHarness(SimHarness.OpenMap(128), c => { tune(c); if (mode == Mode.Protected) c.GetUnit(UnitType.Artillery).Health = 100000; });
        var gunIds = new List<int[]>();
        for (int g = 0; g < Guns; g++) gunIds.Add(sim.Spawn(SimHarness.OwnerA, GunsAt + new float2(g % 2 * 3f, g / 2 * 3f), UnitType.Artillery));
        // Eight ways to come at them; the odd layouts in formation, as a player's order goes by default.
        float2 side = layout % 4 < 2 ? new float2(1f, 0f) : new float2(-1f, 0f), along = layout % 2 == 0 ? new float2(0f, 1f) : new float2(0f, -1f);
        float angle = math.radians(-70f + 20f * layout);
        float2 centre = mode == Mode.MarchPast ? GunsAt + side * 10f - along * 30f : GunsAt + new float2(math.cos(angle), math.sin(angle)) * 26f;
        int2 goal = mode == Mode.MarchPast ? (int2)(GunsAt + side * 10f + along * 30f) : (int2)GunsAt;
        var random = new Unity.Mathematics.Random((uint)(layout * 977 + seed * 131 + 13));
        int rows = (int)math.ceil(math.sqrt(count));
        var enemies = new List<int[]>();
        for (int i = 0; i < count; i++)
            enemies.Add(sim.Spawn(SimHarness.OwnerB, centre + new float2(i % rows - rows / 2f, i / rows - rows / 2f) * 1.1f + random.NextFloat2(new float2(-0.2f), new float2(0.2f)), enemy));
        sim.Tick();
        sim.Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = OrderKind.AttackMove, Formation = layout % 2 == 1, OwnerId = SimHarness.OwnerB, Tile = goal, Ids = enemies.Select(e => e[0]).ToArray() });
        var ids = new HashSet<int>(enemies.Select(e => e[0]));
        float shells = 0f, credited = 0f;
        sim.Context.Settled += (data, settled, units) => shells += data.ShellsFired(settled).Length;
        sim.Context.UnitKilled += (owner, killer) => { if (owner == SimHarness.OwnerB && killer == SimHarness.OwnerA) credited++; };
        sim.TickSeconds(window);
        Unit[] then = sim.Units();
        Row row = new Row
        {
            Dead = count - then.Count(u => ids.Contains(u.Id) && u.Health > 0f),
            Damage = count * sim.Config.GetUnit(enemy).Health - then.Where(u => ids.Contains(u.Id)).Sum(u => u.Health),
            Credited = credited,
            Shells = shells,
        };
        sim.TickSeconds(math.max(0f, 30f - window));
        row.GunsLost = Guns - sim.Units().Count(u => gunIds.Any(g => g[0] == u.Id) && u.Health > 0f);
        return row;
    }

    /// <summary>One building 12 tiles off, made too tough to fall (the shipped Barracks' 300 goes in the first salvo), so the damage is the raw number.</summary>
    private static Row Building(int layout, Action<GameConfigData> tune)
    {
        const int Toughness = 100000;
        using var sim = new SimHarness(SimHarness.OpenMap(128), c => { tune(c); c.GetBuilding(BuildingType.Barracks).Health = Toughness; });
        for (int g = 0; g < Guns; g++) sim.Spawn(SimHarness.OwnerA, GunsAt + new float2(g % 2 * 3f, g / 2 * 3f), UnitType.Artillery);
        float angle = math.radians(-70f + 20f * layout);
        int barracks = sim.PlaceBuilding(SimHarness.OwnerB, BuildingType.Barracks, (int2)(GunsAt + new float2(math.cos(angle), math.sin(angle)) * 12f), complete: true);
        sim.TickSeconds(32f);
        return new Row { Damage = (1f - sim.BuildingHealthShare(barracks)) * Toughness };
    }

    private static Row Average(Func<int, int, Row> run)
    {
        Row sum = default;
        for (int seed = 0; seed < Seeds; seed++)
            for (int layout = 0; layout < 8; layout++) sum += run(layout, seed);
        return sum.Over(8 * Seeds);
    }

    private static string Percent(float old, float now) => old > 0f ? $"{(now - old) / old * 100f:+0;-0;0}%" : "n/a";

    private static string Line(string name, Row old, Row now) =>
        $"[balance] {name}: old dead {old.Dead:0.0} shot {old.Credited:0.0} damage {old.Damage:0} gunsLost {old.GunsLost:0.0} | new dead {now.Dead:0.0} shot {now.Credited:0.0} damage {now.Damage:0} gunsLost {now.GunsLost:0.0} shells {now.Shells:0.0} | dead {Percent(old.Dead, now.Dead)} shot {Percent(old.Credited, now.Credited)} damage {Percent(old.Damage, now.Damage)}";

    private static void Old(GameConfigData c) => c.GetUnit(UnitType.Artillery).ShellSpeed = 0f;

    private static void Shipped(GameConfigData c) { }

    private static string Report(Action<GameConfigData> tune)
    {
        var report = new StringBuilder();
        void Compare(string name, UnitType enemy, int count, Mode mode, float window) =>
            report.AppendLine(Line(name, Average((l, s) => Swarm(enemy, count, mode, window, l, s, Old)), Average((l, s) => Swarm(enemy, count, mode, window, l, s, tune))));
        Compare("60 Tanks, fight, 30 s", UnitType.Tank, 60, Mode.Fight, 30f);
        Compare("60 Tanks, protected, 12.5 s", UnitType.Tank, 60, Mode.Protected, 12.5f);
        Compare("60 Tanks, march past, 12 s", UnitType.Tank, 60, Mode.MarchPast, 12f);
        Compare("30 Bombers, before contact, 2.5 s", UnitType.SuicideBomber, 30, Mode.Fight, 2.5f);
        Compare("30 Bombers, march past, 7 s", UnitType.SuicideBomber, 30, Mode.MarchPast, 7f);
        Compare("6 Heavies, fight, 30 s", UnitType.Heavy, 6, Mode.Fight, 30f);
        Compare("6 Heavies, protected, 12.5 s", UnitType.Heavy, 6, Mode.Protected, 12.5f);
        Compare("30 Scouts, fight, 30 s", UnitType.Scout, 30, Mode.Fight, 30f);
        report.AppendLine(Line("Barracks, 32 s", Average((l, s) => Building(l, Old)), Average((l, s) => Building(l, tune))));
        return report.ToString();
    }

    [Test, Explicit("A measurement, not a check: run it by name and read the log.")]
    public void MeasureOldAgainstNew() => Debug.Log(Report(Shipped));
}
