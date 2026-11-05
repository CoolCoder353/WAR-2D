using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;

/// <summary>
/// Client-only effects from the <c>Effects</c> atlas: explosions, bomb blasts, muzzle flashes, tracers, artillery shells, flames, dig
/// debris, repair sparks, building collapses, smoke and fire on damaged buildings, gem glints, bullet hit sparks, and dust
/// puffs (a unit made, a building finished). Each kind has a
/// fixed pool of sprites made once (no allocation per effect after warm-up); when a pool is full the oldest
/// effect is recycled. A runner object advances the frames at <see cref="ArtStyle.FramesPerSecond"/>.
/// </summary>
public static class Effects
{
    /// <summary>Pool sizes: the most of each kind on screen at once.</summary>
    public const int ExplosionPool = 128, FlashPool = 128, TracerPool = 256, BlastPool = 16, DebrisPool = 64, SparkPool = 64;

    /// <summary>Pool sizes for the v0.12 weapons: sniper tracers, artillery shells and their impacts, flame streams.</summary>
    public const int SniperPool = 64, ShellPool = 64, ImpactPool = 64, StreamPool = 128, BomberBlastPool = 16;

    /// <summary>Pool sizes for the v0.13 defences: turret muzzle flashes and Jammer pulses.</summary>
    public const int TurretFlashPool = 64, PulsePool = 32;

    /// <summary>Pool sizes for the polish effects: smoke and fire on damaged buildings, gem glints, and collapse blasts.</summary>
    public const int SmokePool = 160, FirePool = 96, SparklePool = 48, CollapsePool = 16;

    /// <summary>Pool sizes for polish A, E and J (2026-11-01): bullet hit sparks, and dust puffs.</summary>
    public const int HitSparkPool = 128, DustPool = 96;

    /// <summary>Fewest dust puffs ringing a finished building, and how many each footprint tile adds to its side.</summary>
    public const int FinishPuffsMin = 4, FinishPuffsPerTile = 3;

    /// <summary>Tiles per second a smoke puff rises while it plays.</summary>
    public const float SmokeRise = 0.7f;

    /// <summary>Most delayed blasts waiting at once (a collapse's follow-up explosions); more are dropped.</summary>
    public const int MaxDelayed = 128;

    /// <summary>Pool sizes for the combat animations (2026-11-06): each gun size's flash, and the Scout's shots.</summary>
    public const int BurstPool = 96, ScoutTracerPool = 128;

    /// <summary>Most puffs of shell smoke at once. A shell's ground shadow, and a flame's fire, go one for one with the shell and the stream.</summary>
    public const int ShellPuffPool = 160;

    /// <summary>Seconds a tracer takes to reach its target.</summary>
    public const float TracerSeconds = 0.2f;

    /// <summary>How long a shot's streak is in the air, in tiles.</summary>
    public const float ScoutShotTiles = 1f, TankShotTiles = 1.5f, HeavyShotTiles = 1.8f, SniperShotTiles = 10f;

    /// <summary>The fewest seconds an artillery shell takes to land.</summary>
    public const float ShellSeconds = 0.5f;

    /// <summary>Tiles a second a shell flies, slow enough to watch one cross the screen.</summary>
    public const float ShellTilesPerSecond = 14f;

    /// <summary>How high a shell's arc rises: this share of the shot's length, up to <see cref="ShellMaxHeight"/> tiles.</summary>
    public const float ShellArcShare = 0.25f, ShellMaxHeight = 4f;

    /// <summary>Tiles a shell flies between the puffs of its trail.</summary>
    public const float ShellPuffSpacing = 0.35f;

    /// <summary>Tiles a flame stream sprite is wide; it is stretched to reach the target.</summary>
    public const float StreamTiles = 2f;


    /// <summary>One pooled sprite and what it is playing.</summary>
    private sealed class Slot
    {
        public GameObject Go;
        public SpriteRenderer Sprite;
        public float Start;
        public Vector3 From, To;
        public bool Active;
        /// <summary>Seconds a flying effect takes (tracer or shell).</summary>
        public float Seconds = TracerSeconds;
        /// <summary>True for a shell: it plays a <see cref="ShellImpact"/> where it lands.</summary>
        public bool ImpactOnLanding;
        /// <summary>True for a bullet: it plays a <see cref="HitSpark"/> where it lands.</summary>
        public bool SparkOnLanding;
        /// <summary>World units per second the effect drifts while it plays (rising smoke, a flash on a moving gun).</summary>
        public Vector3 Drift;
        /// <summary>A shot: tiles its streak is in the air, and tiles its sprite is drawn at scale 1.</summary>
        public float Length, ArtLength;
        /// <summary>A shell: where it last left a puff.</summary>
        public Vector3 LastPuff;
        /// <summary>The sprite that goes with this one: a shell's ground shadow, a stream's fire.</summary>
        public Slot Partner;
        /// <summary>When a looping effect (a flame) ends; 0 for one that plays its frames once.</summary>
        public float Until;
        /// <summary>A flame: the ids of the unit burning and of what it burns.</summary>
        public int Attacker, Target;
    }

