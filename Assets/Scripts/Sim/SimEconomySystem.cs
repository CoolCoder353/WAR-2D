using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Unit upkeep and decay. Once a second each owner's units, in slot order, are charged their
    /// running cost from the owner's budget until it runs out; the rest are marked unpaid. Unpaid units
    /// lose <c>DecayPercentPerSecond</c> of max health per second until a later charge pays. The boundary
    /// takes the spent totals from the owners (<see cref="SimContext.Spend"/>). Also counts live units per
    /// owner for the unit cap.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimTickGroup))]
    [UpdateAfter(typeof(SimMovementSystem))]
    public partial struct SimEconomySystem : ISystem
    {
        private EntityQuery units;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimData>();
            units = state.GetEntityQuery(ComponentType.ReadWrite<Unit>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SimClock clock = SystemAPI.GetSingleton<SimClock>();
            if (!clock.Running) return;
            SimData data = SystemAPI.GetSingleton<SimData>();
            int count = clock.UnitCount;

            state.Dependency = new UpkeepJob
            {
                Count = count,
                Charge = clock.Tick % math.max(1, data.TickRate) == 0,
                Health = data.Health,
                MaxHealth = data.MaxHealth,
                OwnerSlot = data.OwnerSlot,
                Type = data.Type,
                Unpaid = data.Unpaid,
                RunningCostByType = data.RunningCostByType,
                Budget = data.UpkeepBudget,
                Spent = data.UpkeepSpent,
                UnitsBySlot = data.UnitsBySlot,
                Charged = data.ChargedThisTick,
                DecayPerTick = data.DecayPercentPerSecond * 0.01f * data.Dt,
            }.Schedule(state.Dependency);

            state.Dependency = new WriteBackEconomyJob
            {
                Health = data.Health,
                Unpaid = data.Unpaid,
                Count = count,
            }.ScheduleParallel(units, state.Dependency);
        }
    }

    /// <summary>The per-second charge in slot order, the decay of unpaid units, and the unit count per owner.</summary>
    [BurstCompile]
    public struct UpkeepJob : IJob
    {
        public int Count;
        public bool Charge;
        public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> MaxHealth;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        [ReadOnly] public NativeArray<byte> Type;
        public NativeArray<byte> Unpaid;
        [ReadOnly] public NativeArray<float> RunningCostByType;
        public NativeArray<float> Budget;
        public NativeArray<float> Spent;
        public NativeArray<int> UnitsBySlot;
        public NativeArray<byte> Charged;
        public float DecayPerTick;

        public void Execute()
        {
            for (int s = 0; s < UnitsBySlot.Length; s++) UnitsBySlot[s] = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Health[i] <= 0f) continue;
                int owner = OwnerSlot[i];
                if (owner >= UnitsBySlot.Length) continue;
                UnitsBySlot[owner]++;
                if (Charge)
                {
                    float cost = Type[i] < RunningCostByType.Length ? RunningCostByType[Type[i]] : 0f;
                    if (Budget[owner] >= cost)
                    {
                        Budget[owner] -= cost;
                        Spent[owner] += cost;
                        Unpaid[i] = 0;
                    }
                    else
                    {
                        Unpaid[i] = 1;
                    }
                }
                if (Unpaid[i] != 0) Health[i] = math.max(0f, Health[i] - MaxHealth[i] * DecayPerTick);
            }
            Charged[0] = Charge ? (byte)1 : (byte)0;
        }
    }

    /// <summary>Writes health and the unpaid flag back to each unit.</summary>
    [BurstCompile]
    public partial struct WriteBackEconomyJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Unpaid;
        public int Count;

        private void Execute(ref Unit unit, [EntityIndexInQuery] int index)
        {
            if (index >= Count) return;
            unit.Health = Health[index];
            unit.Unpaid = Unpaid[index];
        }
    }
}
