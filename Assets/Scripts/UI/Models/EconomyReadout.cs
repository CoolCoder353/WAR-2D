using System.Collections.Generic;

namespace WAR2D.UI
{
    /// <summary>How the resources readout reads in the top bar (Figma v0.15 QoL redesign, item 4).</summary>
    public enum EconomyState
    {
        /// <summary>Net income is zero or better.</summary>
        Healthy,
        /// <summary>Upkeep is higher than income, but the balance lasts a minute or more.</summary>
        Negative,
        /// <summary>The balance runs out within <see cref="EconomyReadout.LowSeconds"/>.</summary>
        RunningLow,
        /// <summary>The balance is empty and upkeep is higher than income: things are decaying.</summary>
        Decaying,
    }

    /// <summary>
    /// The top bar's net-income readout: income minus upkeep averaged over the last <see cref="WindowSeconds"/> (so it does not
    /// flicker), the time until the balance runs out, and the state they make. Pure: <see cref="Feed"/> takes the clock.
    /// </summary>
    public sealed class EconomyReadout
    {
        /// <summary>The averaging window.</summary>
        public const float WindowSeconds = 5f;

        /// <summary>A negative net that empties the balance within this many seconds is <see cref="EconomyState.RunningLow"/>.</summary>
        public const float LowSeconds = 60f;

        private readonly struct Sample
        {
            public readonly float Time, Net;
            public Sample(float time, float net) { Time = time; Net = net; }
        }

        private readonly Queue<Sample> samples = new Queue<Sample>();
        private bool lowWarned;

        /// <summary>Net income per second over the window (income minus upkeep).</summary>
        public float NetPerSecond { get; private set; }

        /// <summary>Seconds until the balance reaches zero at the current net, or -1 when it is not falling.</summary>
        public float SecondsUntilEmpty { get; private set; } = -1f;

        /// <summary>The state to show.</summary>
        public EconomyState State { get; private set; }

        /// <summary>Raised once when the state first becomes <see cref="EconomyState.RunningLow"/>; it re-arms when the net is zero or better.</summary>
        public event System.Action LowFunds;

        /// <summary>Records this moment's balance, income and upkeep.</summary>
        public void Feed(float resources, float incomePerSecond, float upkeepPerSecond, float now)
        {
            samples.Enqueue(new Sample(now, incomePerSecond - upkeepPerSecond));
            while (samples.Count > 1 && now - samples.Peek().Time > WindowSeconds) samples.Dequeue();
            float sum = 0f;
            foreach (Sample s in samples) sum += s.Net;
            NetPerSecond = sum / samples.Count;
            SecondsUntilEmpty = NetPerSecond < 0f ? resources / -NetPerSecond : -1f;

            EconomyState state;
            if (NetPerSecond >= 0f) state = EconomyState.Healthy;
            else if (resources < 1f) state = EconomyState.Decaying;
            else if (SecondsUntilEmpty < LowSeconds) state = EconomyState.RunningLow;
            else state = EconomyState.Negative;
            State = state;

            if (state == EconomyState.Healthy) lowWarned = false;
            else if (state == EconomyState.RunningLow && !lowWarned)
            {
                lowWarned = true;
                LowFunds?.Invoke();
            }
        }

        /// <summary>The readout's text: "+24/s net", "▼ −12/s net", "−12/s · runs out in 0:48" or "Decaying".</summary>
        public string Text()
        {
            int net = (int)System.Math.Round(NetPerSecond);
            // A small fall reads with a decimal ("−0.4/s"), never "−0/s".
            string fall = NetPerSecond < 0f && net == 0 ? $"{-NetPerSecond:0.0}" : $"{System.Math.Abs(net):N0}";
            switch (State)
            {
                case EconomyState.Healthy: return $"+{net:N0}/s net";
                case EconomyState.Negative: return $"▼ −{fall}/s net";
                case EconomyState.RunningLow: return $"−{fall}/s · runs out in {Clock(SecondsUntilEmpty)}";
                default: return "Decaying";
            }
        }

        /// <summary>Seconds as m:ss.</summary>
        public static string Clock(float seconds)
        {
            int s = System.Math.Max(0, (int)System.Math.Ceiling(seconds));
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
