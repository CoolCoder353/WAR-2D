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
        feed.PingRequested += _ => pings++;
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
    public void TextNamesOnlyGiftersAndSharers()
    {
        string Name(int owner) => "P" + owner;
        Assert.AreEqual("Under attack near your Spawner", AlertFeedModel.Text(new Alert { Kind = AlertKind.UnderAttack }, Name, "Spawner"));
        Assert.AreEqual("P7 shares vision with you", AlertFeedModel.Text(new Alert { Kind = AlertKind.VisionSharedWithYou, OtherOwnerId = 7 }, Name, null));
        Assert.AreEqual("P7 gifted you 500", AlertFeedModel.Text(new Alert { Kind = AlertKind.GiftReceived, OtherOwnerId = 7, Amount = 500 }, Name, null));
    }
}
