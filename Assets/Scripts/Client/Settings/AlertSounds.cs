using System;
using System.Collections.Generic;

/// <summary>
/// The sound each <see cref="AlertKind"/> plays (Figma 128:1453, sounds built from <c>Assets/Art/Audio/sfx/</c>
/// by <c>WAR-2D/Audio/Build SFX</c>) and the one sound a batch of alerts plays, honouring Settings › Gameplay ›
/// Alerts › Sound (<see cref="GameSettings.SoundsAlert"/>).
/// </summary>
public static class AlertSounds
{
    /// <summary>The generic alert sound, for kinds without their own.</summary>
    public const string Default = "alert";

    /// <summary>The sound id (an <c>Audio/Sfx</c> clip) of an alert kind; a finished Jammer hums instead of chiming.</summary>
    public static string SoundOf(AlertKind kind, bool jammer = false) => kind switch
    {
        AlertKind.UnderAttack => "alert_under_attack",
        AlertKind.BombTriggered => "alert_bomb",
        AlertKind.EnemySpotted => "radar_ping",
        AlertKind.ConstructionFinished => jammer ? "jammer_online" : "construction_finished",
        AlertKind.UpkeepUnpaid => "alert_upkeep",
        AlertKind.GiftReceived => "alert_gift",
        AlertKind.ResourcesLow => "alert_resources_low",
        _ => Default,
    };

    /// <summary>Which kind's sound wins when one batch holds several (lower plays first): the ones that lose games lead.</summary>
    public static int Priority(AlertKind kind) => kind switch
    {
        AlertKind.UnderAttack => 0,
        AlertKind.UpkeepUnpaid => 1,
        AlertKind.BombTriggered => 2,
        AlertKind.ResourcesLow => 3,
        AlertKind.EnemySpotted => 4,
        AlertKind.RepairsStopped => 5,
        AlertKind.ConstructionFinished => 6,
        AlertKind.GiftReceived => 7,
        _ => 8,
    };

    /// <summary>
    /// The one sound a batch of alerts plays: the highest-priority kind whose Sound switch is on, or null when
    /// every kind in it is muted. <paramref name="isJammer"/> says whether a Construction finished alert is a Jammer.
    /// </summary>
    public static string ForBatch(IReadOnlyList<Alert> alerts, GameSettings settings, Func<Alert, bool> isJammer = null)
    {
        if (alerts == null) return null;
        string best = null;
        int bestPriority = int.MaxValue;
        foreach (Alert alert in alerts)
        {
            if (settings != null && !settings.SoundsAlert(alert.Kind)) continue;
            int priority = Priority(alert.Kind);
            if (priority >= bestPriority) continue;
            bestPriority = priority;
            best = SoundOf(alert.Kind, alert.Kind == AlertKind.ConstructionFinished && isJammer != null && isJammer(alert));
        }
        return best;
    }
}
