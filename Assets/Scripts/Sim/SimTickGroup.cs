using Config;
using Mirror;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Sim
{
    /// <summary>
    /// The server's asynchronous fixed-rate tick (20 Hz by default). Its stages schedule Burst jobs on
    /// <c>state.Dependency</c> and never complete them: the jobs run on worker threads between ticks and
    /// <see cref="SimBoundarySystem"/> settles them at the start of the next tick.
    /// Order: boundary → commands → gather → hash → combat → movement → vision → economy → lifecycle → end.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class SimTickGroup : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            GameConfigData config = ConfigLoader.LoadConfig();
            float seconds = config.Simulation.TickRate > 0 ? config.Simulation.TickSeconds : 0.05f;
            RateManager = new RateUtils.FixedRateCatchUpManager(seconds);
        }
    }

    /// <summary>
    /// Settles the previous tick: completes its jobs, removes the units it found dead (freeing their ids
    /// and recording their explosions), applies the upkeep it charged, and decides whether this tick runs.
    /// </summary>
    [UpdateInGroup(typeof(SimTickGroup), OrderFirst = true)]
    public partial class SimBoundarySystem : SystemBase
    {
        internal static readonly Stopwatch TickWatch = new Stopwatch();
        private readonly Stopwatch waitWatch = new Stopwatch();

        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
        }

        protected override void OnUpdate()
        {
            TickWatch.Restart();
            waitWatch.Restart();
            EntityManager.CompleteAllTrackedJobs();
            waitWatch.Stop();
            SimTiming.LastBoundaryWaitMs = waitWatch.Elapsed.TotalMilliseconds;
            SimTiming.LastBoundaryFrame = UnityEngine.Time.frameCount;

            SimData data = SystemAPI.GetSingleton<SimData>();
            SimContext context = SimContext.Current;

            RemoveDead(data, context);
            SettleUpkeep(data, context);

            RefRW<SimClock> clock = SystemAPI.GetSingletonRW<SimClock>();
            context?.Orders.AtBoundary(context.Map, clock.ValueRO.Tick);
            if (clock.ValueRO.Running) context?.RaiseSettled(data, clock.ValueRO.Tick, clock.ValueRO.UnitCount);
            data.AttackEvents.Clear();
            data.FogChanges.Clear();
            bool running = SimContext.RunningOverride ?? (NetworkServer.active && GameCore.Instance != null && RunsIn(GameCore.Instance.CurrentState));
            clock.ValueRW.Running = running;
            if (running) clock.ValueRW.Tick++;
        }

        /// <summary>
        /// The game states the tick runs in: Playing, and GameOver, so the match-end wipe (applied after the
        /// state changes) is still settled once more: its deaths reach clients as explosions and Leaves.
        /// Nothing new can start in GameOver: orders, spawners and building systems need Playing.
        /// </summary>
        public static bool RunsIn(GameState state) => state == GameState.Playing || state == GameState.GameOver;

        /// <summary>Takes last tick's upkeep from the owners, then refreshes every owner's budget.</summary>
        private static void SettleUpkeep(SimData data, SimContext context)
        {
            bool charged = data.ChargedThisTick[0] != 0;
            data.ChargedThisTick[0] = 0;
            for (int s = 0; s < SimData.MaxOwners; s++)
            {
                int owner = data.OwnerIdBySlot[s];
                float spent = data.UpkeepSpent[s];
                data.UpkeepSpent[s] = 0f;
                if (owner == 0) continue;
                if (charged && spent > 0f) context?.Spend?.Invoke(owner, spent);
                data.UpkeepBudget[s] = context?.BudgetOf != null ? context.BudgetOf(owner) : float.MaxValue;
            }
        }

        private void RemoveDead(SimData data, SimContext context)
        {
            if (data.Deaths.Count == 0) return;
            var entities = new NativeList<Entity>(data.Deaths.Count, Allocator.Temp);
            while (data.Deaths.TryDequeue(out DeathRecord death))
            {
                entities.Add(death.Entity);
                if (context == null) continue;
                context.Ids.Free(death.Id);
                context.RaiseUnitDied(death.Id, death.Position);
                context.RaiseUnitKilled(death.OwnerId, death.Killer);
            }
            EntityManager.DestroyEntity(entities.AsArray());
            entities.Dispose();
        }
    }

    /// <summary>Closes the tick's main-thread timing.</summary>
    [UpdateInGroup(typeof(SimTickGroup), OrderLast = true)]
    public partial class SimEndSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimData>();
        }

        protected override void OnUpdate()
        {
            SimContext context = SimContext.Current;
            if (context != null) context.Orders.ScheduleRebuilds(context.Config.Simulation.MaxFieldRebuildsPerTick);
            SimTiming.LastMainThreadMs = SimBoundarySystem.TickWatch.Elapsed.TotalMilliseconds;
        }
    }
}
