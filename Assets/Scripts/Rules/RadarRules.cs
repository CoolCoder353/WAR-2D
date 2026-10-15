/// <summary>One Radar's contact state for its pings.</summary>
public struct RadarWatch
{
    /// <summary>True from a contact until the radius has been clear for the clear time.</summary>
    public bool Contact;
    /// <summary>When the radius last became clear (valid while Contact and no enemy is in range).</summary>
    public double ClearSince;
    /// <summary>When it last pinged.</summary>
    public double LastPing;
    /// <summary>True once it has pinged (LastPing is valid).</summary>
    public bool Pinged;
    /// <summary>True while the last step saw no enemy.</summary>
    public bool WasClear;
}

/// <summary>Radar ping rules (v0.7). Pure.</summary>
public static class RadarRules
{
    /// <summary>
    /// Advances one Radar by one observation. A contact starts when an enemy is in range and no contact is
    /// open; it pings then if the cooldown since the last ping has passed. A contact ends only after the
    /// radius has been clear for <paramref name="clearSeconds"/>. True when this step pings.
    /// </summary>
    public static bool Step(ref RadarWatch watch, bool enemyInRange, double now, float clearSeconds, float cooldownSeconds)
    {
        if (!enemyInRange)
        {
            if (!watch.WasClear) watch.ClearSince = now;
            watch.WasClear = true;
            if (watch.Contact && now - watch.ClearSince >= clearSeconds) watch.Contact = false;
            return false;
        }
        watch.WasClear = false;
        if (watch.Contact) return false;
        watch.Contact = true;
        if (watch.Pinged && now - watch.LastPing < cooldownSeconds) return false;
        watch.Pinged = true;
        watch.LastPing = now;
        return true;
    }
}
