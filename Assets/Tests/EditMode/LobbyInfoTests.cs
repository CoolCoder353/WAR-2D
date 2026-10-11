using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Net.Steam;

public class LobbyInfoTests
{
    private static LobbyInfo Make(ulong id, string name, int players, string state = "lobby", string ver = "0.9.0") =>
        new LobbyInfo { LobbyId = id, HostSteamId = id + 1, HostName = name, Version = ver, State = state, Mode = "FreeForAll", MapSize = 128, Players = players };

    [Test]
    public void EncodeThenDecodeRoundTrips()
    {
        LobbyInfo a = Make(10, "Ann", 3);
        Assert.IsTrue(LobbyInfo.TryDecode(a.LobbyId, a.HostSteamId, a.Players, a.Encode(), out LobbyInfo b));
        Assert.AreEqual(a, b);
    }

    [Test]
    public void DecodeRejectsMissingKeysAndBadNumbers()
    {
        var data = Make(10, "Ann", 3).Encode();
        data.Remove(LobbyInfo.KeyVersion);
        Assert.IsFalse(LobbyInfo.TryDecode(10, 11, 3, data, out _));
        data = Make(10, "Ann", 3).Encode();
        data[LobbyInfo.KeyMapSize] = "big";
        Assert.IsFalse(LobbyInfo.TryDecode(10, 11, 3, data, out _));
    }

    [Test]
    public void DecodeTruncatesAHugeHostName()
    {
        var data = Make(10, new string('x', 500), 1).Encode();
        Assert.IsTrue(LobbyInfo.TryDecode(10, 11, 1, data, out LobbyInfo info));
        Assert.LessOrEqual(info.HostName.Length, CommandValidator.MaxNicknameLength);
    }

    [Test]
    public void BrowserHidesStartedFullAndForeignVersionLobbies()
    {
        var all = new List<LobbyInfo>
        {
            Make(1, "Zed", 2), Make(2, "Amy", 5), Make(3, "Started", 4, state: "playing"),
            Make(4, "Full", LobbyRules.MaxPlayers), Make(5, "Old", 2, ver: "0.8.0"),
        };
        List<LobbyInfo> shown = LobbyListModel.Visible(all, "0.9.0");
        CollectionAssert.AreEqual(new ulong[] { 2, 1 }, shown.ConvertAll(l => l.LobbyId));
    }

    [TestCase("0.9.0", "0.9.0", true)]
    [TestCase("0.9.0", "0.9.1", false)]
    [TestCase("", "0.9.0", false)]
    [TestCase(null, null, false)]
    public void VersionsMustMatchExactly(string a, string b, bool expected) =>
        Assert.AreEqual(expected, GameVersion.IsCompatible(a, b));
}
