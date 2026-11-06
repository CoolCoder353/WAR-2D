using System;
using System.Collections.Generic;
using Config;

/// <summary>
/// Strong and Weak against for the Field manual (v0.18, spec 2.3): one signed score per pair from an equal-cost fight on open
/// ground (range, minimum range, splash and numbers), run both ways round and averaged, so a matchup can only read one way. A
/// bomber has no gun for that fight, so it is ranked by the worth one blast takes from a packed group of the target, after what
/// the target shoots down on the way in. This is <c>tools/balance-sheet.js</c>'s fight and <c>tools/manual-mock.js</c>'s table;
/// a test compares the lists. Pure.
/// </summary>
public static class ManualRules
{
    private const int Budget = 3000, MaxListed = 3, MinMargin = 20, BomberMargin = 90;
    private const float Packing = 0.2f, Dt = 0.05f, MaxSeconds = 600f;
    private static readonly TargetClass[] Classes = { TargetClass.LightUnit, TargetClass.HeavyUnit, TargetClass.Building, TargetClass.Wall };

    /// <summary>A type's lists, by type name ("Tank", "GunTurret").</summary>
    public struct Lists
    {
        public List<string> Strong, Weak;
    }

    private sealed class Fighter
    {
        public string Type;
        public float Hp, Damage, Range, Interval, Speed, Cost, Radius, Splash, MinRange;
        /// <summary>v0.20: flat armour, blast resistance, aim seconds; whether its shots are shells, and whether they set fire.</summary>
        public float Armour, Resist, Aim;
        public bool Shell, Ignites;
        public TargetClass Class;
        public float[] Mult;
        public float Against(TargetClass c) => Mult[Array.IndexOf(Classes, c)];
        public float Cycle => Interval + Aim;
    }

    /// <summary>v0.20's shot rules, as <c>tools/balance-sheet.js</c>'s <c>hitOn</c>: the least hit, fire and the shells' hit share.</summary>
    private sealed class Rules
    {
        public float MinHit, BurnDps, ShellHitShare;
        public BurnConfig Burn;
    }

    private static Rules rules;

    /// <summary>One shot of <paramref name="a"/> on <paramref name="b"/> (see <c>hitOn</c> in balance-sheet.js).</summary>
    private static float HitOn(Fighter a, Fighter b)
    {
        float raw = a.Damage * a.Against(b.Class);
        if (a.Shell) return raw * (1f - b.Resist) * rules.ShellHitShare;
        float hit = raw > 0f ? Math.Max(rules.MinHit, raw - b.Armour) : 0f;
        if (a.Ignites) hit += rules.BurnDps * rules.Burn.Rate(b.Class) * a.Cycle;
        return hit;
    }

