using NUnit.Framework;

public class GiftRulesTests
{
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    [TestCase(0f)]
    [TestCase(0.5f)]
    [TestCase(-10f)]
    [TestCase(1001f)]
    public void InvalidAmountsAreRefused(float amount) => Assert.IsFalse(GiftRules.IsValid(amount, 1000f));

    [TestCase(1f)]
    [TestCase(500f)]
    [TestCase(1000f)]
    public void AmountsUpToTheBalanceAreAllowed(float amount) => Assert.IsTrue(GiftRules.IsValid(amount, 1000f));

    [Test]
    public void OnlyLivePlayersInPlayAfterTheCooldown()
    {
        Assert.IsTrue(GiftRules.CanGift(GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 2, 10, 4, 5f));
        Assert.IsFalse(GiftRules.CanGift(GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 2, 8, 4, 5f), "cooldown");
        Assert.IsFalse(GiftRules.CanGift(GameState.Playing, PlayerState.Playing, PlayerState.Playing, 1, 1, 10, 0, 5f), "not to yourself");
        Assert.IsFalse(GiftRules.CanGift(GameState.Playing, PlayerState.Playing, PlayerState.Eliminated, 1, 2, 10, 0, 5f), "not to an eliminated player");
        Assert.IsFalse(GiftRules.CanGift(GameState.Playing, PlayerState.Eliminated, PlayerState.Playing, 1, 2, 10, 0, 5f), "not when you're out");
        Assert.IsFalse(GiftRules.CanGift(GameState.Lobby, PlayerState.Playing, PlayerState.Playing, 1, 2, 10, 0, 5f), "only during play");
        Assert.IsFalse(GiftRules.CanGift(GameState.Playing, PlayerState.Playing, null, 1, 2, 10, 0, 5f), "not to an unknown player");
    }
}
