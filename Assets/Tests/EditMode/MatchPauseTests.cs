using NUnit.Framework;

/// <summary>v0.14 plan Task 9: the one-human pause rule, the pause sources and the match clock.</summary>
public class MatchPauseTests
{
    [Test]
    public void OnlyTheOneConnectedHumanMayPauseWhileTheMatchRuns()
    {
        Assert.That(PauseRules.MayMenuPause(GameState.Playing, 1, true), Is.True);
        Assert.That(PauseRules.MayMenuPause(GameState.PlacingHQ, 1, true), Is.True);
        Assert.That(PauseRules.MayMenuPause(GameState.Countdown, 1, true), Is.True);
        Assert.That(PauseRules.MayMenuPause(GameState.Playing, 2, true), Is.False, "two humans: never");
        Assert.That(PauseRules.MayMenuPause(GameState.Playing, 1, false), Is.False, "not someone else (an AI, a made-up id)");
        Assert.That(PauseRules.MayMenuPause(GameState.Lobby, 1, true), Is.False);
        Assert.That(PauseRules.MayMenuPause(GameState.GameOver, 1, true), Is.False);
    }

    [Test]
    public void EitherSourcePausesAndBothMustLetGo()
    {
        var pause = new MatchPause();
        Assert.That(pause.Paused, Is.False);
        pause.Dev = true;
        pause.MenuOwner = 5;
        Assert.That(pause.Paused, Is.True);
        pause.Dev = false;
        Assert.That(pause.Paused, Is.True, "the menu still pauses");
        pause.MenuOwner = 0;
        Assert.That(pause.Paused, Is.False);
        pause.Dev = true;
        pause.Clear();
        Assert.That(pause.Paused, Is.False);
    }

    [Test]
    public void TheMatchClockCountsUnpausedPlacingAndPlayingTimeOnly()
    {
        var clock = new MatchClock();
        clock.Advance(GameState.Lobby, false, 1.0);
        clock.Advance(GameState.PlacingHQ, false, 2.0);
        clock.Advance(GameState.Countdown, false, 3.0);
        clock.Advance(GameState.Playing, false, 4.0);
        clock.Advance(GameState.Playing, true, 100.0);
        clock.Advance(GameState.GameOver, false, 5.0);
        clock.Advance(GameState.Playing, false, -1.0);
        Assert.That(clock.Now, Is.EqualTo(6.0));
        clock.Reset();
        Assert.That(clock.Now, Is.EqualTo(0.0));
    }
}
