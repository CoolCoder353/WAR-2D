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

    /// <summary>True when an unpaid building loses health: only once its owner has no units left to lose first.</summary>
    public static bool BuildingDecays(int ownerUnits) => ownerUnits <= 0;

    /// <summary>Health lost over <paramref name="seconds"/> while upkeep is unpaid.</summary>
    public static float DecayDamage(float maxHealth, float percentPerSecond, float seconds) => maxHealth * percentPerSecond / 100f * seconds;
}
