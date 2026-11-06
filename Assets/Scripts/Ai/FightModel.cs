using Config;
using Unity.Mathematics;

namespace WAR2D.Ai
{
    /// <summary>
    /// One side of a fight as the fight model sees it (AI overhaul §3.1): hit points by armour class, damage per second
    /// against each class, health-weighted range, and its worth (cost) for trades and targets.
    /// </summary>
    public struct Force
    {
        public float HpLight, HpHeavy, HpBuilding;
        public float DpsLight, DpsHeavy, DpsBuilding;
        /// <summary>Sum of range × hit points (the health-weighted mean range is this over <see cref="Hp"/>).</summary>
        public float RangeHp;
        /// <summary>Its worth: the cost of what is in it, scaled by health.</summary>
        public float Value;
        public int Count;

        public float Hp => HpLight + HpHeavy + HpBuilding;

        /// <summary>True when it holds nothing that fights.</summary>
        public bool Empty => Count == 0 || Hp <= 0f;

        /// <summary>Adds another force to this one.</summary>
        public void Add(in Force more)
        {
            HpLight += more.HpLight; HpHeavy += more.HpHeavy; HpBuilding += more.HpBuilding;
            DpsLight += more.DpsLight; DpsHeavy += more.DpsHeavy; DpsBuilding += more.DpsBuilding;
            RangeHp += more.RangeHp; Value += more.Value; Count += more.Count;
        }

        /// <summary>This force with its strength multiplied by <paramref name="factor"/> (estimate noise): damage scaled, health kept.</summary>
        public Force Scaled(float factor)
        {
            Force f = this;
            f.DpsLight *= factor; f.DpsHeavy *= factor; f.DpsBuilding *= factor;
            return f;
        }
    }

    /// <summary>
    /// Who wins a fight (AI overhaul §3.1), from the public numbers every client has: each side's damage per second
    /// against the other's armour mix (the damage table, splash counted against <c>SplashTargets</c> more targets), a
    /// bonus for out-ranging the other side, then Lanchester's square law (strength = damage per second × hit points).
    /// Pure: callers build the forces from what the AI may know.
    /// </summary>
    public static class FightModel
    {
        /// <summary>Adds a unit of <paramref name="type"/> with <paramref name="healthShare"/> of its health left.</summary>
        public static void AddUnit(ref Force f, GameConfigData game, UnitType type, float healthShare, AiPolicyConfig policy)
        {
            if (type == UnitType.None || type == UnitType.Builder || !game.Units.TryGetValue(type, out UnitConfig c)) return;
            float share = math.saturate(healthShare);
            float hp = c.Health * share;
            if (c.Armour == TargetClass.HeavyUnit) f.HpHeavy += hp; else f.HpLight += hp;
            if (c.IsBomber)
            {
                // Its leap can't be shot down, so only its run in counts against it (the blast is the whole of its damage).
                float rate = c.BlastDamage / math.max(1f, policy.BomberFightSeconds) * (1f + c.BlastRadius * policy.SplashTargets);
                f.DpsLight += rate * game.Damage.Multiplier(type, TargetClass.LightUnit);
                f.DpsHeavy += rate * game.Damage.Multiplier(type, TargetClass.HeavyUnit) * (1f - ClassResist(game, TargetClass.HeavyUnit));
                f.DpsBuilding += rate * game.Damage.Multiplier(type, TargetClass.Building);
            }
            else
            {
                f.DpsLight += Rate(game, type, c, TargetClass.LightUnit, policy);
                f.DpsHeavy += Rate(game, type, c, TargetClass.HeavyUnit, policy);
                f.DpsBuilding += Rate(game, type, c, TargetClass.Building, policy);
            }
            f.RangeHp += c.Range * hp;
            f.Value += c.UpfrontCost * share;
            f.Count++;
        }

        /// <summary>Adds a finished attacking building (a Gun turret) with <paramref name="healthShare"/> of its health.</summary>
        public static void AddTurret(ref Force f, GameConfigData game, BuildingType type, float healthShare)
        {
            if (!game.Buildings.TryGetValue(type, out BuildingConfig c) || !c.IsAttacker) return;
            float share = math.saturate(healthShare);
            float hp = c.Health * share;
            f.HpBuilding += hp;
            float shots = c.AttackInterval > 0f ? 1f / c.AttackInterval : 0f;
            float minHit = game.Combat.MinHit;
            float Hit(TargetClass k) { float raw = c.Damage * game.Damage.Multiplier(type, k); return raw <= 0f ? 0f : math.max(minHit, raw - ClassArmour(game, k)); }
            f.DpsLight += shots * Hit(TargetClass.LightUnit);
            f.DpsHeavy += shots * Hit(TargetClass.HeavyUnit);
            f.DpsBuilding += shots * Hit(TargetClass.Building);
            f.RangeHp += c.Range * hp;
            f.Value += c.UpfrontCost * share;
            f.Count++;
        }

