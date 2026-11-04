using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    /// <summary>A shot at an owner's unit or building: where the victim stood and where the shot came from.</summary>
    public struct DefendAlarm
    {
        public float2 Victim, From;
    }

    /// <summary>
    /// Idle units answer fire (playtest 2026-11-04). A shot at an owner's unit or building raises an alarm; at most
    /// <see cref="PerOwner"/> are held per owner between answers, one per <c>Defend/AlarmCellTiles</c> cell of the shooter's position.
    /// </summary>
    public static class DefendAlarms
    {
        /// <summary>Most alarms held per owner between answers.</summary>
        public const int PerOwner = 8;

        /// <summary>Adds an alarm unless the owner already has one from that cell, or has its fill.</summary>
        public static void Raise(NativeArray<DefendAlarm> alarms, NativeArray<int> count, float cellTiles, int owner, float2 victim, float2 from)
        {
            if ((uint)owner >= (uint)count.Length) return;
            int n = count[owner];
            if (n >= PerOwner) return;
            int2 cell = (int2)math.floor(from / cellTiles);
            for (int k = 0; k < n; k++)
                if (((int2)math.floor(alarms[owner * PerOwner + k].From / cellTiles)).Equals(cell)) return;
            alarms[owner * PerOwner + n] = new DefendAlarm { Victim = victim, From = from };
            count[owner] = n + 1;
        }

        /// <summary>
        /// Where an alarm sends its responders: the shooter's position if the owner's side sees it now; else the whole step towards it,
        /// however near it is, because a route reaches its owner's client and must not name a position in the fog. It gives away only
        /// the direction fire came from. The step may pass the shooter: attack-move stops for it as soon as it is seen.
        /// </summary>
        public static int2 Goal(in DefendAlarm alarm, bool seen, float stepTiles)
        {
            if (seen) return (int2)math.floor(alarm.From);
            return (int2)math.floor(alarm.Victim + math.normalizesafe(alarm.From - alarm.Victim) * stepTiles);
        }
    }

    /// <summary>
    /// Finds who answers the alarms: armed, standing idle with no target, no work and no order, within <see cref="RadiusSq"/> of a
    /// victim of its own side's alarm. One pass over the units, however many alarms. Each result is <c>alarm index &lt;&lt; 32 | slot</c>.
    /// </summary>
    [BurstCompile]
    public struct FindRespondersJob : IJob
    {
        public int Count;
        public float RadiusSq;
        [ReadOnly] public NativeArray<DefendAlarm> Alarms;
        [ReadOnly] public NativeArray<int> AlarmCount;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health, DamageByType;
        [ReadOnly] public NativeArray<byte> OwnerSlot, Type, Stance, WorkKind;
        [ReadOnly] public NativeArray<int> Target, OrderSlot;
        [ReadOnly] public NativeArray<byte> OrderLive;
        public NativeList<long> Results;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Stance[i] != Stances.Idle || Target[i] >= 0 || WorkKind[i] != WorkKinds.None || Health[i] <= 0f || DamageByType[Type[i]] <= 0f) continue;
                if ((uint)OrderSlot[i] < (uint)OrderLive.Length && OrderLive[OrderSlot[i]] != 0) continue;
                int owner = OwnerSlot[i];
                if (owner >= AlarmCount.Length) continue;
                int n = AlarmCount[owner];
                for (int k = 0; k < n; k++)
                {
                    int alarm = owner * DefendAlarms.PerOwner + k;
                    if (math.distancesq(Positions[i], Alarms[alarm].Victim) > RadiusSq) continue;
                    Results.Add(((long)alarm << 32) | (uint)i);
                    break;
                }
            }
        }
    }
}
