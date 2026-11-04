using System;
using System.Collections.Generic;

/// <summary>What a drag box keeps (v0.18). Pure.</summary>
public static class SelectionRules
{
    /// <summary>
    /// A box that catches Builders and any other unit keeps only the others; a box of nothing but Builders keeps them.
    /// Diggers and Suicide bombers count as army. There is no override key: <c>.</c> and Ctrl + <c>.</c> select Builders, and
    /// a double-click selects those on screen.
    /// </summary>
    public static void PreferArmy(List<int> caught, Func<int, UnitType> typeOf)
    {
        bool army = false;
        foreach (int id in caught)
            if (typeOf(id) != UnitType.Builder) { army = true; break; }
        if (army) caught.RemoveAll(id => typeOf(id) == UnitType.Builder);
    }
}
