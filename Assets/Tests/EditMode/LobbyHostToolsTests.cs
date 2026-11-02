using System.Collections.Generic;
using NUnit.Framework;
using WAR2D.Net;
using WAR2D.Net.Steam;

/// <summary>Host tools in the lobby (playtest 2026-11-02, Figma K1/K2): server names, bans and the removal message.</summary>
public class LobbyHostToolsTests
{
    [Test]
    public void TheServerNameFallsBackToTheHosts()
    {
        Assert.AreEqual("Friday night 2v2", ServerNames.Display("Friday night 2v2", "Oscar"));
        Assert.AreEqual("Oscar's server", ServerNames.Display("", "Oscar"));
        Assert.AreEqual("Oscar's server", ServerNames.Display(null, "Oscar"));
    }

    [Test]
    public void BansHoldSteamIdsAndAddressesApart()
    {
        var bans = new LobbyBans();
        bans.Add(LobbyBans.SteamKey(76561198000000001UL));
        bans.Add(LobbyBans.AddressKey("192.168.1.20"));
        Assert.IsTrue(bans.Contains(LobbyBans.SteamKey(76561198000000001UL)));
        Assert.IsFalse(bans.Contains(LobbyBans.SteamKey(76561198000000002UL)));
        Assert.IsTrue(bans.Contains(LobbyBans.AddressKey("192.168.1.20")));
        Assert.IsFalse(bans.Contains(LobbyBans.AddressKey("76561198000000001")), "an address never matches a Steam ban");
        Assert.IsFalse(bans.Contains(null));
    }

    [Test]
    public void ARemovedPlayerIsToldWhyOnce()
    {
        LobbyRemoval.Received(new RemovedFromLobby { Banned = false, ServerName = "Oscar's server" });
        Assert.AreEqual("You were removed from Oscar's server by the host.", LobbyRemoval.Take());
        Assert.IsNull(LobbyRemoval.Take());
        Assert.AreEqual("You are banned from the server.", LobbyRemoval.Text(true, null));
    }

    [Test]
    public void TheJoinListReadsTheServerNameAndOlderHostsStillList()
    {
        var data = new LobbyInfo { HostName = "Oscar", ServerName = "Friday night", Version = "0.16.0", State = "lobby", Mode = "Teams", MapSize = 512 }.Encode();
        Assert.IsTrue(LobbyInfo.TryDecode(1, 2, 3, data, out LobbyInfo info));
        Assert.AreEqual("Friday night", info.ServerName);
        var older = new Dictionary<string, string>(data);
        older.Remove(LobbyInfo.KeyServerName);
        Assert.IsTrue(LobbyInfo.TryDecode(1, 2, 3, older, out LobbyInfo old), "the name is optional");
        Assert.AreEqual("Oscar's server", old.ServerName);
        older[LobbyInfo.KeyServerName] = "<b>spoof</b>‮";
        Assert.IsTrue(LobbyInfo.TryDecode(1, 2, 3, older, out LobbyInfo cleaned));
        StringAssert.DoesNotContain("<", cleaned.ServerName, "sanitised like a nickname");
    }
}