    /// <summary>The whole table, by attacker then target; missing means no fight (Builders).</summary>
    public static Dictionary<string, Dictionary<string, int>> Table(GameConfigData config)
    {
        var score = new Dictionary<string, Dictionary<string, int>>();
        void Set(string a, string b, int v)
        {
            if (!score.TryGetValue(a, out var ra)) score[a] = ra = new Dictionary<string, int>();
            if (!score.TryGetValue(b, out var rb)) score[b] = rb = new Dictionary<string, int>();
            ra[b] = v;
            rb[a] = -v;
        }

        rules = new Rules { MinHit = config.Combat.MinHit, BurnDps = config.Burn.Dps, Burn = config.Burn, ShellHitShare = config.Ai?.Policy != null ? config.Ai.Policy.ShellHitShare : 1f };
        var fighters = new List<Fighter>();
        Fighter bomber = null;
        foreach (UnitType type in UnitOrder)
        {
            if (!config.Units.TryGetValue(type, out UnitConfig u)) continue;
            var f = new Fighter
            {
                Type = type.ToString(), Hp = u.Health, Damage = u.Damage, Range = u.Range, Interval = u.AttackInterval, Speed = u.MoveSpeed, Cost = u.UpfrontCost,
                Radius = u.Radius, Splash = u.FiresShells ? u.BlastRadius : u.Splash, MinRange = u.MinRange, Class = u.Armour, Mult = new float[Classes.Length],
                Armour = u.FlatArmour, Resist = u.BlastResist, Aim = type == UnitType.Sniper ? config.Sniper.AimSeconds : 0f,
                Shell = u.FiresShells, Ignites = type == UnitType.Flamethrower,
            };
            for (int c = 0; c < Classes.Length; c++) f.Mult[c] = config.Damage.Multiplier(type, Classes[c]);
            if (u.IsBomber) bomber = f;
            else if (u.Damage > 0) fighters.Add(f);
        }
        BuildingConfig t = config.GetBuilding(BuildingType.GunTurret);
        var turret = new Fighter
        {
            Type = BuildingType.GunTurret.ToString(), Hp = t.Health, Damage = t.Damage, Range = t.Range, Interval = t.AttackInterval, Cost = t.UpfrontCost, Class = TargetClass.Building, Mult = new float[Classes.Length],
        };
        for (int c = 0; c < Classes.Length; c++) turret.Mult[c] = config.Damage.Multiplier(BuildingType.GunTurret, Classes[c]);
        fighters.Add(turret);

        for (int i = 0; i < fighters.Count; i++)
            for (int j = i + 1; j < fighters.Count; j++)
            {
                float v = (Once(fighters[i], fighters[j]) - Once(fighters[j], fighters[i])) / 2f * 100f;
                Set(fighters[i].Type, fighters[j].Type, (int)Math.Floor(v + 0.5f));
            }

        if (bomber != null)
        {
            UnitConfig b = config.GetUnit((UnitType)Enum.Parse(typeof(UnitType), bomber.Type));
            var worth = new List<(string type, float v)>();
            foreach (Fighter x in fighters)
            {
                float hits = x.Radius > 0f ? 1f + (Packing / ((float)Math.PI * x.Radius * x.Radius)) * (float)Math.PI * b.BlastRadius * b.BlastRadius : 1f;
                float perBlast = Math.Min(1f, b.BlastDamage * bomber.Against(x.Class) / x.Hp) * hits * x.Cost;
                float shotDown = Count(x) * (x.Range / bomber.Speed / x.Cycle) * Math.Min(1f, HitOn(x, bomber) / bomber.Hp);
                worth.Add((x.Type, perBlast * Math.Max(0f, 1f - shotDown / Count(bomber))));
            }
            worth.Sort((p, q) => q.v.CompareTo(p.v));
            for (int i = 0; i < MaxListed && i < worth.Count; i++) Set(bomber.Type, worth[i].type, BomberMargin);
            for (int i = System.Math.Max(0, worth.Count - MaxListed); i < worth.Count; i++) Set(bomber.Type, worth[i].type, -BomberMargin);
        }
        return score;
    }

    /// <summary>The config's unit order, which breaks ties as the mock's script does.</summary>
    private static readonly UnitType[] UnitOrder =
    {
        UnitType.Tank, UnitType.Builder, UnitType.Digger, UnitType.Scout, UnitType.Flamethrower, UnitType.Heavy, UnitType.Artillery, UnitType.Sniper, UnitType.Bomber,
    };

    /// <summary>One type's lists: up to three wins and three losses by at least the margin; workers aren't named as "strong against".</summary>
    public static Lists Counters(Dictionary<string, Dictionary<string, int>> table, string type)
    {
        var strong = new List<(string t, int v)>();
        var weak = new List<(string t, int v)>();
        if (table.TryGetValue(type, out var row))
            foreach (KeyValuePair<string, int> p in row)
            {
                if (p.Value >= MinMargin && p.Key != nameof(UnitType.Builder) && p.Key != nameof(UnitType.Digger)) strong.Add((p.Key, p.Value));
                else if (p.Value <= -MinMargin) weak.Add((p.Key, p.Value));
            }
        strong.Sort((a, b) => b.v.CompareTo(a.v));
        weak.Sort((a, b) => a.v.CompareTo(b.v));
        return new Lists { Strong = Top(strong), Weak = Top(weak) };
    }

