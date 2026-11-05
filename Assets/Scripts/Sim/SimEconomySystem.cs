using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>
    /// Unit upkeep and decay. Once a second each owner's units, in slot order, are charged their
    /// running cost from the owner's budget until it runs out; the rest are marked unpaid. Each unpaid unit
    /// costs its owner <c>DecayPercentPerSecond</c> of a unit per second until a later charge pays, taken from
    /// one unit at a time (<see cref="UpkeepJob"/>). Buildings give way first (<see cref="ServerPlayer.TryPayBuildingUpkeep"/>). The boundary
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
                Owed = data.UpkeepOwed,
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
        public NativeArray<float> Owed;
        public NativeArray<int> UnitsBySlot;
        public NativeArray<byte> Charged;
        public float DecayPerTick;

        public void Execute()
        {
            var unpaid = new NativeArray<int>(UnitsBySlot.Length, Allocator.Temp);
            var weakest = new NativeArray<int>(UnitsBySlot.Length, Allocator.Temp);
            for (int s = 0; s < UnitsBySlot.Length; s++) { UnitsBySlot[s] = 0; weakest[s] = -1; }
            for (int i = 0; i < Count; i++)
            {
                if (Health[i] <= 0f) continue;
                int owner = OwnerSlot[i];
                if (owner >= UnitsBySlot.Length) continue;
                UnitsBySlot[owner]++;
                if (Charge)
                {
                    float cost = Type[i] < RunningCostByType.Length ? RunningCostByType[Type[i]] : 0f;
                    Owed[owner] += cost;
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
                if (Unpaid[i] == 0) continue;
                unpaid[owner]++;
                int w = weakest[owner];
                if (w < 0 || Health[i] / MaxHealth[i] <= Health[w] / MaxHealth[w]) weakest[owner] = i;
            }

            // A whole unit at a time (playtest 2026-11-04): what every unpaid unit would have lost goes to the weakest of
            // them until it dies, then to the next, so a short balance costs a few units and not the whole army's health.
            var pool = new NativeArray<float>(UnitsBySlot.Length, Allocator.Temp);
            bool spills = false;
            for (int s = 0; s < pool.Length; s++)
            {
                if (unpaid[s] == 0) continue;
                pool[s] = unpaid[s] * DecayPerTick;
                Drain(weakest[s], s, pool);
                spills |= pool[s] > 0f;
            }
            if (spills)
                for (int i = Count - 1; i >= 0; i--)
                {
                    int owner = OwnerSlot[i];
                    if (owner < pool.Length && pool[owner] > 0f && Unpaid[i] != 0 && Health[i] > 0f) Drain(i, owner, pool);
                }
            Charged[0] = Charge ? (byte)1 : (byte)0;
        }

        /// <summary>Takes one unit's share of its owner's decay: up to its remaining health, in shares of its max health.</summary>
        private void Drain(int unit, int owner, NativeArray<float> pool)
        {
            float left = Health[unit] / MaxHealth[unit];
            float taken = math.min(pool[owner], left);
            Health[unit] = taken >= left ? 0f : Health[unit] - taken * MaxHealth[unit];
            pool[owner] -= taken;
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
