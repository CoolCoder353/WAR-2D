using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using WAR2D.Dev.DebugApi;

public class DebugDispatcherTests
{
    private DebugRegistry registry;
    private readonly List<string> replies = new List<string>();
    private bool server = true, client = true;

    [SetUp]
    public void SetUp()
    {
        replies.Clear();
        registry = new DebugRegistry();
        registry.Add("echo", DebugRole.Any, "echo x", a => new { x = DebugArgs.Int(a, "x") });
        registry.Add("srv", DebugRole.Server, "server only", a => "ok");
        registry.Add("boom", DebugRole.Any, "throws", a => throw new System.InvalidOperationException("kaput"));
        int polls = 0;
        registry.Add("slow", DebugRole.Any, "waits", a => new DebugWait { Poll = () => ++polls >= 3 ? "done" : null, TimeoutSeconds = 5 });
        registry.Add("never", DebugRole.Any, "times out", a => new DebugWait { Poll = () => null, TimeoutSeconds = 1 });
    }

    private DebugDispatcher Make() => new DebugDispatcher(registry, "tok", () => server, () => client);
    private JObject Last => JObject.Parse(replies[replies.Count - 1]);

    [Test]
    public void RunsACommandAndEchoesTheId()
    {
        Make().Handle("{\"id\":7,\"token\":\"tok\",\"cmd\":\"echo\",\"args\":{\"x\":3}}", replies.Add);
        Assert.AreEqual(7, (int)Last["id"]);
        Assert.IsTrue((bool)Last["ok"]);
        Assert.AreEqual(3, (int)Last["result"]["x"]);
    }

    [Test]
    public void RefusesAWrongToken()
    {
        Make().Handle("{\"id\":1,\"token\":\"bad\",\"cmd\":\"echo\",\"args\":{\"x\":1}}", replies.Add);
        Assert.IsFalse((bool)Last["ok"]);
        StringAssert.Contains("token", (string)Last["error"]);
    }

    [TestCase("not json")]
    [TestCase("{\"id\":1,\"token\":\"tok\"}")]
    [TestCase("{\"id\":1,\"token\":\"tok\",\"cmd\":\"nope\"}")]
    [TestCase("{\"id\":1,\"token\":\"tok\",\"cmd\":\"echo\",\"args\":{\"x\":\"three\"}}")]
    [TestCase("{\"id\":1,\"token\":\"tok\",\"cmd\":\"echo\",\"args\":{}}")]
    public void BadRequestsAreErrorsNotExceptions(string line)
    {
        Assert.DoesNotThrow(() => Make().Handle(line, replies.Add));
        Assert.IsFalse((bool)Last["ok"]);
    }

    [Test]
    public void ServerCommandRefusedOnAClient()
    {
        server = false;
        Make().Handle("{\"id\":1,\"token\":\"tok\",\"cmd\":\"srv\"}", replies.Add);
        StringAssert.Contains("server", (string)Last["error"]);
    }

    [Test]
    public void HandlerExceptionBecomesAnError()
    {
        WAR2D.Dev.Logging.WarLog.ForwardToUnity = false;
        try { Make().Handle("{\"id\":1,\"token\":\"tok\",\"cmd\":\"boom\"}", replies.Add); }
        finally { WAR2D.Dev.Logging.WarLog.ForwardToUnity = true; }
        StringAssert.Contains("kaput", (string)Last["error"]);
    }

    [Test]
    public void WaitsAnswerWhenDoneAndTimeOut()
    {
        DebugDispatcher d = Make();
        d.Handle("{\"id\":1,\"token\":\"tok\",\"cmd\":\"slow\"}", replies.Add);
        d.Handle("{\"id\":2,\"token\":\"tok\",\"cmd\":\"never\"}", replies.Add);
        Assert.AreEqual(0, replies.Count);
        for (int i = 0; i < 3; i++) d.Pump(0.1f);
        Assert.AreEqual("done", (string)JObject.Parse(replies[0])["result"]);
        d.Pump(1f);
        Assert.IsFalse((bool)JObject.Parse(replies[1])["ok"]);
        StringAssert.Contains("timed out", (string)JObject.Parse(replies[1])["error"]);
    }
}