    private sealed class Pool
    {
        public readonly string Id;
        public readonly int Size;
        public readonly List<Slot> Slots = new List<Slot>();
        public int Next; // ring index: the next slot to use (the oldest when full)

        public Pool(string id, int size) { Id = id; Size = size; }
    }

    private static readonly Pool Explosions = new Pool("Explosion", ExplosionPool);
    private static readonly Pool Flashes = new Pool("MuzzleFlash", FlashPool);
    private static readonly Pool Tracers = new Pool("Tracer", TracerPool);
    private static readonly Pool Blasts = new Pool("BombBlast", BlastPool);
    private static readonly Pool Debris = new Pool("DigDebris", DebrisPool);
    private static readonly Pool Sparks = new Pool("RepairSparks", SparkPool);
    private static readonly Pool SniperTracers = new Pool("SniperTracer", SniperPool);
    private static readonly Pool Shells = new Pool("ArtilleryShell", ShellPool);
    private static readonly Pool Impacts = new Pool("ShellImpact", ImpactPool);
    private static readonly Pool Streams = new Pool("FlameStream", StreamPool);
    private static readonly Pool FlameFires = new Pool("FlameSplash", StreamPool);
    private static readonly Pool BurstsScout = new Pool("BurstScout", BurstPool);
    private static readonly Pool BurstsSniper = new Pool("BurstSniper", BurstPool);
    private static readonly Pool BurstsTank = new Pool("BurstTank", BurstPool);
    private static readonly Pool BurstsHeavy = new Pool("BurstHeavy", BurstPool);
    private static readonly Pool ScoutTracers = new Pool("ScoutTracer", ScoutTracerPool);
    private static readonly Pool ShellShadows = new Pool("ShellShadow", ShellPool);
    private static readonly Pool ShellPuffs = new Pool("ShellPuff", ShellPuffPool);
    private static readonly Pool BomberBlasts = new Pool("BomberBlast", BomberBlastPool);
    private static readonly Pool TurretFlashes = new Pool("TurretMuzzle", TurretFlashPool);
    private static readonly Pool Pulses = new Pool("JamPulse", PulsePool);
    private static readonly Pool Smokes = new Pool("Smoke", SmokePool);
    private static readonly Pool Fires = new Pool("Fire", FirePool);
    private static readonly Pool Sparkles = new Pool("GemSparkle", SparklePool);
    private static readonly Pool Collapses = new Pool("BomberBlast", CollapsePool);
    private static readonly Pool HitSparks = new Pool("HitSpark", HitSparkPool);
    private static readonly Pool Dust = new Pool("DustPuff", DustPool);

    /// <summary>A blast waiting to go off (a collapse's follow-up explosions and debris).</summary>
    private struct Delayed
    {
        public float At;
        public Vector2 Position;
        public bool Debris;
    }

    private static readonly List<Delayed> delayed = new List<Delayed>();
    private static Runner runner;
    private static ArtAtlas atlas;

    /// <summary>Sprite objects created so far (tests: stays at most the pool sizes).</summary>
    public static int Created { get; private set; }

    /// <summary>Explosions playing now.</summary>
    public static int ActiveExplosions => Count(Explosions);

    /// <summary>An explosion centred on <paramref name="position"/>.</summary>
    public static void Explosion(Vector2 position)
    {
        Slot s = Take(Explosions);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("explosion_small", position);
    }

    /// <summary>A bomb going off at <paramref name="position"/> (3 × 3 tiles of blast).</summary>
    public static void BombBlast(Vector2 position)
    {
        Slot s = Take(Blasts);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("bomb_blast", position);
    }

    /// <summary>A Suicide bomber going off at <paramref name="position"/> (4 × 4 tiles of blast).</summary>
    public static void BomberBlast(Vector2 position)
    {
        Slot s = Take(BomberBlasts);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("bomber_blast", position);
    }

