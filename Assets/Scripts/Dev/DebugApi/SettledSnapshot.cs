using Unity.Mathematics;
using WAR2D.Sim;

namespace WAR2D.Dev.DebugApi
{
    /// <summary>
    /// A managed copy of the settled unit SoA, taken in <see cref="SimContext.Settled"/> (main thread, jobs complete),
    /// so API commands and overlays can read units between ticks without touching <see cref="SimData"/>.
    /// Captured only while someone wants it.
    /// </summary>
    public sealed class SettledSnapshot
    {
        public static SettledSnapshot Current { get; private set; }

        /// <summary>Readers that need captures (the API while listening counts as one).</summary>
        public static int Wanted;

        public int Tick, Count;
        public int[] Id = new int[0], Owner = new int[0], Target = new int[0], OrderSlot = new int[0];
        public byte[] Type = new byte[0], Stance = new byte[0], TargetKind = new byte[0];
        public float2[] Position = new float2[0];
        public float[] Health = new float[0], MaxHealth = new float[0];

        public static void Attach(SimContext sim)
        {
            Current = new SettledSnapshot();
            sim.Settled += OnSettled;
        }

        public static void Detach(SimContext sim)
        {
            sim.Settled -= OnSettled;
            Current = null;
        }

        private static void OnSettled(SimData data, int tick, int count)
        {
            if (Current != null && (DebugApi.Listening || Wanted > 0)) Current.Capture(data, tick, count);
        }

        public void Capture(SimData data, int tick, int count)
        {
            Tick = tick;
            Count = count;
            if (Id.Length < count)
            {
                int n = math.max(count, Id.Length * 2);
                Id = new int[n]; Owner = new int[n]; Target = new int[n]; OrderSlot = new int[n];
                Type = new byte[n]; Stance = new byte[n]; TargetKind = new byte[n];
                Position = new float2[n]; Health = new float[n]; MaxHealth = new float[n];
            }
            for (int i = 0; i < count; i++)
            {
                Id[i] = data.IdOf[i];
                Owner[i] = data.OwnerId[i];
                Type[i] = data.Type[i];
                Stance[i] = data.Stance[i];
                Position[i] = data.Positions[i];
                Health[i] = data.Health[i];
                MaxHealth[i] = data.MaxHealth[i];
                Target[i] = data.Target[i];
                TargetKind[i] = data.TargetKind[i];
                OrderSlot[i] = data.OrderSlot[i];
            }
        }
    }
}
