/// <summary>Gifting resources to another player. Pure.</summary>
public static class GiftRules
{
    /// <summary>A finite amount of at least 1 that the sender can afford.</summary>
    public static bool IsValid(float amount, float balance) =>
        !float.IsNaN(amount) && !float.IsInfinity(amount) && amount >= 1f && amount <= balance;

    /// <summary>
    /// The sender may gift now: the match is being played, both players are still in it, they are
    /// different players, and the sender's cooldown has passed.
    /// </summary>
    public static bool CanGift(GameState state, PlayerState? sender, PlayerState? target, int senderId, int targetId,
                               double now, double lastGift, float cooldownSeconds) =>
        state == GameState.Playing && sender == PlayerState.Playing && target == PlayerState.Playing &&
        senderId != targetId && now - lastGift >= cooldownSeconds;

    /// <summary>Who learns of a gift: the sender and the recipient, and nobody else.</summary>
    public static int[] Recipients(int senderId, int targetId) => new[] { senderId, targetId };
}
