using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Map pings (playtest 2026-11-04): who hears one, how often one may be sent, and which middle-button press is one. Pure.</summary>
public static class PingRules
{
    /// <summary>Most pixels the pointer may move during a middle click; further is a camera drag.</summary>
    public const float ClickPixels = 6f;

    /// <summary>Most seconds a middle click may be held; longer is a camera drag that hasn't moved yet.</summary>
    public const float ClickSeconds = 0.4f;

    /// <summary>True when a middle-button press and release was a click, not a drag of the camera.</summary>
    public static bool IsClick(Vector2 pressed, Vector2 released, float heldSeconds) =>
        heldSeconds <= ClickSeconds && (released - pressed).sqrMagnitude <= ClickPixels * ClickPixels;

    /// <summary>
    /// Fills <paramref name="to"/> with who hears a ping: its sender and everyone the sender shares vision with. It follows
    /// vision's direction, because a ping tells its hearers what the sender is looking at, as shared vision does.
    /// </summary>
    public static void Recipients(int sender, IEnumerable<int> owners, Func<int, int, bool> sharesVisionWith, List<int> to)
    {
        to.Clear();
        to.Add(sender);
        foreach (int owner in owners)
            if (owner != sender && sharesVisionWith(sender, owner)) to.Add(owner);
    }

    /// <summary>The least time between one player's pings.</summary>
    public sealed class Cooldown
    {
        private readonly float seconds;
        private readonly Dictionary<int, double> last = new Dictionary<int, double>();

        public Cooldown(float seconds) => this.seconds = seconds;

        /// <summary>True when the owner may ping now, and starts its wait.</summary>
        public bool Allow(int owner, double now)
        {
            if (last.TryGetValue(owner, out double then) && now - then < seconds) return false;
            last[owner] = now;
            return true;
        }
    }
}
