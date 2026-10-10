/// <summary>Server-side checks for a diplomacy change (Cmd_SetAttack, Cmd_SetShareVision).</summary>
public static class DiplomacyRules
{
    /// <summary>
    /// True when a change may be applied: diplomacy is on, the match is Playing, the sender is still
    /// playing, the target is another live player, and the sender's cooldown for this command has passed.
    /// </summary>
    public static bool CanChange(bool enabled, GameState state, PlayerState? sender, PlayerState? target,
                                 int senderId, int targetId, double now, double lastChange, float cooldownSeconds)
    {
        if (!enabled || state != GameState.Playing) return false;
        if (sender != PlayerState.Playing || target != PlayerState.Playing || senderId == targetId) return false;
        return now - lastChange >= cooldownSeconds;
    }
}
