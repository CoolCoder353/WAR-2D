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
}
