using NUnit.Framework;
using WAR2D.Spike;

public class SpikeStatsTests
{
    [Test]
    public void PercentilesInterpolateBetweenSortedSamples()
    {
        var s = new SpikeStats();
        foreach (double v in new double[] { 5, 1, 4, 2, 3 }) s.Add(v);
        Assert.AreEqual(3.0, s.Percentile(50), 1e-9);
        Assert.AreEqual(4.8, s.Percentile(95), 1e-9);
        Assert.AreEqual(5.0, s.Max, 1e-9);
        Assert.AreEqual(3.0, s.Mean, 1e-9);
    }

    [Test]
    public void EmptyStatsReportNaN() => Assert.IsNaN(new SpikeStats().Percentile(95));

    [Test]
    public void ArgsParseInvariantNumbers()
    {
        SpikeArgs a = SpikeArgs.Parse(new[] { "-spike", "fog", "-units", "40000", "-teams", "2", "-quit" });
        Assert.AreEqual("fog", a.Bench);
        Assert.AreEqual(40000, a.Units);
        Assert.AreEqual(2, a.Teams);
        Assert.AreEqual(512, a.MapSize);
        Assert.IsTrue(a.Quit);
    }
}
