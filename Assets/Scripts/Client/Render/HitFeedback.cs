using Unity.Mathematics;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// How a hit shows on a unit or building the client sees lose health: it flashes red for <see cref="FlashSeconds"/>, and
    /// its health bar keeps the lost part as a pale trail that holds for <see cref="HoldSeconds"/>, then drains at
    /// <see cref="DrainPerSecond"/> (a whole bar in 0.4 s). Visual constants, like the building flash; pure, for tests.
    /// </summary>
    public static class HitFeedback
    {
        /// <summary>Seconds a hit unit or building flashes red.</summary>
        public const float FlashSeconds = 0.15f;

        /// <summary>Seconds the trail waits after the last hit before it drains.</summary>
        public const float HoldSeconds = 0.25f;

        /// <summary>Health fraction the trail drains per second.</summary>
        public const float DrainPerSecond = 2.5f;

        /// <summary>True while a hit at <paramref name="hitAt"/> still flashes at <paramref name="now"/>.</summary>
        public static bool Flashing(float hitAt, float now) => now >= hitAt && now - hitAt < FlashSeconds;

        /// <summary>
        /// The trail's next level, a health fraction never below <paramref name="health"/>: it follows healing at once, holds after a
        /// hit, then drains toward the health.
        /// </summary>
        public static float Trail(float trail, float health, float sinceHit, float dt)
        {
            if (health >= trail) return health;
            if (sinceHit < HoldSeconds) return trail;
            return math.max(health, trail - DrainPerSecond * dt);
        }
    }
}
