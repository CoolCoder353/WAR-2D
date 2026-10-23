using Mirror;
using UnityEngine;
using WAR2D.Sim;

/// <summary>When the in-match menu may pause the match (v0.14, spec §9).</summary>
public static class PauseRules
{
    /// <summary>
    /// True when a human may pause from the in-match menu: exactly one human is connected (AIs don't count), the asker is
    /// that human, and the match is placing HQs, counting down or playing.
    /// </summary>
    public static bool MayMenuPause(GameState state, int humansConnected, bool askerIsConnectedHuman) =>
        askerIsConnectedHuman && humansConnected == 1 &&
        (state == GameState.PlacingHQ || state == GameState.Countdown || state == GameState.Playing);
}

/// <summary>
/// The match clock (v0.14, server): seconds of unpaused PlacingHQ and Playing time. Every match timer that used to read
/// wall time (diplomacy and gift cooldowns, alert throttles, Radar contacts) reads this, so a pause stops them.
/// </summary>
public sealed class MatchClock
{
    /// <summary>Seconds of unpaused PlacingHQ and Playing time since the match started.</summary>
    public double Now { get; private set; }

    /// <summary>Advances the clock by a frame's time, when the match is placing HQs or playing and not paused.</summary>
    public void Advance(GameState state, bool paused, double deltaSeconds)
    {
        if (paused || deltaSeconds <= 0) return;
        if (state == GameState.PlacingHQ || state == GameState.Playing) Now += deltaSeconds;
    }

    /// <summary>Back to zero (a new match).</summary>
    public void Reset() => Now = 0;
}

/// <summary>
/// Who pauses the match (v0.14, server): the dev pause (debug API) and the one-human menu pause. Either stops the tick
/// (<see cref="SimPause.Paused"/>), the building systems, the AI and the match clock.
/// </summary>
public sealed class MatchPause
{
    /// <summary>The debug API's pause (dev only).</summary>
    public bool Dev;

    /// <summary>The owner whose in-match menu pauses the match, or 0.</summary>
    public int MenuOwner;

    public bool MenuPaused => MenuOwner != 0;

    /// <summary>True while anything pauses the match.</summary>
    public bool Paused => Dev || MenuPaused;

    public void Clear()
    {
        Dev = false;
        MenuOwner = 0;
    }
}

/// <summary>
/// Pause in one-human matches (v0.14, spec §9): while the only connected human has the in-match menu open the match is
/// paused; the countdown, the HQ pick deadline and the match start time move on by the paused time, so clients' timers
/// freeze. With two or more humans the menu never pauses.
/// </summary>
public partial class GameCore
{
    /// <summary>True while the match is paused (menu or dev): clients stop their prediction clock.</summary>
    [SyncVar]
    public bool IsPaused;

    /// <summary>True while the one human's in-match menu pauses the match: the menu shows its Paused state.</summary>
    [SyncVar]
    public bool IsMenuPaused;

    /// <summary>Who pauses the match (server).</summary>
    public MatchPause MatchPause { get; } = new MatchPause();

    /// <summary>Seconds of unpaused PlacingHQ and Playing time (server).</summary>
    public MatchClock MatchClock { get; } = new MatchClock();

    /// <summary>The match clock now, or 0 without a GameCore (server).</summary>
    public static double MatchNow => Instance != null ? Instance.MatchClock.Now : 0;

    private double pausedSince = -1;

    /// <summary>Pauses or resumes the match from the sender's in-match menu (only when they are the only human connected).</summary>
    [Command(requiresAuthority = false)]
    public void CmdSetMenuPause(bool on, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdSetMenuPause)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        TrySetMenuPause(owner, on);
    }

    /// <summary>Sets or clears <paramref name="owner"/>'s menu pause; returns whether the match's pause is now as asked.</summary>
    [Server]
    internal bool TrySetMenuPause(int owner, bool on)
    {
        if (!on)
        {
            if (MatchPause.MenuOwner != owner) return false;
            MatchPause.MenuOwner = 0;
            ApplyPause();
            return true;
        }
        if (!PauseRules.MayMenuPause(CurrentState, HumansConnected(), IsConnectedHuman(owner))) return false;
        MatchPause.MenuOwner = owner;
        ApplyPause();
        return true;
    }

    /// <summary>The debug API's pause and resume (dev only): goes through the same pause as the menu.</summary>
    [Server]
    public void SetDevPause(bool on)
    {
        MatchPause.Dev = on;
        ApplyPause();
    }

    /// <summary>Humans with a connection (AIs never count).</summary>
    [Server]
    private int HumansConnected()
    {
        int n = 0;
        foreach (NetworkIdentity identity in ServerPlayers.Keys) if (identity != null) n++;
        return n;
    }

    [Server]
    private bool IsConnectedHuman(int owner)
    {
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null && BuildingData.UIntToInt(identity.netId) == owner) return true;
        return false;
    }

    /// <summary>Copies the pause to the tick and the synced flags.</summary>
    [Server]
    private void ApplyPause()
    {
        bool paused = MatchPause.Paused;
        SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim != null && sim.Pause.Paused != paused) sim.Pause.Paused = paused;
        IsPaused = paused;
        IsMenuPaused = MatchPause.MenuPaused;
    }

    /// <summary>
    /// Once a frame (server): drops a menu pause that no longer qualifies (a second human, the state moved on), moves the
    /// synced countdown, pick deadline and match start time on by the time spent paused, and advances the match clock.
    /// </summary>
    [Server]
    internal void UpdatePause()
    {
        if (MatchPause.MenuPaused && !PauseRules.MayMenuPause(CurrentState, HumansConnected(), IsConnectedHuman(MatchPause.MenuOwner)))
            MatchPause.MenuOwner = 0;
        if (CurrentState == GameState.Lobby || CurrentState == GameState.GameOver) MatchPause.MenuOwner = 0;
        ApplyPause();

        bool paused = MatchPause.Paused;
        double now = NetworkTime.time;
        if (paused && pausedSince >= 0)
        {
            double shift = now - pausedSince;
            if (CurrentState == GameState.Countdown) CountdownEndTime += shift;
            if (HqPlacementEndTime > 0) HqPlacementEndTime += shift;
            if (CurrentState == GameState.Playing) MatchStartTime += shift;
        }
        pausedSince = paused ? now : -1;
        MatchClock.Advance(CurrentState, paused, Time.deltaTime);
    }

    /// <summary>A new match (or back to the lobby): nothing paused, the clock at zero.</summary>
    [Server]
    private void ResetPause()
    {
        MatchPause.Clear();
        MatchClock.Reset();
        pausedSince = -1;
        IsPaused = false;
        IsMenuPaused = false;
    }
}
