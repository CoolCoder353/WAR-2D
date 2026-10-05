using System.Collections.Generic;
using NUnit.Framework;

public class NetIdAllocatorTests
{
    [Test]
    public void Allocate_IsUniqueAndPositive()
    {
        var ids = new NetIdAllocator();
        var seen = new HashSet<int>();
        for (int i = 0; i < 10000; i++)
        {
            int id = ids.Allocate();
            Assert.That(id, Is.GreaterThan(0));
            Assert.That(seen.Add(id), Is.True);
        }
    }

    [Test]
    public void Reset_StartsAgainAtOne()
    {
        var ids = new NetIdAllocator();
        ids.Allocate();
        ids.Allocate();
        ids.Reset();
        Assert.That(ids.Allocate(), Is.EqualTo(1));
    }
}
