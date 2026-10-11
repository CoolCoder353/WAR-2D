using NUnit.Framework;
using WAR2D.Net.Steam;

public class ConnectionRouteTests
{
    [Test]
    public void SteamPrefixParsesToASteamRoute()
    {
        Assert.IsTrue(ConnectionRoute.TryParse("steam:76561198000000001", 7778, out var r));
        Assert.AreEqual(RouteKind.Steam, r.Kind);
        Assert.AreEqual(76561198000000001UL, r.SteamId);
    }

    [Test]
    public void AnAddressParsesToADirectRoute()
    {
        Assert.IsTrue(ConnectionRoute.TryParse("203.0.113.5:27015", 7778, out var r));
        Assert.AreEqual(RouteKind.Direct, r.Kind);
        Assert.AreEqual("203.0.113.5", r.Host);
        Assert.AreEqual(27015, r.Port);
    }

    [TestCase("steam:")]
    [TestCase("steam:abc")]
    [TestCase("steam:0")]
    [TestCase("steam:-5")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("1.2.3")]
    public void MalformedRoutesAreRejected(string text)
    {
        Assert.IsFalse(ConnectionRoute.TryParse(text, 7778, out _));
    }

    [Test]
    public void ToStringRoundTrips()
    {
        Assert.IsTrue(ConnectionRoute.TryParse(ConnectionRoute.ForSteam(76561198000000001UL).ToString(), 7778, out var r));
        Assert.AreEqual(76561198000000001UL, r.SteamId);
    }
}
