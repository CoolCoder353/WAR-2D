using System;
using UnityEngine;

namespace WAR2D.Spike
{
    /// <summary>
    /// Fires <see cref="Tick"/> from an accumulator in <c>Update</c> at exactly 20 Hz, the spike's
    /// tick rate. It never uses <c>FixedUpdate</c>, which runs at 50 Hz in this project.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpikeClock : MonoBehaviour
    {
        /// <summary>Seconds per tick (20 ticks per second).</summary>
        public const float TickSeconds = 1f / 20f;

        /// <summary>
        /// Called once per 20 Hz tick. A frame slower than <see cref="TickSeconds"/> fires the
        /// elapsed ticks back to back rather than dropping them.
        /// </summary>
        public Action Tick;

        private float accumulator;

        private void Update()
        {
            accumulator += Time.unscaledDeltaTime;
            while (accumulator >= TickSeconds)
            {
                accumulator -= TickSeconds;
                Tick?.Invoke();
            }
        }
    }
}
