using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>A shell in flight (<see cref="SimData.Shells"/>): all its landing needs, so it outlives the unit that fired it.</summary>
    public struct Shell
    {
        /// <summary>Where the gun stood when it fired (a victim's alarm points here), and where the shell comes down.</summary>
        public float2 From, Landing;
        /// <summary>The tick it was fired on, and the tick its damage lands on.</summary>
        public int FiredTick, LandTick;
        /// <summary>The gun's id (replication names it when it fires) and owner (who gets the kill).</summary>
        public int AttackerId, AttackerOwner;
        /// <summary>The gun's damage before the falloff and the damage table, and the blast radius on buildings and walls.</summary>
        public float Damage, Radius;
        public byte AttackerType, AttackerSlot;
    }

    /// <summary>A shell that came down this tick, hit or miss (<see cref="SimData.ShellLandings"/>): the crater and the explosion.</summary>
    public struct ShellLanding
    {
        public float2 Point;
        public int OwnerId;
    }

    /// <summary>Where a shell is aimed and how long it flies.</summary>
    public static class ShellAim
    {
        /// <summary>
        /// Seconds until a shell meets a target driving straight: the sooner root of |d + v t| = s t. Never more than twice the flight to
        /// where the target is now, as a target about as fast as the shell runs that root off to infinity.
        /// </summary>
        public static float Seconds(float2 from, float2 target, float2 velocity, float speed)
        {
            float2 d = target - from;
            float plain = math.length(d) / speed;
            float a = speed * speed - math.lengthsq(velocity), b = math.dot(d, velocity);
            return a <= 0f ? 2f * plain : math.min((b + math.sqrt(b * b + a * math.lengthsq(d))) / a, 2f * plain);
        }

        /// <summary>Ticks a flight of <paramref name="seconds"/> takes: at least one, rounded up so a shell is never early.</summary>
        public static int Ticks(float seconds, float dt) => math.max(1, (int)math.ceil(seconds / dt - 1e-3f));
    }

    /// <summary>
    /// Brings down the shells due this tick: each becomes a splash request for <see cref="SplashJob"/> (which sizes its blast by what it
    /// hits) and a landing. The rest keep their order, so what the attack job fires next is the list's tail (<see cref="SimData.ShellsFired"/>).
    /// </summary>
    [BurstCompile]
    public struct ShellLandingJob : IJob
    {
        public NativeList<Shell> Shells;
        public NativeList<SplashRequest> Splashes;
        public NativeList<ShellLanding> Landings;
        public int Tick;

        public void Execute()
        {
            int kept = 0;
            for (int i = 0; i < Shells.Length; i++)
            {
                Shell s = Shells[i];
                if (s.LandTick > Tick)
                {
                    Shells[kept++] = s;
                    continue;
                }
                Splashes.AddNoResize(new SplashRequest
                {
                    Centre = s.Landing, Radius = s.Radius, Damage = s.Damage, AttackerType = s.AttackerType, AttackerSlot = s.AttackerSlot,
                    AttackerOwner = s.AttackerOwner, PrimarySlot = -1, From = s.From, ByClass = 1,
                });
                Landings.AddNoResize(new ShellLanding { Point = s.Landing, OwnerId = s.AttackerOwner });
            }
            Shells.ResizeUninitialized(kept);
        }
    }
}