    /// <summary>Rock breaking where a tile was dug out (or a wall fell), centred on the tile.</summary>
    public static void DigDebris(Vector2 position)
    {
        Slot s = Take(Debris);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("dig", position);
    }

    /// <summary>Sparks on a building being repaired.</summary>
    public static void RepairSparks(Vector2 position)
    {
        Slot s = Take(Sparks);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("repair", position);
    }

    /// <summary>A muzzle flash at <paramref name="position"/>.</summary>
    public static void MuzzleFlash(Vector3 position, string sound = "shot_tank")
    {
        Slot s = Take(Flashes);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx(sound, position);
    }

    /// <summary>The sound a unit type's shot makes (one parameter set per weapon in Art/Audio/sfx).</summary>
    public static string ShotSound(UnitType attacker) => attacker switch
    {
        UnitType.Scout => "shot_scout",
        UnitType.Flamethrower => "shot_flamethrower",
        UnitType.Sniper => "shot_sniper",
        UnitType.Heavy => "shot_heavy",
        UnitType.Artillery => "shot_artillery",
        UnitType.Builder => "shot_builder",
        UnitType.Digger => "shot_digger",
        _ => "shot_tank",
    };

    /// <summary>A shell landing at <paramref name="position"/>: the impact sprite and its sound.</summary>
    public static void ShellImpact(Vector2 position)
    {
        Slot s = Take(Impacts);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("shell_impact", position);
    }

    /// <summary>How far from a shooter's centre its barrel ends, in tiles (the muzzle flash goes there).</summary>
    public const float BarrelReach = 0.45f;

    /// <summary>
    /// A shot from a gun with no turret to turn (a Builder's, a Digger's): a tracer from the shooter to the target, with a muzzle flash
    /// at the shooter's barrel. Without <paramref name="line"/> (the Tracers setting thinning them out) only the flash and sound play.
    /// </summary>
    public static void Tracer(Vector3 from, Vector3 to, UnitType attacker = UnitType.Tank, bool line = true)
    {
        Vector3 heading = to - from;
        heading.z = 0f;
        Vector3 muzzle = from + (heading.sqrMagnitude > 1e-6f ? heading.normalized * BarrelReach : Vector3.zero);
        MuzzleFlash(muzzle, ShotSound(attacker));
        if (line) Shot(Tracers, from, to, TankShotTiles);
    }

