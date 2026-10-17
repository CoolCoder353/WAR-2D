using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Client;

/// <summary>The client's record of each unit's goals, behind the Shift markers and "Queue full".</summary>
public class OrderQueueTrackerTests
{
    [Test]
    public void QueuedGoalsAddUpToTheLimitAndAPlainOrderReplacesThem()
    {
        var tracker = new OrderQueueTracker(maxQueued: 4);
        Assert.AreEqual(5, tracker.Limit, "the order plus four queued");
        int[] unit = { 7 };
        tracker.Sent(unit, new float2(1, 1), queue: false);
        for (int i = 2; i <= 6; i++) tracker.Sent(unit, new float2(i, i), queue: true);
        Assert.AreEqual(5, tracker.GoalsOf(7).Count, "the sixth is dropped, as the server drops it");
        Assert.IsTrue(tracker.IsFull(7));
        tracker.Sent(unit, new float2(9, 9), queue: false);
        Assert.AreEqual(new List<float2> { new float2(9, 9) }, new List<float2>(tracker.GoalsOf(7)));
        Assert.IsFalse(tracker.IsFull(7));
    }

    [Test]
    public void AQueuedOrderWithNothingCurrentStartsTheList()
    {
        var tracker = new OrderQueueTracker(4);
        tracker.Sent(new[] { 3 }, new float2(5, 5), queue: true);
        Assert.AreEqual(1, tracker.GoalsOf(3).Count);
    }

    [Test]
    public void ReachedGoalsDropOffAndLostUnitsAreForgotten()
    {
        var tracker = new OrderQueueTracker(4);
        tracker.Sent(new[] { 1, 2 }, new float2(10, 10), false);
        tracker.Sent(new[] { 1, 2 }, new float2(20, 10), true);
        var at = new Dictionary<int, float2> { [1] = new float2(10.5f, 10f) };
        tracker.Prune(id => at.TryGetValue(id, out float2 p) ? p : (float2?)null, 1.5f);
        Assert.AreEqual(new List<float2> { new float2(20, 10) }, new List<float2>(tracker.GoalsOf(1)), "the reached goal is done");
        Assert.AreEqual(0, tracker.GoalsOf(2).Count, "a unit the client no longer knows is forgotten");
        tracker.Cleared(new[] { 1 });
        Assert.AreEqual(0, tracker.GoalsOf(1).Count, "Stop clears the goals");
    }

    [Test]
    public void SquadTooltipsSayHowSquadsWork()
    {
        Assert.AreEqual("Squad 4 · 45 units\nCtrl+4 saves the selection here. 4 selects it, twice centres the camera.", WAR2D.UI.SquadBarController.Tooltip(3, 45));
        StringAssert.StartsWith("Squad 0 · empty", WAR2D.UI.SquadBarController.Tooltip(9, 0));
        StringAssert.StartsWith("Squad 1 · 1 unit\n", WAR2D.UI.SquadBarController.Tooltip(0, 1));
    }
}
