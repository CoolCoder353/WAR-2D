using NUnit.Framework;
using WAR2D.Net.Steam;

public class SteamAuthTests
{
    private const ulong Alice = 76561198000000002UL, Mallory = 76561198000000666UL;
    private static SteamAuthRequest Req(ulong id, string ver = "0.9.0", int ticketBytes = 8) =>
        new SteamAuthRequest { version = ver, steamId = id, ticket = new byte[ticketBytes] };

    [Test]
    public void PolicyAcceptsAMatchingRequest() =>
        Assert.IsTrue(SteamAuthPolicy.Check("0.9.0", Req(Alice), Alice, out _));

    [Test]
    public void PolicyRejectsAClaimedIdThatIsNotTheConnectionsId()
    {
        Assert.IsFalse(SteamAuthPolicy.Check("0.9.0", Req(Mallory), Alice, out string reason));
        StringAssert.Contains("identity", reason);
    }

    [TestCase("0.8.0")]
    [TestCase("")]
    public void PolicyRejectsAWrongVersion(string version)
    {
        Assert.IsFalse(SteamAuthPolicy.Check("0.9.0", Req(Alice, version), Alice, out string reason));
        StringAssert.Contains("version", reason);
    }

    [TestCase(0)]
    [TestCase(SteamAuthPolicy.MaxTicketBytes + 1)]
    public void PolicyRejectsEmptyOrHugeTickets(int bytes) =>
        Assert.IsFalse(SteamAuthPolicy.Check("0.9.0", Req(Alice, ticketBytes: bytes), Alice, out _));

    [Test]
    public void PolicyRejectsANullTicket()
    {
        var r = Req(Alice); r.ticket = null;
        Assert.IsFalse(SteamAuthPolicy.Check("0.9.0", r, Alice, out _));
    }

    [Test]
    public void DirectIsOnlyAllowedInTheEditorWithDevApiOrTheFlag()
    {
        Assert.IsFalse(SteamAuthPolicy.DirectAllowed(false, false, new[] { "game" }));
        Assert.IsTrue(SteamAuthPolicy.DirectAllowed(true, false, new string[0]));
        Assert.IsTrue(SteamAuthPolicy.DirectAllowed(false, true, new string[0]));
        Assert.IsTrue(SteamAuthPolicy.DirectAllowed(false, false, new[] { "game", "-direct" }));
    }

    [Test]
    public void ASessionIsAcceptedOnlyWhenSteamConfirmsTheTicket()
    {
        var fake = new FakeSteamBackend();
        var sessions = new SteamAuthSessions(fake, "0.9.0");
        Assert.AreEqual(SteamAuthSessions.Begin.Pending, sessions.OnRequest(5, Req(Alice), Alice, 0, out _));
        Assert.IsEmpty(sessions.Drain(1));
        fake.RaiseAuthResult(Alice, true);
        var done = sessions.Drain(1);
        Assert.AreEqual(1, done.Count);
        Assert.AreEqual((5, true), (done[0].connectionId, done[0].accepted));
    }

    [Test]
    public void ASteamFailureRejectsTheConnection()
    {
        var fake = new FakeSteamBackend();
        var sessions = new SteamAuthSessions(fake, "0.9.0");
        sessions.OnRequest(5, Req(Alice), Alice, 0, out _);
        fake.RaiseAuthResult(Alice, false);
        var done = sessions.Drain(1);
        Assert.IsFalse(done[0].accepted);
        CollectionAssert.Contains(fake.Ended, Alice);
    }

    [Test]
    public void ImmediateBackendRejectionNeverBecomesPending()
    {
        var fake = new FakeSteamBackend { AcceptBegin = false };
        var sessions = new SteamAuthSessions(fake, "0.9.0");
        Assert.AreEqual(SteamAuthSessions.Begin.Rejected, sessions.OnRequest(5, Req(Alice), Alice, 0, out string reason));
        Assert.IsNotEmpty(reason);
    }

    [Test]
    public void ASilentSteamTimesOut()
    {
        var fake = new FakeSteamBackend();
        var sessions = new SteamAuthSessions(fake, "0.9.0", timeoutSeconds: 10);
        sessions.OnRequest(5, Req(Alice), Alice, 0, out _);
        Assert.IsEmpty(sessions.Drain(9));
        var done = sessions.Drain(11);
        Assert.AreEqual(1, done.Count);
        Assert.IsFalse(done[0].accepted);
        CollectionAssert.Contains(fake.Ended, Alice);
    }

    [Test]
    public void ASecondConnectionClaimingTheSameSteamIdIsRejected()
    {
        var sessions = new SteamAuthSessions(new FakeSteamBackend(), "0.9.0");
        sessions.OnRequest(5, Req(Alice), Alice, 0, out _);
        Assert.AreEqual(SteamAuthSessions.Begin.Rejected, sessions.OnRequest(6, Req(Alice), Alice, 0, out _));
    }

    [Test]
    public void AConnectionThatLeftBeforeSteamAnsweredIsNeverAccepted()
    {
        var fake = new FakeSteamBackend();
        var sessions = new SteamAuthSessions(fake, "0.9.0");
        sessions.OnRequest(5, Req(Alice), Alice, 0, out _);
        sessions.Forget(5); // it disconnected
        CollectionAssert.Contains(fake.Ended, Alice, "the session it opened is ended");
        fake.RaiseAuthResult(Alice, true); // Steam answers late
        Assert.IsEmpty(sessions.Drain(1));
        Assert.AreEqual(SteamAuthSessions.Begin.Pending, sessions.OnRequest(6, Req(Alice), Alice, 1, out _), "a retry is not 'already signing in'");
    }

    [Test]
    public void AConnectionThatLeftWithAnUndrainedVerdictIsDroppedAndEnded()
    {
        var fake = new FakeSteamBackend();
        var sessions = new SteamAuthSessions(fake, "0.9.0");
        sessions.OnRequest(5, Req(Alice), Alice, 0, out _);
        fake.RaiseAuthResult(Alice, true);
        sessions.Forget(5); // it disconnected before the authenticator drained the verdict
        Assert.IsEmpty(sessions.Drain(1));
        CollectionAssert.Contains(fake.Ended, Alice);
    }

    [Test]
    public void IdentityIsPerConnectionAndForgotten()
    {
        SteamIdentity.Clear();
        SteamIdentity.Set(7, Alice);
        Assert.IsTrue(SteamIdentity.TryGet(7, out ulong id));
        Assert.AreEqual(Alice, id);
        SteamIdentity.Forget(7);
        Assert.IsFalse(SteamIdentity.TryGet(7, out _));
    }
}
