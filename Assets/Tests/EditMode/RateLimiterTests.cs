using System.Collections.Generic;
using NUnit.Framework;

public class RateLimiterTests
{
    private static RateLimiter Make() => new RateLimiter(
        new Dictionary<string, (float, float)> { ["Move"] = (3f, 1f) }, (10f, 10f));

    [Test]
    public void AllowsBurstThenRejects()
    {
        var rl = Make();
        Assert.That(rl.TryConsume(1, "Move", 0), Is.True);
        Assert.That(rl.TryConsume(1, "Move", 0), Is.True);
        Assert.That(rl.TryConsume(1, "Move", 0), Is.True);
        Assert.That(rl.TryConsume(1, "Move", 0), Is.False);
    }

    [Test]
    public void RefillsOverTime()
    {
        var rl = Make();
        for (int i = 0; i < 3; i++) rl.TryConsume(1, "Move", 0);
        Assert.That(rl.TryConsume(1, "Move", 0.5), Is.False);
        Assert.That(rl.TryConsume(1, "Move", 1.0), Is.True);
    }

    [Test]
    public void ConnectionsAreIndependent()
    {
        var rl = Make();
        for (int i = 0; i < 3; i++) rl.TryConsume(1, "Move", 0);
        Assert.That(rl.TryConsume(2, "Move", 0), Is.True);
    }

    [Test]
    public void UnknownCommandUsesDefaultRule()
    {
        var rl = Make();
        for (int i = 0; i < 10; i++) Assert.That(rl.TryConsume(1, "Other", 0), Is.True);
        Assert.That(rl.TryConsume(1, "Other", 0), Is.False);
    }

    [Test]
    public void ForgetResetsConnection()
    {
        var rl = Make();
        for (int i = 0; i < 3; i++) rl.TryConsume(1, "Move", 0);
        rl.Forget(1);
        Assert.That(rl.TryConsume(1, "Move", 0), Is.True);
    }
}