        /// <summary>
        /// v0.20: a unit's damage per second against a target class, the way <c>tools/balance-sheet.js</c> works a shot out: less the
        /// class's flat armour (never under <c>Combat/MinHit</c>), plus a Flamethrower's fire, over its reload and a Sniper's aim; a
        /// shell's blast less the class's resistance, times <c>ShellHitShare</c>; splash counted against <c>SplashTargets</c> more targets.
        /// </summary>
        public static float Rate(GameConfigData game, UnitType type, UnitConfig c, TargetClass k, AiPolicyConfig policy)
        {
            float cycle = c.AttackInterval + (type == UnitType.Sniper ? game.Sniper.AimSeconds : 0f);
            if (cycle <= 0f) return 0f;
            float raw = c.Damage * game.Damage.Multiplier(type, k);
            float hit, radius;
            if (c.FiresShells)
            {
                hit = raw * (1f - ClassResist(game, k)) * policy.ShellHitShare;
                radius = c.BlastRadius;
            }
            else
            {
                hit = raw <= 0f ? 0f : math.max(game.Combat.MinHit, raw - ClassArmour(game, k));
                if (type == UnitType.Flamethrower) hit += game.Burn.Dps * game.Burn.Rate(k) * cycle;
                radius = c.Splash;
            }
            return hit / cycle * (1f + radius * policy.SplashTargets);
        }

        /// <summary>The flat armour a class is modelled with: its most armoured unit's (a Heavy's for heavy units).</summary>
        public static float ClassArmour(GameConfigData game, TargetClass k)
        {
            float armour = 0f;
            foreach (UnitConfig u in game.Units.Values) if (u.Armour == k) armour = math.max(armour, u.FlatArmour);
            return armour;
        }

        /// <summary>The blast resistance a class is modelled with: its most resistant fighting unit's (Bombers aside: they never take a fight).</summary>
        public static float ClassResist(GameConfigData game, TargetClass k)
        {
            float resist = 0f;
            foreach (UnitConfig u in game.Units.Values) if (u.Armour == k && !u.IsBomber) resist = math.max(resist, u.BlastResist);
            return resist;
        }

        /// <summary>A force's damage per second against another's armour mix (weighted by the other's hit points per class).</summary>
        public static float DpsAgainst(in Force attacker, in Force target)
        {
            float hp = target.Hp;
            if (hp <= 0f) return 0f;
            return (attacker.DpsLight * target.HpLight + attacker.DpsHeavy * target.HpHeavy + attacker.DpsBuilding * target.HpBuilding) / hp;
        }

        /// <summary>A force's health-weighted mean range.</summary>
        public static float Range(in Force f) => f.Hp > 0f ? f.RangeHp / f.Hp : 0f;

        /// <summary>
        /// Our strength over theirs (above 1 favours us): Lanchester's square law, each side's damage against the other's
        /// mix times its hit points, the longer-ranged side's damage raised by up to <paramref name="rangeWeight"/>.
        /// Infinite against nothing that fights; 0 when we have nothing.
        /// </summary>
        public static float Advantage(in Force ours, in Force theirs, float rangeWeight)
        {
            if (ours.Empty) return 0f;
            if (theirs.Empty) return float.PositiveInfinity;
            float ourDps = DpsAgainst(ours, theirs), theirDps = DpsAgainst(theirs, ours);
            float ourRange = Range(ours), theirRange = Range(theirs);
            if (ourRange > theirRange) ourDps *= 1f + rangeWeight * math.min(1f, (ourRange - theirRange) / math.max(1f, theirRange));
            else if (theirRange > ourRange) theirDps *= 1f + rangeWeight * math.min(1f, (theirRange - ourRange) / math.max(1f, ourRange));
            float ourStrength = ourDps * ours.Hp, theirStrength = theirDps * theirs.Hp;
            if (theirStrength <= 0f) return float.PositiveInfinity;
            return ourStrength / theirStrength;
        }

        /// <summary>The share of its force a side expects to keep at <paramref name="advantage"/> (0 when it loses).</summary>
        public static float Survivors(float advantage) =>
            advantage <= 1f ? 0f : float.IsPositiveInfinity(advantage) ? 1f : math.sqrt(1f - 1f / advantage);
    }
}
