using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.UI;

public class AlertThrottleTests
{
    [Test]
    public void SameAreaWithinTheThrottleIsSuppressed()
    {
        var throttle = new AlertThrottle(10f, 32);
        Assert.IsTrue(throttle.Allow(1, AlertKind.UnderAttack, new int2(5, 5), 0));
        Assert.IsFalse(throttle.Allow(1, AlertKind.UnderAttack, new int2(20, 30), 9.9), "same 32-tile area");
    }

    [Test]
    public void AnotherAreaOrOwnerOrKindIsAllowed()
    {
        var throttle = new AlertThrottle(10f, 32);
        Assert.IsTrue(throttle.Allow(1, AlertKind.UnderAttack, new int2(5, 5), 0));
        Assert.IsTrue(throttle.Allow(1, AlertKind.UnderAttack, new int2(40, 5), 1), "the next area");
        Assert.IsTrue(throttle.Allow(2, AlertKind.UnderAttack, new int2(5, 5), 1), "another owner");
        Assert.IsTrue(throttle.Allow(1, AlertKind.UpkeepUnpaid, new int2(5, 5), 1), "another kind");
    }

    [Test]
    public void TheThrottleExpires()
    {
        var throttle = new AlertThrottle(10f, 32);
        Assert.IsTrue(throttle.Allow(1, AlertKind.UnderAttack, new int2(5, 5), 0));
        Assert.IsTrue(throttle.Allow(1, AlertKind.UnderAttack, new int2(5, 5), 10.0));
    }

    [Test]
    public void FeedKeepsTheNewestAndDismissesAndExpires()
    {
        var feed = new AlertFeedModel(8f);
        int pings = 0;
        feed.PingRequested += (_, _) => pings++;
        for (int i = 0; i < AlertFeedModel.MaxShown + 2; i++) feed.Add(new Alert { Kind = AlertKind.UnderAttack }, "a" + i, i);
        Assert.AreEqual(AlertFeedModel.MaxShown, feed.Entries.Count);
        Assert.AreEqual("a2", feed.Entries[0].Text, "the oldest drop off");
        Assert.AreEqual(AlertFeedModel.MaxShown + 2, pings);
        feed.Dismiss(feed.Entries[0].Id);
        Assert.AreEqual(AlertFeedModel.MaxShown - 1, feed.Entries.Count);
        feed.Expire(100f);
        Assert.AreEqual(0, feed.Entries.Count);
    }

    [Test]
    public void ClickingAnAlertWithAPlaceAsksForTheCameraAndOthersDoNot()
    {
        var feed = new AlertFeedModel(8f);
        var focused = new System.Collections.Generic.List<int2>();
        feed.FocusRequested += focused.Add;
        feed.Add(new Alert { Kind = AlertKind.UnderAttack, Tile = new int2(7, 9) }, "attack", 0f);
        feed.Add(new Alert { Kind = AlertKind.UpkeepUnpaid }, "unpaid", 0f);
        feed.Focus(feed.Entries[0].Id);
        feed.Focus(feed.Entries[1].Id);
        CollectionAssert.AreEqual(new[] { new int2(7, 9) }, focused);
        Assert.AreEqual(2, feed.Entries.Count, "focusing does not dismiss");
    }

    [Test]
    public void SiegeAlertsPingAndReadAsApproved()
    {
        var feed = new AlertFeedModel(8f);
        var pinged = new System.Collections.Generic.List<AlertKind>();
        feed.PingRequested += (_, kind) => pinged.Add(kind);
        foreach (AlertKind kind in new[] { AlertKind.ConstructionFinished, AlertKind.BombTriggered, AlertKind.EnemySpotted, AlertKind.RepairsStopped })
            feed.Add(new Alert { Kind = kind }, "", 0f);
        CollectionAssert.AreEqual(new[] { AlertKind.BombTriggered, AlertKind.EnemySpotted }, pinged);
        System.Func<int, string> none = _ => "x";
        Assert.AreEqual("Construction finished: Spawner", AlertFeedModel.Text(new Alert { Kind = AlertKind.ConstructionFinished }, none, "Spawner"));
        Assert.AreEqual("Bomb triggered", AlertFeedModel.Text(new Alert { Kind = AlertKind.BombTriggered }, none, null));
        Assert.AreEqual("Enemy spotted by Radar", AlertFeedModel.Text(new Alert { Kind = AlertKind.EnemySpotted }, none, null));
        Assert.AreEqual("Out of resources: repairs stopped", AlertFeedModel.Text(new Alert { Kind = AlertKind.RepairsStopped }, none, null));
    }

    [Test]
    public void TextNamesOnlyGiftersAndSharers()
    {
        string Name(int owner) => "P" + owner;
        Assert.AreEqual("Under attack near your Spawner", AlertFeedModel.Text(new Alert { Kind = AlertKind.UnderAttack }, Name, "Spawner"));
        Assert.AreEqual("P7 shares vision with you", AlertFeedModel.Text(new Alert { Kind = AlertKind.VisionSharedWithYou, OtherOwnerId = 7 }, Name, null));
        Assert.AreEqual("P7 gifted you 500", AlertFeedModel.Text(new Alert { Kind = AlertKind.GiftReceived, OtherOwnerId = 7, Amount = 500 }, Name, null));
    }
}
