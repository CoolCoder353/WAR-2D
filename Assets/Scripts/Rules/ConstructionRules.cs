using Unity.Mathematics;

/// <summary>Blueprint construction (v0.7). Pure.</summary>
public static class ConstructionRules
{
    /// <summary>A new blueprint's health: it can be attacked and destroyed from the start.</summary>
    public const float StartHealth = 1f;

    /// <summary>Sight radius in tiles of a building under construction.</summary>
    public const float BlueprintSight = 1f;

    /// <summary>
    /// What removing an own building refunds (owner, 2026-10-15): a blueprint its whole cost, whatever its
    /// progress; a finished building <paramref name="demolishRefund"/> of it (<c>Siege.DemolishRefund</c>).
    /// </summary>
    public static float Refund(bool blueprint, float cost, float demolishRefund) =>
        blueprint ? cost : cost * math.saturate(demolishRefund);

    /// <summary>Health a repair restores: the work done, capped by what is missing and by what the budget pays for.</summary>
    public static float AffordableRepair(float work, float missing, float costPerHealth, float budget)
    {
        float health = math.min(work, math.max(0f, missing));
        return costPerHealth > 0f ? math.min(health, math.max(0f, budget) / costPerHealth) : health;
    }

    /// <summary>
    /// Adds <paramref name="work"/> health-points of construction: health and progress rise together
    /// (progress is work done over max health), health never past max. True once progress reaches 1.
    /// </summary>
    public static bool AddWork(ref float progress, ref float health, float maxHealth, float work)
    {
        if (maxHealth <= 0f || work <= 0f) return progress >= 1f;
        progress = math.min(1f, progress + work / maxHealth);
        health = math.min(maxHealth, health + work);
        return progress >= 1f;
    }
}
