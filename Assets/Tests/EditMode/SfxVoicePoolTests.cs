using NUnit.Framework;
using UnityEngine;
using WAR2D.Audio;

/// <summary>Which sound effects the voice pool lets through.</summary>
public class SfxVoicePoolTests
{
    private static readonly Rect View = new Rect(0, 0, 40, 20);

    [Test]
    public void NeverMoreThanTheVoices()
    {
        var pool = new SfxVoicePool(8, 0f, _ => 1f);
        int played = 0;
        for (int i = 0; i < 10000; i++) if (pool.TryPlay("shot" + i % 50, new Vector2(20, 10), View, 0f)) played++;
        Assert.AreEqual(8, played);
        Assert.AreEqual(8, pool.Active(0.5f));
        Assert.AreEqual(0, pool.Active(1.5f), "voices free when their sound ends");
        Assert.IsTrue(pool.TryPlay("shot", new Vector2(20, 10), View, 1.5f));
    }

    [Test]
    public void TheSameSoundWithinTheCooldownIsDropped()
    {
        var pool = new SfxVoicePool(16, 0.05f, _ => 0.01f);
        int played = 0;
        for (int i = 0; i < 10000; i++) if (pool.TryPlay("shot", new Vector2(20, 10), View, 0f)) played++;
        Assert.AreEqual(1, played, "10,000 shots in one frame play once");
        Assert.IsFalse(pool.TryPlay("shot", new Vector2(20, 10), View, 0.04f));
        Assert.IsTrue(pool.TryPlay("shot", new Vector2(20, 10), View, 0.06f));
        Assert.IsTrue(pool.TryPlay("explosion", new Vector2(20, 10), View, 0.06f), "other sounds have their own cooldown");
    }

    [Test]
    public void SoundsOffScreenAreDropped()
    {
        var pool = new SfxVoicePool(16, 0f);
        Assert.IsTrue(pool.TryPlay("a", new Vector2(-5, 10), View, 0f), "inside the margin");
        Assert.IsFalse(pool.TryPlay("b", new Vector2(-15, 10), View, 0f));
        Assert.IsFalse(pool.TryPlay("c", new Vector2(20, 100), View, 0f));
    }

    [Test]
    public void VolumeFallsWithDistanceFromTheViewCentre()
    {
        float centre = SfxVoicePool.Volume(View.center, View);
        float edge = SfxVoicePool.Volume(new Vector2(40, 10), View);
        float margin = SfxVoicePool.Volume(new Vector2(50, 10), View);
        Assert.AreEqual(1f, centre, 1e-5f);
        Assert.Less(edge, centre);
        Assert.Less(margin, edge);
        Assert.AreEqual(0.3f, margin, 1e-5f);
    }
}
