using Config;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Dev.DebugApi;

public class DebugApiPlayModeTests
{
    [UnityTest]
    public IEnumerator PingOverTheSocket()
    {
        DebugApi.AllowForTests = true;
        DebugServer.EnsureRunning();
        yield return null;
        Assert.IsTrue(DebugApi.Listening);

        string file = Path.Combine(Application.persistentDataPath, "debugapi", DebugServer.Instance.BoundPort + ".json");
        Assert.IsTrue(File.Exists(file));
        string token = (string)JObject.Parse(File.ReadAllText(file))["token"];

        using var client = new TcpClient("127.0.0.1", DebugServer.Instance.BoundPort);
        var stream = client.GetStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        var reader = new StreamReader(stream);
        writer.WriteLine($"{{\"id\":1,\"token\":\"{token}\",\"cmd\":\"ping\"}}");
        var read = reader.ReadLineAsync();
        float until = Time.realtimeSinceStartup + 5f;
        while (!read.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
        Assert.IsTrue(read.IsCompleted, "no reply");
        JObject reply = JObject.Parse(read.Result);
        Assert.IsTrue((bool)reply["ok"]);
        Assert.AreEqual("pong", (string)reply["result"]);

        DebugServer.Shutdown();
        DebugApi.AllowForTests = false;
        Assert.IsFalse(File.Exists(file));
    }

    private static JObject Call(string cmd, string args = "{}")
    {
        JObject reply = null;
        var d = new DebugDispatcher(DebugServer.Registry, "t", () => Mirror.NetworkServer.active, () => Mirror.NetworkClient.active);
        d.Handle($"{{\"id\":1,\"token\":\"t\",\"cmd\":\"{cmd}\",\"args\":{args}}}", r => reply = JObject.Parse(r));
        return reply; // waits are covered by the socket test; scenario calls here are immediate
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator ScenarioSpawnAndPauseInAHostedMatch()
    {
        DebugApi.AllowForTests = true;
        DebugServer.EnsureRunning(); // the settled snapshot is captured only while the API listens
        yield return PlayModeMatch.LoadMenu();
        GameConfigData config = PlayModeMatch.Configure();
        yield return PlayModeMatch.StartMatchAsHost(config);
        Unity.Mathematics.int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);
        int owner = PlayModeMatch.LocalOwner;
        Call("spawn", $"{{\"owner\":{owner},\"type\":\"Tank\",\"tile\":[{hq.x + 8},{hq.y}],\"count\":5}}");  // answers later (a wait): the settled snapshot below proves it
        yield return PlayModeMatch.WaitUntil(() => SettledSnapshot.Current != null && (int)JObject.FromObject(ReadCommands.Units(SettledSnapshot.Current, JObject.Parse($"{{\"owner\":{owner},\"type\":\"Tank\"}}")))["count"] >= 5, 10f);
        Assert.IsTrue((bool)Call("pause")["ok"]);
        yield return new WaitForSeconds(0.3f);
        int tick = WAR2D.Sim.SimContext.Current.Clock.Tick;
        yield return new WaitForSeconds(0.5f);
        Assert.AreEqual(tick, WAR2D.Sim.SimContext.Current.Clock.Tick, "paused");
        Assert.IsTrue((bool)Call("resume")["ok"]);
        yield return PlayModeMatch.TearDown();
    }

[UnityTest]
public IEnumerator ScreenshotWritesAPng()
{
    DebugApi.AllowForTests = true;
    JObject reply = null;
    var d = new DebugDispatcher(DebugServer.Registry, "t", () => false, () => false);
    string path = System.IO.Path.Combine(UnityEngine.Application.temporaryCachePath, "shot-test.png");
    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
    d.Handle($"{{\"id\":1,\"token\":\"t\",\"cmd\":\"screenshot\",\"args\":{{\"path\":\"{path.Replace("\\", "/")}\"}}}}", s => reply = JObject.Parse(s));
    float until = Time.realtimeSinceStartup + 5f;
    while (reply == null && Time.realtimeSinceStartup < until) { d.Pump(Time.unscaledDeltaTime); yield return null; }
    Assert.IsTrue((bool)reply["ok"], (string)reply["error"]);
    Assert.IsTrue(System.IO.File.Exists(path));
    DebugApi.AllowForTests = false;
}
}
