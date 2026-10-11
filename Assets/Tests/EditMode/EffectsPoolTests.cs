using NUnit.Framework;
using UnityEngine;

/// <summary>Effects reuse a fixed pool of sprites instead of making one per effect.</summary>
public class EffectsPoolTests
{
    [SetUp]
    public void SetUp() => Effects.Clear();

    [TearDown]
    public void TearDown() => Effects.Clear();

    [Test]
    public void FiveHundredExplosionsNeverExceedThePool()
    {
        for (int i = 0; i < 500; i++) Effects.Explosion(new Vector2(i, 0));
        Assert.AreEqual(Effects.ExplosionPool, Effects.Created, "one sprite per pool slot, made once");
        Assert.AreEqual(Effects.ExplosionPool, Effects.ActiveExplosions);
        for (int i = 0; i < 500; i++) Effects.Explosion(new Vector2(i, 1));
        Assert.AreEqual(Effects.ExplosionPool, Effects.Created, "no allocation after warm-up");
    }

    [Test]
    public void TheOldestExplosionIsRecycled()
    {
        for (int i = 0; i < Effects.ExplosionPool; i++) Effects.Explosion(new Vector2(i, 0));
        Effects.Explosion(new Vector2(-50, 7));
        GameObject runner = GameObject.Find("Effects");
        Assert.IsNotNull(runner);
        // The first explosion (at x = 0) was the oldest: its sprite moved to the newest position.
        Assert.AreEqual(new Vector3(-50, 7, 0), runner.transform.GetChild(0).position);
        Assert.AreEqual(new Vector3(1, 0, 0), runner.transform.GetChild(1).position);
    }

    [Test]
    public void TracersBringAMuzzleFlashAndUseTheirOwnPool()
    {
        for (int i = 0; i < 300; i++) Effects.Tracer(new Vector3(0, i, 0), new Vector3(5, i, 0));
        Assert.AreEqual(Effects.TracerPool + Effects.FlashPool, Effects.Created);
    }
}
