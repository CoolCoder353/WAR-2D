using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;

/// <summary>Fights on the minimap (v0.16, Figma C): one pulse per cell, a quiet time after it, a cap, and the own colour.</summary>
public class MinimapCombatTests
{
    private static MinimapCombat Make() => new MinimapCombat(cellTiles: 16, pulseSeconds: 1.5f, quietSeconds: 3f, max: 2);

    [Test]
    public void HitsInOneCellMakeOnePulseAtTheCellCentre()
    {
        MinimapCombat m = Make();
        m.Hit(new float2(3, 3), own: false, now: 0f);
        m.Hit(new float2(10, 12), own: false, now: 0.1f);
        Assert.AreEqual(1, m.Pulses.Count);
        Assert.AreEqual(new float2(8, 8), m.Pulses[0].Centre);
    }

    [Test]
    public void UpkeepDecayIsNotAFightButABiggerDropIs()
    {
        MinimapCombat m = Make();
        m.DecayPercentPerSecond = 5f;
        Assert.IsFalse(m.IsOwnDecay(1, 0.2f), "not decaying: every drop is a hit");
        m.OwnDecaying = true;
        Assert.IsTrue(m.IsOwnDecay(2, 0.2f), "5 %/s over 0.2 s, plus a point of rounding");
        Assert.IsTrue(m.IsOwnDecay(6, 1f));
        Assert.IsFalse(m.IsOwnDecay(10, 1f), "more than decay explains: a hit");
        Assert.IsFalse(m.IsOwnDecay(15, 60f), "a long gap is capped at the decay window");
    }

    [Test]
    public void AnOwnHitDuringThePulseTurnsItOwn()
    {
        MinimapCombat m = Make();
        m.Hit(new float2(3, 3), false, 0f);
        m.Hit(new float2(4, 4), true, 0.5f);
        Assert.IsTrue(m.Pulses[0].Own);
    }

    [Test]
    public void ACellStaysQuietAfterItsPulse()
    {
        MinimapCombat m = Make();
        m.Hit(new float2(3, 3), false, 0f);
        m.Update(2f); // the pulse (1.5 s) ended
        Assert.AreEqual(0, m.Pulses.Count);
        m.Hit(new float2(3, 3), false, 2f);
        Assert.AreEqual(0, m.Pulses.Count, "quiet until 1.5 + 3 s");
        m.Hit(new float2(3, 3), false, 4.6f);
        Assert.AreEqual(1, m.Pulses.Count);
    }

    [Test]
    public void NoMoreThanMaxPulses()
    {
        MinimapCombat m = Make();
        for (int i = 0; i < 5; i++) m.Hit(new float2(i * 32, 0), false, 0f);
        Assert.AreEqual(2, m.Pulses.Count);
    }
}
