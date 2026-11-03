using NUnit.Framework;
using Unity.Mathematics;

public class MinerRulesTests
{
    [TestCase(0f, 1, 0)]
    [TestCase(90f, 0, 1)]
    [TestCase(180f, -1, 0)]
    [TestCase(270f, 0, -1)]
    [TestCase(-90f, 0, -1)]
    [TestCase(450f, 0, 1)]
    [TestCase(89.9f, 0, 1)]
    public void FacingOffset_MapsRotationToNeighbour(float degrees, int x, int y)
    {
        Assert.That(MinerRules.FacingOffset(degrees), Is.EqualTo(new int2(x, y)));
    }

    [TestCase(0f)]
    [TestCase(90f)]
    [TestCase(180f)]
    [TestCase(270f)]
    public void ZDegrees_RecoversEulerZ(float degrees)
    {
        quaternion q = quaternion.Euler(0, 0, math.radians(degrees));
        float back = (MinerRules.ZDegrees(q) % 360f + 360f) % 360f;
        Assert.That(back, Is.EqualTo(degrees).Within(0.01f));
    }

    [Test]
    public void AMinerEarnsFromEitherGemAtItsRate()
    {
        Assert.IsTrue(MinerRules.Produces(TileType.Gem));
        Assert.IsTrue(MinerRules.Produces(TileType.RichGem));
        Assert.IsFalse(MinerRules.Produces(TileType.Wall));
        Assert.AreEqual(1f, MinerRules.RateOf(TileType.Gem, 2.5f));
        Assert.AreEqual(2.5f, MinerRules.RateOf(TileType.RichGem, 2.5f));
        Assert.AreEqual(0f, MinerRules.RateOf(TileType.Ground, 2.5f));
    }

    [Test]
    public void AMinerSaysWhatItMines()
    {
        // v0.17 (Figma H1): the own Miner's hover line and the placement label.
        Assert.AreEqual("Mining rich gem: 25/s", WAR2D.Client.BuildingHover.MiningLine(TileType.RichGem, 10f, 2.5f));
        Assert.AreEqual("Mining: 10/s", WAR2D.Client.BuildingHover.MiningLine(TileType.Gem, 10f, 2.5f));
        Assert.IsNull(WAR2D.Client.BuildingHover.MiningLine(TileType.Ground, 10f, 2.5f));
        Assert.AreEqual("Rich gem: 25/s", BuildingPlacement.MinerLabel(TileType.RichGem, 10f, 2.5f));
        Assert.AreEqual("Gem: 10/s", BuildingPlacement.MinerLabel(TileType.Gem, 10f, 2.5f));
        Assert.IsNull(BuildingPlacement.MinerLabel(TileType.Wall, 10f, 2.5f));
    }
}
