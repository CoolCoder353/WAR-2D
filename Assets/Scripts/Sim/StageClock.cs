using Unity.Entities;
using Unity.Jobs;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Sim
{
    /// <summary>
    /// Perf harness only (<c>-perf</c>): the wall time of the combat and vision stages' job chains on the worker
    /// threads (v0.13, so the Gun turret's jobs and the jam rebuild show up in <c>perf.csv</c> as
    /// <c>perf.part.combat</c> and <c>perf.part.vision</c>). A tiny managed job stamps the clock in the chain just
    /// before a stage's first job and after its last one; the boundary that settles the tick reads the difference
    /// (<see cref="SimTiming.LastCombatJobsMs"/>, <see cref="SimTiming.LastVisionJobsMs"/>). The vision stamps run only
    /// on ticks the vision stage runs, so a vision sample is never a skipped tick's 0. Off without the flag.
    /// </summary>
    public static class StageClock
    {
        public const int CombatStart = 0, CombatEnd = 1, VisionStart = 2, VisionEnd = 3;

        /// <summary>Stopwatch timestamps, written by <see cref="StampJob"/> on a worker; 0 when the stamp didn't run this tick.</summary>
        internal static readonly long[] Stamps = new long[4];

        /// <summary>True while the harness measures (the <c>-perf</c> flag).</summary>
        public static bool Enabled => DevApi.PerfFlag;

        /// <summary>Whether both stamps ran in the last settled tick (read before <see cref="Take"/> clears them).</summary>
        internal static bool Ran(int start, int end) => Stamps[start] != 0 && Stamps[end] >= Stamps[start];

        /// <summary>Milliseconds between two stamps of the last settled tick (0 when either didn't run), and clears them.</summary>
        internal static double Take(int start, int end)
        {
            long a = Stamps[start], b = Stamps[end];
            Stamps[start] = 0;
            Stamps[end] = 0;
            return a == 0 || b < a ? 0.0 : (b - a) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Writes the clock into one slot when it runs (not Burst: it reads the managed stopwatch).</summary>
        internal struct StampJob : IJob
        {
            public int Slot;

            public void Execute() => Stamps[Slot] = Stopwatch.GetTimestamp();
        }

        /// <summary>Schedules a stamp in the <see cref="Unit"/> chain of the calling stage marker.</summary>
        internal static void Stamp(ref JobHandle dependency, int slot)
        {
            if (!Enabled) return;
            dependency = new StampJob { Slot = slot }.Schedule(dependency);
        }
    }

    /// <summary>Stamps the start of the combat stage's job chain (perf harness only).</summary>
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimBombSystem))]
    [UpdateBefore(typeof(SimCombatSystem))]
    public partial class SimCombatStartStamp : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
            GetEntityQuery(ComponentType.ReadWrite<Unit>()); // in the units' chain, like a tick stage
        }

        protected override void OnUpdate()
        {
            if (!SystemAPI.GetSingleton<SimClock>().Running) return;
            JobHandle dependency = Dependency;
            StageClock.Stamp(ref dependency, StageClock.CombatStart);
            Dependency = dependency;
        }
    }

    /// <summary>Stamps the end of the combat stage's job chain (perf harness only).</summary>
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimCombatSystem))]
    [UpdateBefore(typeof(SimMovementSystem))]
    public partial class SimCombatEndStamp : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
            GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        protected override void OnUpdate()
        {
            if (!SystemAPI.GetSingleton<SimClock>().Running) return;
            JobHandle dependency = Dependency;
            StageClock.Stamp(ref dependency, StageClock.CombatEnd);
            Dependency = dependency;
        }
    }

    /// <summary>Stamps the start of the vision stage's job chain (perf harness only).</summary>
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimDigSystem))]
    [UpdateAfter(typeof(SimConstructionSystem))]
    [UpdateBefore(typeof(SimVisionSystem))]
    public partial class SimVisionStartStamp : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
            GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        protected override void OnUpdate()
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running || !SimVisionSystem.RunsOn(clock.Tick, SystemAPI.GetSingleton<SimData>().VisionInterval)) return;
            JobHandle dependency = Dependency;
            StageClock.Stamp(ref dependency, StageClock.VisionStart);
            Dependency = dependency;
        }
    }

    /// <summary>Stamps the end of the vision stage's job chain (perf harness only).</summary>
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimVisionSystem))]
    [UpdateBefore(typeof(SimEconomySystem))]
    public partial class SimVisionEndStamp : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
            GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        protected override void OnUpdate()
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running || !SimVisionSystem.RunsOn(clock.Tick, SystemAPI.GetSingleton<SimData>().VisionInterval)) return;
            JobHandle dependency = Dependency;
            StageClock.Stamp(ref dependency, StageClock.VisionEnd);
            Dependency = dependency;
        }
    }
}
