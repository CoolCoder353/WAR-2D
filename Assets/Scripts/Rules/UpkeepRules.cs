/// <summary>Running-cost payment and unpaid-upkeep decay.</summary>
public static class UpkeepRules
{
    /// <summary>Deducts <paramref name="cost"/> from <paramref name="balance"/> when affordable. Free costs always succeed.</summary>
    public static bool TryCharge(ref float balance, float cost)
    {
        if (cost <= 0f) return true;
        if (balance < cost) return false;
        balance -= cost;
        return true;
    }

    /// <summary>Health lost over <paramref name="seconds"/> while upkeep is unpaid.</summary>
    public static float DecayDamage(float maxHealth, float percentPerSecond, float seconds) => maxHealth * percentPerSecond / 100f * seconds;
}
