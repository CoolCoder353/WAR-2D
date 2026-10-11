using NUnit.Framework;
using WAR2D.Net.Steam;

public class GameManagerRouteTests
{
    [Test]
    public void SteamRoutesNeedSteam()
    {
        var r = ConnectionRoute.ForSteam(76561198000000002UL);
        Assert.IsFalse(GameManager.CanConnect(r, steamAvailable: false, directAllowed: true, out string why));
        StringAssert.Contains("Steam", why);
        Assert.IsTrue(GameManager.CanConnect(r, true, false, out _));
    }

    [Test]
    public void DirectRoutesNeedTheDirectFlag()
    {
        var r = ConnectionRoute.ForAddress("127.0.0.1", 7778);
        Assert.IsFalse(GameManager.CanConnect(r, true, false, out string why));
        StringAssert.Contains("-direct", why);
        Assert.IsTrue(GameManager.CanConnect(r, false, true, out _));
    }
}
