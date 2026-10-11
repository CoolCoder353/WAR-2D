using NUnit.Framework;
using WAR2D.UI;

/// <summary>When the "host left the match" dialog shows.</summary>
public class HostLeftNoticeTests
{
    [Test]
    public void OnlyAJoinedPlayerDroppedMidMatchIsTold()
    {
        Assert.IsTrue(HostLeftNotice.ShouldNotify(false, false, GameState.Playing));
        Assert.IsTrue(HostLeftNotice.ShouldNotify(false, false, GameState.PlacingHQ));
        Assert.IsTrue(HostLeftNotice.ShouldNotify(false, false, GameState.Countdown));
        Assert.IsFalse(HostLeftNotice.ShouldNotify(true, false, GameState.Playing), "the host itself");
        Assert.IsFalse(HostLeftNotice.ShouldNotify(false, true, GameState.Playing), "left on purpose");
        Assert.IsFalse(HostLeftNotice.ShouldNotify(false, false, GameState.Lobby));
        Assert.IsFalse(HostLeftNotice.ShouldNotify(false, false, GameState.GameOver), "the match was already over");
    }

    [Test]
    public void LeavingOnPurposeIsUsedOnce()
    {
        HostLeftNotice.LeavingOnPurpose = true;
        HostLeftNotice.ClientStopped(false, GameState.Playing);
        Assert.IsFalse(HostLeftNotice.Pending);
        Assert.IsFalse(HostLeftNotice.LeavingOnPurpose, "reset for the next match");
        HostLeftNotice.ClientStopped(false, GameState.Playing);
        Assert.IsTrue(HostLeftNotice.Pending);
        HostLeftNotice.ClientStopped(true, GameState.Lobby); // clears it again
        Assert.IsFalse(HostLeftNotice.Pending);
    }
}
