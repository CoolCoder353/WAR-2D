using System;
using Unity.Collections;
using WAR2D.Sim;

/// <summary>Hidden bomb rules (v0.7). Pure.</summary>
public static class BombRules
{
    /// <summary>True when a unit of <paramref name="unitOwnerSlot"/> sets off a bomb of <paramref name="bombOwnerSlot"/>: the bomb's owner attacks it.</summary>
    public static bool Triggers(NativeArray<ushort> attackMask, int bombOwnerSlot, int unitOwnerSlot) =>
        bombOwnerSlot != unitOwnerSlot && SimData.Attacks(attackMask, bombOwnerSlot, unitOwnerSlot);

    /// <summary>True when a client of <paramref name="viewer"/> may know <paramref name="owner"/>'s bombs: its own, or the owner shares vision with it.</summary>
    public static bool MayKnow(int viewer, int owner, Func<int, int, bool> sharesVisionWith) =>
        viewer == owner || (sharesVisionWith != null && sharesVisionWith(owner, viewer));
}