    private static List<string> Top(List<(string t, int v)> rows)
    {
        var names = new List<string>(MaxListed);
        for (int i = 0; i < rows.Count && i < MaxListed; i++) names.Add(rows[i].t);
        return names;
    }

    private static int Count(Fighter f) => Math.Max(1, (int)Math.Floor(Budget / f.Cost));

    private static float Once(Fighter a, Fighter b)
    {
        (int winner, float keep) = Duel(a, Count(a), b, Count(b));
        return winner < 0 ? 0f : winner == 0 ? keep : -keep;
    }

    private static float SplashWeight(Fighter attacker, Fighter target)
    {
        if (attacker.Splash <= 0f || target.Radius <= 0f) return 0f;
        float density = Packing / ((float)Math.PI * target.Radius * target.Radius);
        return density * 2f * (float)Math.PI * attacker.Splash * attacker.Splash / 3f;
    }

    /// <summary>Two armies: the shorter-ranged walks in under fire, then everyone fires at the enemy's front unit; splash also hits those behind it.</summary>
    private static (int winner, float keep) Duel(Fighter a, int nA, Fighter b, int nB)
    {
        var f = new[] { a, b };
        var hp = new[] { new float[nA], new float[nB] };
        Array.Fill(hp[0], a.Hp);
        Array.Fill(hp[1], b.Hp);
        var due = new float[2];
        var counts = new int[2];
        var volleys = new int[2];
        var can = new bool[2];
        float gap = Math.Max(a.Range, b.Range), t = 0f;
        var weights = new[] { SplashWeight(a, b), SplashWeight(b, a) };
        while (t < MaxSeconds)
        {
            counts[0] = Alive(hp[0]);
            counts[1] = Alive(hp[1]);
            if (counts[0] == 0 || counts[1] == 0) break;
            bool moved = false;
            for (int s = 0; s < 2; s++)
                if (gap > f[s].Range && f[s].Speed > 0f) { gap = Math.Max(f[s].Range, gap - f[s].Speed * Dt); moved = true; }
            for (int s = 0; s < 2; s++) can[s] = f[s].Damage > 0f && gap <= f[s].Range + 1e-6f && gap >= f[s].MinRange - 1e-6f;
            if (!moved && !can[0] && !can[1]) return (-1, 0f);
            for (int s = 0; s < 2; s++)
            {
                if (!can[s]) { due[s] = 0f; volleys[s] = 0; continue; }
                due[s] += counts[s] * Dt / f[s].Cycle;
                volleys[s] = (int)Math.Floor(due[s]);
                due[s] -= volleys[s];
            }
            for (int s = 0; s < 2; s++)
            {
                float[] enemy = hp[1 - s];
                float hit = HitOn(f[s], f[1 - s]), w = weights[s];
                for (int k = 0; k < volleys[s]; k++)
                {
                    int front = Array.FindIndex(enemy, h => h > 0f);
                    if (front < 0) break;
                    if (w > 0f)
                    {
                        int want = (int)Math.Ceiling(w), found = 0;
                        for (int j = front + 1; j < enemy.Length && found < want; j++) if (enemy[j] > 0f) found++;
                        if (found > 0)
                        {
                            float each = hit * Math.Min(w, found) / found;
                            int done = 0;
                            for (int j = front + 1; j < enemy.Length && done < found; j++)
                                if (enemy[j] > 0f) { enemy[j] = Math.Max(0f, enemy[j] - each); done++; }
                        }
                    }
                    enemy[front] = Math.Max(0f, enemy[front] - hit);
                }
            }
            t += Dt;
        }
        counts[0] = Alive(hp[0]);
        counts[1] = Alive(hp[1]);
        if (counts[0] > 0 && counts[1] > 0) return (-1, 0f);
        int winner = counts[0] > 0 ? 0 : 1;
        float sum = 0f;
        foreach (float h in hp[winner]) sum += h;
        return (winner, sum / (hp[winner].Length * f[winner].Hp));
    }

    private static int Alive(float[] hp)
    {
        int n = 0;
        foreach (float h in hp) if (h > 0f) n++;
        return n;
    }
}