    /// <summary>Starts a shot's streak flying from <paramref name="from"/> to <paramref name="to"/>, with a <see cref="HitSpark"/> where it lands.</summary>
    private static Slot Shot(Pool pool, Vector3 from, Vector3 to, float length)
    {
        Slot s = Take(pool);
        if (s == null) return null;
        s.SparkOnLanding = true;
        s.From = new Vector3(from.x, from.y, 0f);
        s.To = new Vector3(to.x, to.y, 0f);
        s.Length = length;
        s.ArtLength = s.Sprite.sprite.rect.width / ArtStyle.PixelsPerUnit;
        Vector3 d = s.To - s.From;
        s.Go.transform.SetPositionAndRotation(s.From, Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg));
        s.Go.transform.localScale = new Vector3(0f, 1f, 1f);
        return s;
    }

    /// <summary>
    /// Where a shot's streak lies <paramref name="t"/> (0 to 1) of its flight: its head runs from <paramref name="from"/> to
    /// <paramref name="to"/>, and it is at most <paramref name="length"/> long, so it never starts behind the muzzle or passes the target.
    /// Returns its centre; <paramref name="drawn"/> is how long it is now.
    /// </summary>
    public static Vector3 ShotCentre(Vector3 from, Vector3 to, float t, float length, out float drawn)
    {
        float total = Vector3.Distance(from, to), head = total * t;
        drawn = Mathf.Min(length, head);
        return total > 1e-5f ? Vector3.Lerp(from, to, (head - drawn * 0.5f) / total) : to;
    }

    /// <summary>The flash at a point, pointing at <paramref name="degrees"/>, drifting with the gun that fired it.</summary>
    private static void Burst(Pool pool, Vector2 at, float degrees, Vector3 drift)
    {
        Slot s = Take(pool);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(at.x, at.y, 0f), Quaternion.Euler(0f, 0f, degrees));
        s.Drift = drift;
    }

    /// <summary>
    /// A shot from a gun on a turning turret (<see cref="WAR2D.Client.Render.TurretAim.Has"/>) at the unit's centre: its sound, one flash per
    /// barrel along the turret's drawn angle (radians), and a tracer from each barrel to <paramref name="to"/>, stopping
    /// <paramref name="stopShort"/> tiles before it (the target's near side). Without <paramref name="line"/> (the Tracers setting) only the flash and
    /// sound play. The Artillery fires a shell instead of a tracer, always, which lands on <paramref name="to"/> itself.
    /// </summary>
    public static void GunShot(UnitType gun, Vector2 centre, float hull, float turret, Vector3 to, float stopShort, bool line, Vector3 drift)
    {
        const float ppu = ArtStyle.PixelsPerUnit;
        if (gun == UnitType.Artillery)
        {
            Vector2 bore = centre + WAR2D.Client.Render.TurretAim.ArtilleryBore(hull, turret) / ppu;
            Vector2 axis = WAR2D.Client.Render.TurretAim.ArtilleryBarrelAxis(turret);
            WAR2D.Audio.AudioService.Sfx(ShotSound(gun), bore);
            Burst(BurstsTank, centre + WAR2D.Client.Render.TurretAim.ArtilleryFlash(hull, turret) / ppu, Mathf.Atan2(axis.y, axis.x) * Mathf.Rad2Deg, drift);
            Shell(bore, to);
            return;
        }
        Pool flashes = gun == UnitType.Scout ? BurstsScout : gun == UnitType.Sniper ? BurstsSniper : gun == UnitType.Heavy ? BurstsHeavy : BurstsTank;
        Pool shots = gun == UnitType.Scout ? ScoutTracers : gun == UnitType.Sniper ? SniperTracers : Tracers;
        float length = gun == UnitType.Scout ? ScoutShotTiles : gun == UnitType.Sniper ? SniperShotTiles : gun == UnitType.Heavy ? HeavyShotTiles : TankShotTiles;
        float degrees = turret * Mathf.Rad2Deg;
        WAR2D.Audio.AudioService.Sfx(ShotSound(gun), centre);
        for (int barrel = 0; barrel < WAR2D.Client.Render.TurretAim.Barrels(gun); barrel++)
        {
            Vector2 muzzle = centre + WAR2D.Client.Render.TurretAim.Muzzle(gun, hull, turret, barrel) / ppu;
            Burst(flashes, muzzle, degrees, drift);
            if (!line) continue;
            Vector2 fire = (Vector2)to - muzzle;
            float side = WAR2D.Client.Render.TurretAim.MuzzleSide(gun, barrel) / ppu;
            Vector2 along = fire.sqrMagnitude > 1e-6f ? fire.normalized : new Vector2(Mathf.Cos(turret), Mathf.Sin(turret));
            Vector2 end = (Vector2)to - along * Mathf.Min(stopShort, fire.magnitude) + new Vector2(-along.y, along.x) * side;
            Shot(shots, muzzle, end, length);
        }
    }

    /// <summary>Seconds a shell takes over a shot of this many tiles.</summary>
    public static float ShellFlightSeconds(float tiles) => Mathf.Max(ShellSeconds, tiles / ShellTilesPerSecond);

    /// <summary>Where a shell is drawn at <paramref name="t"/> (0 to 1) of its flight: along the ground line, raised on the screen by its arc.</summary>
    public static Vector3 ShellPoint(Vector3 from, Vector3 to, float t)
    {
        float height = Mathf.Min(Vector3.Distance(from, to) * ShellArcShare, ShellMaxHeight);
        return Vector3.Lerp(from, to, t) + Vector3.up * (height * 4f * t * (1f - t));
    }

    /// <summary>A shell flying from the gun's bore: its height is only drawn, by a sprite rising over a shadow that keeps to the ground line. It lands as a <see cref="ShellImpact"/> and leaves a crater.</summary>
    private static void Shell(Vector2 from, Vector3 to)
    {
        Slot s = Take(Shells);
        if (s == null) return;
        s.Seconds = ShellFlightSeconds(Vector2.Distance(from, to));
        s.ImpactOnLanding = true;
        s.From = new Vector3(from.x, from.y, 0f);
        s.To = new Vector3(to.x, to.y, 0f);
        s.LastPuff = s.From;
        s.Go.transform.SetPositionAndRotation(s.From, Quaternion.identity);
        Slot shadow = Take(ShellShadows);
        s.Partner = shadow;
        if (shadow == null) return;
        shadow.Sprite.sortingOrder = WAR2D.Client.Render.SortingOrders.Decals + 2; // on the ground, under the units
        shadow.Go.transform.SetPositionAndRotation(s.From, Quaternion.identity);
    }

    /// <summary>Where a flame ends: on the target, or as far toward it as its range reaches (a big building's centre can be further).</summary>
    public static Vector2 FlameEnd(Vector2 unit, Vector2 target, float range)
    {
        Vector2 d = target - unit;
        float length = d.magnitude;
        return length <= range ? target : unit + d / length * range;
    }

    /// <summary>
    /// A unit's flame, burning for <paramref name="seconds"/> more: its stream from the nozzle to <paramref name="end"/>, with fire spread
    /// over <paramref name="splash"/> tiles round the end. A unit already burning keeps its flame, so its shots run together into one
    /// stream. <see cref="FollowFlames"/> keeps a flame on its unit and target while it burns.
    /// </summary>
    public static void Flame(int attacker, int target, Vector3 nozzle, Vector3 end, float splash, float seconds)
    {
        WAR2D.Audio.AudioService.Sfx(ShotSound(UnitType.Flamethrower), nozzle);
        Slot stream = null;
        foreach (Slot s in Streams.Slots) if (s.Active && s.Attacker == attacker) { stream = s; break; }
        if (stream == null)
        {
            stream = Take(Streams);
            if (stream == null) return;
            stream.Partner = Take(FlameFires);
            stream.Attacker = attacker;
        }
        stream.Target = target;
        stream.Until = Time.time + seconds;
        stream.Partner.Until = stream.Until;
        stream.Partner.Go.transform.localScale = Vector3.one * (splash / EffectGen.FlameSplashTiles);
        PlaceFlame(stream, nozzle, end);
    }

    private static void PlaceFlame(Slot stream, Vector3 nozzle, Vector3 end)
    {
        Vector3 reach = end - nozzle;
        reach.z = 0f;
        stream.Go.transform.SetPositionAndRotation(new Vector3(nozzle.x, nozzle.y, 0f) + reach * 0.5f, Quaternion.Euler(0f, 0f, Mathf.Atan2(reach.y, reach.x) * Mathf.Rad2Deg));
        stream.Go.transform.localScale = new Vector3(reach.magnitude / StreamTiles, 1f, 1f);
        stream.Partner.Go.transform.position = new Vector3(end.x, end.y, 0f);
    }

    /// <summary>Gives a burning flame's nozzle and end now, or false when its unit or target is gone.</summary>
    public delegate bool FlameLine(int attacker, int target, out Vector3 nozzle, out Vector3 end);

    /// <summary>Keeps every burning flame on its unit and target; one whose unit or target has gone goes out.</summary>
    public static void FollowFlames(FlameLine line)
    {
        foreach (Slot s in Streams.Slots)
        {
            if (!s.Active) continue;
            if (!line(s.Attacker, s.Target, out Vector3 nozzle, out Vector3 end))
            {
                Runner.Stop(s);
                Runner.Stop(s.Partner);
                continue;
            }
            PlaceFlame(s, nozzle, end);
        }
    }

    /// <summary>
    /// Where a Gun turret's muzzle is, from its footprint's centre, for a head facing <paramref name="angle"/>
    /// (radians): <see cref="WAR2D.Art.Render.RenderGen.TurretMuzzleReach"/> along the facing, raised by the
    /// muzzle's height in the 3/4 view.
    /// </summary>
    public static Vector3 TurretMuzzle(Vector2 centre, float angle)
    {
        const float ppu = ArtStyle.PixelsPerUnit;
        float reach = WAR2D.Art.Render.RenderGen.TurretMuzzleReach / ppu;
        float rise = WAR2D.Art.Render.RenderGen.TurretMuzzleHeight * WAR2D.Art.Render.RenderGen.Lift / ppu;
        return new Vector3(centre.x + Mathf.Cos(angle) * reach, centre.y + Mathf.Sin(angle) * reach + rise, 0f);
    }

    /// <summary>
    /// A Gun turret's shot (v0.13): the <c>TurretMuzzle</c> flash at the muzzle of a head facing
    /// <paramref name="angle"/> (turned to it), <c>shot_turret</c>, and a tracer from the muzzle to
    /// <paramref name="to"/> when the client was told the target (null: flash and sound only).
    /// </summary>
    public static void TurretShot(Vector2 centre, float angle, Vector3? to)
    {
        Vector3 muzzle = TurretMuzzle(centre, angle);
        WAR2D.Audio.AudioService.Sfx("shot_turret", muzzle);
        Slot flash = Take(TurretFlashes);
        if (flash == null) return;
        TurretShots++;
        flash.Go.transform.SetPositionAndRotation(muzzle, Quaternion.Euler(0f, 0f, angle * Mathf.Rad2Deg));
        if (!to.HasValue) return;
        if (Shot(Tracers, muzzle, to.Value, TankShotTiles) != null) TurretTracers++;
    }

    /// <summary>A Jammer's pulse (v0.13), centred on its footprint (the art places the rings on its dish).</summary>
    public static void JamPulse(Vector2 centre)
    {
        Slot s = Take(Pulses);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(centre.x, centre.y, 0f), Quaternion.identity);
    }

    /// <summary>A puff of smoke rising from <paramref name="position"/> (a damaged building).</summary>
    public static void Smoke(Vector2 position)
    {
        Slot s = Take(Smokes);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        s.Drift = new Vector3(0f, SmokeRise, 0f);
    }

    /// <summary>A tongue of flame at <paramref name="position"/> (a badly damaged building).</summary>
    public static void Fire(Vector2 position)
    {
        Slot s = Take(Fires);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
    }

    /// <summary>A glint on the gem tile centred on <paramref name="position"/> (a Miner paying out). Silent: the caller rate-limits <c>gem_mined</c>.</summary>
    /// <param name="tint">The sparkle's colour (v0.17: a rich gem's); white keeps the art's own.</param>
    public static void GemSparkle(Vector2 position, Color? tint = null)
    {
        Slot s = Take(Sparkles);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
        s.Sprite.color = tint ?? Color.white; // a pooled sprite: always set, so a reused one never keeps an old tint
    }

    /// <summary>A bullet striking at <paramref name="position"/> (a tracer arriving). Silent: the shot already played its sound.</summary>
    public static void HitSpark(Vector2 position)
    {
        Slot s = Take(HitSparks);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
    }

    /// <summary>A puff of ground dust at <paramref name="position"/> (a unit just made there).</summary>
    public static void DustPuff(Vector2 position)
    {
        Slot s = Take(Dust);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
    }

    /// <summary>
    /// A building of <paramref name="tiles"/> × <paramref name="tiles"/> finished, centred on <paramref name="centre"/>: a ring
    /// of dust puffs kicked up around its footprint's edge (the brightness pulse is the building renderer's). Silent: the
    /// <c>construction_finished</c> alert plays its sound.
    /// </summary>
    public static void BuildingFinished(Vector2 centre, int tiles)
    {
        int puffs = Mathf.Max(FinishPuffsMin, tiles * FinishPuffsPerTile);
        float half = tiles * 0.5f;
        for (int k = 0; k < puffs; k++)
        {
            // Evenly round the footprint, pushed out to its square edge so a big building's corners get dust too.
            float a = (k + 0.5f) * Mathf.PI * 2f / puffs;
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float edge = half / Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.y));
            DustPuff(centre + dir * edge);
        }
    }

    /// <summary>
    /// A finished building of <paramref name="tiles"/> × <paramref name="tiles"/> destroyed, centred on <paramref name="centre"/>:
    /// a blast sized to it (a 1-tile building's is a unit's), then for 2 tiles and up a ripple of smaller explosions and flying
    /// debris across the footprint over half a second, with <c>explosion_large</c> and <c>building_destroyed</c>.
    /// </summary>
    public static void BuildingCollapse(Vector2 centre, int tiles)
    {
        if (tiles <= 1)
        {
            Explosion(centre);
            return;
        }
        Slot s = Take(tiles >= 4 ? Collapses : Blasts);
        // The blast art is 3 or 4 tiles (never scaled: the pixels stay crisp); the ripple below covers a larger footprint.
        if (s != null) s.Go.transform.SetPositionAndRotation(new Vector3(centre.x, centre.y, 0f), Quaternion.identity);
        WAR2D.Audio.AudioService.Sfx("explosion_large", centre);
        WAR2D.Audio.AudioService.Sfx("building_destroyed", centre);
        int follow = tiles * tiles / 2;
        float now = Time.time, half = tiles * 0.4f;
        for (int k = 0; k < follow && delayed.Count < MaxDelayed; k++)
        {
            // Spread over the footprint without randomness: a golden-angle spiral from the centre outward.
            float a = k * 2.39996f, r = half * Mathf.Sqrt((k + 0.5f) / follow);
            delayed.Add(new Delayed
            {
                At = now + 0.08f + 0.5f * k / follow,
                Position = centre + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r),
                Debris = k % 2 == 1,
            });
        }
        EnsureRunner();
    }

    /// <summary>Delayed blasts still waiting (tests).</summary>
    public static int PendingBlasts => delayed.Count;

    /// <summary>Smoke puffs playing now (tests).</summary>
    public static int ActiveSmoke => Count(Smokes);

    /// <summary>Fires playing now (tests).</summary>
    public static int ActiveFires => Count(Fires);

    /// <summary>Gem glints playing now (tests).</summary>
    public static int ActiveSparkles => Count(Sparkles);

    /// <summary>Hit sparks playing now (tests).</summary>
    public static int ActiveHitSparks => Count(HitSparks);

    /// <summary>Dust puffs playing now (tests).</summary>
    public static int ActiveDust => Count(Dust);

    /// <summary>Turret muzzle flashes playing now (tests).</summary>
    public static int ActiveTurretFlashes => Count(TurretFlashes);

    /// <summary>Turret shots drawn (flash and sound) and those with a tracer, since the pools were made (tests).</summary>
    public static int TurretShots { get; private set; }

    /// <summary>Turret shots drawn with a tracer to their target (tests).</summary>
    public static int TurretTracers { get; private set; }

    /// <summary>Jammer pulses playing now (tests).</summary>
    public static int ActivePulses => Count(Pulses);

    /// <summary>Tracers (the plain kind units and turrets fire) flying now (tests).</summary>
    public static int ActiveTracers => Count(Tracers);

    /// <summary>Flashes of the Tank's size playing now (tests).</summary>
    public static int ActiveTankBursts => Count(BurstsTank);

    /// <summary>Shells in the air, and flames burning, now (tests).</summary>
    public static int ActiveShells => Count(Shells);
    public static int ActiveFlames => Count(Streams);

    /// <summary>Destroys every pooled sprite (tests, and nothing else needs it: pools follow their runner).</summary>
    public static void Clear()
    {
        if (runner != null)
        {
            if (Application.isPlaying) Object.Destroy(runner.gameObject);
            else Object.DestroyImmediate(runner.gameObject);
        }
        Reset();
    }

    private static void Reset()
    {
        runner = null;
        Created = 0;
        TurretShots = 0;
        TurretTracers = 0;
        delayed.Clear();
        foreach (Pool p in new[] { Explosions, Flashes, Tracers, Blasts, Debris, Sparks, SniperTracers, Shells, Impacts, Streams, BomberBlasts, TurretFlashes, Pulses, Smokes, Fires, Sparkles, Collapses, HitSparks, Dust, FlameFires, BurstsScout, BurstsSniper, BurstsTank, BurstsHeavy, ScoutTracers, ShellShadows, ShellPuffs })
        {
            p.Slots.Clear();
            p.Next = 0;
        }
    }

    private static int Count(Pool p)
    {
        int n = 0;
        foreach (Slot s in p.Slots) if (s.Active) n++;
        return n;
    }

    /// <summary>The next slot of a pool: a new one until it is full, then the oldest (restarted).</summary>
    private static void EnsureRunner()
    {
        if (runner != null) return;
        Reset();
        var go = new GameObject("Effects");
        runner = go.AddComponent<Runner>();
    }

    private static Slot Take(Pool pool)
    {
        EnsureRunner();
        if (atlas == null || atlas.Texture == null) atlas = ArtAtlas.Load(ArtAtlas.Effects);
        if (atlas == null) return null;

        Slot s;
        if (pool.Slots.Count < pool.Size)
        {
            var go = new GameObject(pool.Id);
            go.transform.SetParent(runner.transform, false);
            s = new Slot { Go = go, Sprite = go.AddComponent<SpriteRenderer>() };
            s.Sprite.sortingOrder = WAR2D.Client.Render.SortingOrders.Effects;
            pool.Slots.Add(s);
            Created++;
        }
        else
        {
            s = pool.Slots[pool.Next];
            pool.Next = (pool.Next + 1) % pool.Size;
        }
        s.Active = true;
        s.Start = Time.time;
        s.Go.transform.localScale = Vector3.one;
        s.ImpactOnLanding = false;
        s.SparkOnLanding = false;
        s.Drift = Vector3.zero;
        s.Seconds = TracerSeconds;
        s.Until = 0f;
        s.Attacker = 0;
        s.Sprite.sprite = atlas.Sprite(pool.Id, 0);
        s.Go.SetActive(true);
        return s;
    }

    /// <summary>Advances every playing effect; hides the finished ones.</summary>
    private sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            if (atlas == null) return;
            float now = Time.time;
            Animate(Explosions, now, false);
            Animate(Flashes, now, false);
            Animate(Tracers, now, true);
            Animate(SniperTracers, now, true);
            Animate(Shells, now, true);
            Animate(Impacts, now, false);
            Animate(Streams, now, false);
            Animate(FlameFires, now, false);
            Animate(BurstsScout, now, false);
            Animate(BurstsSniper, now, false);
            Animate(BurstsTank, now, false);
            Animate(BurstsHeavy, now, false);
            Animate(ScoutTracers, now, true);
            Animate(ShellPuffs, now, false);
            Animate(Blasts, now, false);
            Animate(BomberBlasts, now, false);
            Animate(Debris, now, false);
            Animate(Sparks, now, false);
            Animate(TurretFlashes, now, false);
            Animate(Pulses, now, false);
            Animate(Smokes, now, false);
            Animate(Fires, now, false);
            Animate(Sparkles, now, false);
            Animate(Collapses, now, false);
            Animate(HitSparks, now, false);
            Animate(Dust, now, false);
            Release(now);
        }

        /// <summary>Sets off the delayed blasts whose time has come.</summary>
        private static void Release(float now)
        {
            for (int i = delayed.Count - 1; i >= 0; i--)
            {
                Delayed d = delayed[i];
                if (d.At > now) continue;
                delayed.RemoveAt(i);
                if (d.Debris) DigDebris(d.Position);
                else Explosion(d.Position);
            }
        }

        private static void Animate(Pool pool, float now, bool tracer)
        {
            int frames = atlas.FrameCount(pool.Id);
            foreach (Slot s in pool.Slots)
            {
                if (!s.Active) continue;
                float age = now - s.Start;
                if (tracer)
                {
                    float t = age / s.Seconds;
                    if (t >= 1f)
                    {
                        if (s.ImpactOnLanding)
                        {
                            ShellImpact(s.To);
                            Decals.Crater(s.To);
                            if (s.Partner != null) Stop(s.Partner);
                        }
                        else if (s.SparkOnLanding) HitSpark(s.To);
                        Stop(s);
                        continue;
                    }
                    if (s.ImpactOnLanding) Fly(s, t, age);
                    else
                    {
                        s.Go.transform.position = ShotCentre(s.From, s.To, t, s.Length, out float drawn);
                        s.Go.transform.localScale = new Vector3(drawn / s.ArtLength, 1f, 1f);
                    }
                    continue;
                }
                int frame = (int)(age * ArtStyle.FramesPerSecond);
                if (s.Until > 0f)
                {
                    if (now >= s.Until) { Stop(s); continue; }
                    frame %= frames;
                }
                else if (frame >= frames) { Stop(s); continue; }
                s.Sprite.sprite = atlas.Sprite(pool.Id, frame);
                if (s.Drift != Vector3.zero) s.Go.transform.position += s.Drift * Time.deltaTime;
            }
        }

        /// <summary>A shell in flight: over its arc with its nose along its path, its shadow below it on the ground, leaving puffs behind it.</summary>
        private static void Fly(Slot s, float t, float age)
        {
            Vector3 at = ShellPoint(s.From, s.To, t), ahead = ShellPoint(s.From, s.To, t + 0.02f) - at;
            s.Go.transform.position = at;
            int frames = atlas.Frames(Shells.Id), facing = WAR2D.Client.Render.TurretAim.Facing(Mathf.Atan2(ahead.y, ahead.x));
            s.Sprite.sprite = atlas.Sprite(Shells.Id, facing * frames + (int)(age * ArtStyle.FramesPerSecond) % frames);
            if (s.Partner != null) s.Partner.Go.transform.position = Vector3.Lerp(s.From, s.To, t);
            if ((at - s.LastPuff).sqrMagnitude < ShellPuffSpacing * ShellPuffSpacing) return;
            s.LastPuff = at;
            Slot puff = Take(ShellPuffs);
            if (puff != null) puff.Go.transform.SetPositionAndRotation(at, Quaternion.identity);
        }

        public static void Stop(Slot s)
        {
            s.Active = false;
            s.Go.SetActive(false);
        }

        private void OnDestroy()
        {
            if (runner == this) Reset();
        }
    }
}
