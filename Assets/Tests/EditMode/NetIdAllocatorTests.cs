using System.Collections.Generic;
using NUnit.Framework;

public class NetIdAllocatorTests
{
    [Test] public void FreshIdsArePositiveAndUnique()
    {
        var a = new NetIdAllocator(1024);
        var seen = new HashSet<int>();
        for (int i = 0; i < 1024; i++) { int id = a.Allocate(); Assert.Greater(id, 0); Assert.IsTrue(seen.Add(id)); }
    }

    [Test] public void FreedIndexIsReusedWithANewGeneration()
    {
        var a = new NetIdAllocator(4);
        int first = a.Allocate();
        a.Free(first);
        int again = a.Allocate();
        Assert.AreEqual(NetIdAllocator.IndexOf(first), NetIdAllocator.IndexOf(again));
        Assert.AreNotEqual(first, again);
        Assert.IsFalse(a.IsLive(first));
        Assert.IsTrue(a.IsLive(again));
    }

    [Test] public void ExhaustionThrows()
    {
        var a = new NetIdAllocator(2);
        a.Allocate(); a.Allocate();
        Assert.Throws<System.InvalidOperationException>(() => a.Allocate());
    }

    [Test] public void DoubleFreeIsIgnored()
    {
        var a = new NetIdAllocator(2);
        int id = a.Allocate(); a.Free(id); a.Free(id);
        a.Allocate(); a.Allocate(); // both indices usable exactly once more
        Assert.Throws<System.InvalidOperationException>(() => a.Allocate());
    }

    [Test] public void GenerationWrapsBackToOne()
    {
        var a = new NetIdAllocator(1);
        int id = a.Allocate();
        for (int i = 0; i < 2100; i++) { a.Free(id); id = a.Allocate(); Assert.Greater(id, 0); }
        Assert.That(NetIdAllocator.GenerationOf(id), Is.InRange(1, 2047));
    }
}
