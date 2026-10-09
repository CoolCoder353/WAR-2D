using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Sim;

public class WaypointBookTests
{
    [Test]
    public void AppendAndPopInFifoOrder()
    {
        var book = new WaypointBook(4);
        book.Append(7, new int2(1, 1), Stances.Move);
        book.Append(7, new int2(2, 2), Stances.AttackMove);
        Assert.IsTrue(book.TryPop(7, out int2 goal, out byte stance));
        Assert.AreEqual(new int2(1, 1), goal);
        Assert.AreEqual(Stances.Move, stance);
        Assert.IsTrue(book.TryPop(7, out goal, out stance));
        Assert.AreEqual(new int2(2, 2), goal);
        Assert.AreEqual(Stances.AttackMove, stance);
        Assert.IsFalse(book.TryPop(7, out _, out _));
    }

    [Test]
    public void CapDropsExtras()
    {
        var book = new WaypointBook(2);
        for (int i = 0; i < 5; i++) book.Append(3, new int2(i, i), Stances.Move);
        Assert.AreEqual(2, book.CountOf(3));
        book.TryPop(3, out int2 first, out _);
        book.TryPop(3, out int2 second, out _);
        Assert.AreEqual(new int2(0, 0), first);
        Assert.AreEqual(new int2(1, 1), second);
    }

    [Test]
    public void ZeroCapKeepsNothing()
    {
        var book = new WaypointBook(0);
        book.Append(3, int2.zero, Stances.Move);
        Assert.AreEqual(0, book.CountOf(3));
        Assert.IsFalse(book.TryPop(3, out _, out _));
    }

    [Test]
    public void ClearAndForget()
    {
        var book = new WaypointBook(4);
        book.Append(1, int2.zero, Stances.Move);
        book.Append(2, int2.zero, Stances.Move);
        book.Clear(1);
        book.Forget(2);
        Assert.AreEqual(0, book.CountOf(1));
        Assert.AreEqual(0, book.CountOf(2));
        Assert.IsFalse(book.TryPop(1, out _, out _));
        book.Append(1, new int2(5, 5), Stances.Move);
        Assert.AreEqual(1, book.CountOf(1), "a cleared unit can queue again");
    }

    [Test]
    public void CountOfIsPerUnit()
    {
        var book = new WaypointBook(4);
        book.Append(1, int2.zero, Stances.Move);
        book.Append(1, int2.zero, Stances.Move);
        book.Append(2, int2.zero, Stances.Move);
        Assert.AreEqual(2, book.CountOf(1));
        Assert.AreEqual(1, book.CountOf(2));
        Assert.AreEqual(0, book.CountOf(3));
    }
}
