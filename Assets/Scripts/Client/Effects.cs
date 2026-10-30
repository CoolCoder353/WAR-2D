using System.Collections.Generic;
using UnityEngine;
using WAR2D.Art;

/// <summary>
/// Client-only effects from the <c>Effects</c> atlas: explosions, bomb blasts, muzzle flashes, tracers, dig
/// debris, repair sparks, building collapses, smoke and fire on damaged buildings, and gem glints. Each kind has a
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

    /// <summary>Tiles per second a smoke puff rises while it plays.</summary>
    public const float SmokeRise = 0.7f;

    /// <summary>Most delayed blasts waiting at once (a collapse's follow-up explosions); more are dropped.</summary>
    public const int MaxDelayed = 128;

    /// <summary>Seconds a tracer takes to reach its target.</summary>
    public const float TracerSeconds = 0.2f;

    /// <summary>Seconds an artillery shell takes to land.</summary>
    public const float ShellSeconds = 0.5f;

    /// <summary>Tiles a flame stream sprite is wide; it is stretched to reach the target.</summary>
    public const float StreamTiles = 3f;


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
        /// <summary>World units per second the effect drifts while it plays (rising smoke).</summary>
        public Vector3 Drift;
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
    private static readonly Pool BomberBlasts = new Pool("BomberBlast", BomberBlastPool);
    private static readonly Pool TurretFlashes = new Pool("TurretMuzzle", TurretFlashPool);
    private static readonly Pool Pulses = new Pool("JamPulse", PulsePool);
    private static readonly Pool Smokes = new Pool("Smoke", SmokePool);
    private static readonly Pool Fires = new Pool("Fire", FirePool);
    private static readonly Pool Sparkles = new Pool("GemSparkle", SparklePool);
    private static readonly Pool Collapses = new Pool("BomberBlast", CollapsePool);

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
    /// A tracer from the shooter to the target (with a muzzle flash at the shooter's barrel). Without <paramref name="line"/>
    /// (the Tracers setting thinning them out) only the flash and sound play; Artillery shells and flame streams always draw.
    /// </summary>
    public static void Tracer(Vector3 from, Vector3 to, UnitType attacker = UnitType.Tank, bool line = true)
    {
        Vector3 heading = to - from;
        heading.z = 0f;
        Vector3 muzzle = from + (heading.sqrMagnitude > 1e-6f ? heading.normalized * BarrelReach : Vector3.zero);
        if (attacker == UnitType.Flamethrower)
        {
            // A stream stretched from the nozzle to the target; no flash.
            WAR2D.Audio.AudioService.Sfx(ShotSound(attacker), muzzle);
            Slot stream = Take(Streams);
            if (stream == null) return;
            Vector3 reach = to - muzzle;
            reach.z = 0f;
            stream.Go.transform.SetPositionAndRotation(new Vector3(muzzle.x, muzzle.y, 0f) + reach * 0.5f, Quaternion.Euler(0f, 0f, Mathf.Atan2(reach.y, reach.x) * Mathf.Rad2Deg));
            stream.Go.transform.localScale = new Vector3(Mathf.Max(0.1f, reach.magnitude / StreamTiles), 1f, 1f);
            return;
        }
        MuzzleFlash(muzzle, ShotSound(attacker));
        if (!line && attacker != UnitType.Artillery) return;
        Pool pool = attacker == UnitType.Sniper ? SniperTracers : attacker == UnitType.Artillery ? Shells : Tracers;
        Slot s = Take(pool);
        if (s == null) return;
        s.Seconds = attacker == UnitType.Artillery ? ShellSeconds : TracerSeconds;
        s.ImpactOnLanding = attacker == UnitType.Artillery;
        s.From = new Vector3(from.x, from.y, 0f);
        s.To = new Vector3(to.x, to.y, 0f);
        Vector3 d = s.To - s.From;
        s.Go.transform.SetPositionAndRotation(s.From, Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg));
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
        Slot s = Take(Tracers);
        if (s == null) return;
        TurretTracers++;
        s.From = muzzle;
        s.To = new Vector3(to.Value.x, to.Value.y, 0f);
        Vector3 d = s.To - s.From;
        s.Go.transform.SetPositionAndRotation(s.From, Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg));
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
    public static void GemSparkle(Vector2 position)
    {
        Slot s = Take(Sparkles);
        if (s == null) return;
        s.Go.transform.SetPositionAndRotation(new Vector3(position.x, position.y, 0f), Quaternion.identity);
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
        foreach (Pool p in new[] { Explosions, Flashes, Tracers, Blasts, Debris, Sparks, SniperTracers, Shells, Impacts, Streams, BomberBlasts, TurretFlashes, Pulses, Smokes, Fires, Sparkles, Collapses })
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
        s.Drift = Vector3.zero;
        s.Seconds = TracerSeconds;
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
                        if (s.ImpactOnLanding) ShellImpact(s.To);
                        Stop(s);
                        continue;
                    }
                    s.Go.transform.position = Vector3.Lerp(s.From, s.To, t);
                    continue;
                }
                int frame = (int)(age * ArtStyle.FramesPerSecond);
                if (frame >= frames) { Stop(s); continue; }
                s.Sprite.sprite = atlas.Sprite(pool.Id, frame);
                if (s.Drift != Vector3.zero) s.Go.transform.position += s.Drift * Time.deltaTime;
            }
        }

        private static void Stop(Slot s)
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
