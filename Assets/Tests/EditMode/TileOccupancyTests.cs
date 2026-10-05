using NUnit.Framework;
using Unity.Mathematics;

public class TileOccupancyTests
{
    private readonly int2 a = new int2(1, 1);
    private readonly int2 b = new int2(2, 1);

    [Test]
    public void FreeTile_IsAvailableToAnyone()
    {
        var occ = new TileOccupancy();
        Assert.That(occ.IsAvailable(a, 7), Is.True);
        Assert.That(occ.IsAvailable(a, -1), Is.True);
    }

    [Test]
    public void ClaimedTile_IsOnlyAvailableToOwner()
    {
        var occ = new TileOccupancy();
        Assert.That(occ.TryClaim(a, 7), Is.True);
        Assert.That(occ.IsAvailable(a, 7), Is.True);
        Assert.That(occ.IsAvailable(a, 8), Is.False);
        Assert.That(occ.IsAvailable(a, -1), Is.False);
        Assert.That(occ.TryClaim(a, 8), Is.False);
    }

    [Test]
    public void Release_ByNonOwner_DoesNothing()
    {
        var occ = new TileOccupancy();
        occ.TryClaim(a, 7);
        occ.Release(a, 8);
        Assert.That(occ.IsAvailable(a, 8), Is.False);
    }

    [Test]
    public void ReleaseAll_FreesEveryClaim()
    {
        var occ = new TileOccupancy();
        occ.TryClaim(a, 7);
        occ.TryClaim(b, 7);
        Assert.That(occ.ClaimCount(7), Is.EqualTo(2));
        occ.ReleaseAll(7);
        Assert.That(occ.ClaimCount(7), Is.EqualTo(0));
        Assert.That(occ.IsAvailable(a, 8), Is.True);
        Assert.That(occ.IsAvailable(b, 8), Is.True);
    }

    [Test]
    public void ReclaimingOwnTile_IsIdempotent()
    {
        var occ = new TileOccupancy();
        occ.TryClaim(a, 7);
        Assert.That(occ.TryClaim(a, 7), Is.True);
        Assert.That(occ.ClaimCount(7), Is.EqualTo(1));
    }
}
