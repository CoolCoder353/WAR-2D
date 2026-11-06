using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>What kind of damage a <see cref="DamageHit"/> is (v0.20): flat armour, blast resistance and the death's cause follow it.</summary>
    public static class HitKinds
    {
        /// <summary>Gunfire and flame hits (direct and splash): flat armour applies.</summary>
        public const byte Shot = 0;
        /// <summary>A Bomber's, a shell's or a Mine's blast: blast resistance applies.</summary>
        public const byte Blast = 1;
        /// <summary>Burning: neither applies.</summary>
        public const byte Fire = 2;
    }

    /// <summary>The status bits a client is told about a unit it knows (v0.20, <c>CombatBatch</c>).</summary>
    public static class UnitStatusBits
    {
        public const byte Burning = 1;
        public const byte Airborne = 2;
        public const byte Aiming = 4;
    }

    /// <summary>
    /// A unit's v0.20 combat state, stored by its id's index (as the v0.18 bomber aims were) because slots move between ticks: its fire,
    /// a Sniper's aim, a Bomber's leap and run-in point, and what last hurt it. A record whose <see cref="Id"/> isn't the unit's is
    /// stale (the index was reused) and counts as empty; every writer starts from <see cref="For"/>.
    /// </summary>
    public struct UnitCombat
    {
        public int Id;
        /// <summary>Seconds of fire left (0: not burning), and the owner of the Flamethrower that lit it last (who gets the kill).</summary>
        public float BurnLeft;
        public int BurnBy;
        /// <summary>A Sniper's aim: seconds aimed so far and the id it aims at (0: not aiming).</summary>
        public float Aim;
        public int AimTarget;
        /// <summary>A Bomber's leap: the ticks it left the ground and lands, and the line it flies (<see cref="LeapEnd"/> 0: none).</summary>
        public int LeapStart, LeapEnd;
        public float2 LeapFrom, LeapTo;
        /// <summary>A Bomber's run-in point this tick (the army's centre, or a building side's middle) when <see cref="HasRunPoint"/>.</summary>
        public float2 RunPoint;
        public byte HasRunPoint;
        /// <summary>The <see cref="HitKinds"/> of the last hit it took: its death's cause.</summary>
        public byte LastHit;

        /// <summary>The record for <paramref name="id"/>: the stored one when it is that unit's, else a fresh one.</summary>
        public static UnitCombat For(in UnitCombat stored, int id) => stored.Id == id ? stored : new UnitCombat { Id = id };

        /// <summary>True while the unit is in the air at <paramref name="tick"/>.</summary>
        public bool AirborneAt(int id, int tick) => Id == id && LeapEnd > 0 && tick < LeapEnd;

        /// <summary>True while the unit burns.</summary>
        public bool BurningFor(int id) => Id == id && BurnLeft > 0f;

        /// <summary>The <see cref="UnitStatusBits"/> a client is shown at <paramref name="tick"/>.</summary>
        public byte StatusAt(int id, int tick)
        {
            if (Id != id) return 0;
            byte bits = 0;
            if (BurnLeft > 0f) bits |= UnitStatusBits.Burning;
            if (LeapEnd > 0 && tick < LeapEnd) bits |= UnitStatusBits.Airborne;
            if (AimTarget != 0) bits |= UnitStatusBits.Aiming;
            return bits;
        }
    }

    /// <summary>A building's fire (v0.20), by its id's index; stale when <see cref="Id"/> isn't the building's.</summary>
    public struct BuildingFire
    {
        public int Id;
        public float Left;
        public int By;
    }

    /// <summary>An Artillery shell in flight (v0.20): it lands at <see cref="To"/> on <see cref="LandTick"/>, even if its gun has died.</summary>
    public struct Shell
    {
        /// <summary>A per-match serial, so replication tells each client of each shell once.</summary>
        public int Id;
        public float2 From, To;
        public int LaunchTick, LandTick;
        public int OwnerId;
        public byte OwnerSlot;
        /// <summary>The firing unit's type (its blast radius, damage and damage-table row) and id.</summary>
        public byte Type;
        public int ArtilleryId;
    }

    /// <summary>A shot the attack job asks the shell stage to launch.</summary>
    public struct ShellLaunch
    {
        public float2 From, To;
        public int OwnerId;
        public byte OwnerSlot;
        public byte Type;
        public int ArtilleryId;
    }

    /// <summary>A leap that started this tick, for replication.</summary>
    public struct LeapEvent
    {
        public int Id;
        public float2 From, To;
        public int Start, End;
    }

    /// <summary>The v0.20 hit rules (spec §7.4), shared by every job that makes a hit.</summary>
    public static class CombatRules
    {
        /// <summary>
        /// A direct hit (gunfire, a flame's direct or splash hit): <paramref name="raw"/> (damage times the damage table) less flat
        /// armour, never under <paramref name="minHit"/>; a hit the table zeroes stays 0.
        /// </summary>
        public static float DirectHit(float raw, float armour, float minHit) => raw <= 0f ? 0f : math.max(minHit, raw - armour);

        /// <summary>A blast's share after resistance: <paramref name="amount"/> times one less <paramref name="resist"/>.</summary>
        public static float Resisted(float amount, float resist) => amount * (1f - math.saturate(resist));

        /// <summary>
        /// Where a led shell lands: where a target at <paramref name="target"/> driving at <paramref name="velocity"/> will be when a
        /// shell from <paramref name="from"/> at <paramref name="speed"/> gets there (two refinements), capped at
        /// <paramref name="maxReach"/> from the gun.
        /// </summary>
        public static float2 Lead(float2 from, float2 target, float2 velocity, float speed, float maxReach)
        {
            float2 at = target;
            for (int k = 0; k < 2; k++) at = target + velocity * (math.distance(from, at) / math.max(0.01f, speed));
            float2 d = at - from;
            float length = math.length(d);
            return length > maxReach && length > 0f ? from + d * (maxReach / length) : at;
        }
    }
}
