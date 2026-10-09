using System.Collections.Generic;

namespace WAR2D.Sim
{
    /// <summary>
    /// Who attacks whom and who shares vision with whom, per owner slot. Bit <c>t</c> of
    /// <see cref="AttackMask"/>(s) means slot s attacks slot t; likewise <see cref="ShareVisionMask"/>(s)
    /// means s shares its vision with t. Both are one-way. Authoritative on the main thread; copied into
    /// <see cref="SimData.AttackMask"/> and <see cref="SimData.ShareVisionMask"/> at the tick boundary when dirty.
    /// </summary>
    public sealed class Diplomacy
    {
        private readonly ushort[] attack = new ushort[SimData.MaxOwners];
        private readonly ushort[] share = new ushort[SimData.MaxOwners];

        /// <summary>True when a mask changed since the last <see cref="CopyTo"/>.</summary>
        public bool Dirty { get; private set; }

        /// <summary>The slots this slot attacks.</summary>
        public ushort AttackMask(int slot) => Valid(slot) ? attack[slot] : (ushort)0;

        /// <summary>The slots this slot shares its vision with.</summary>
        public ushort ShareVisionMask(int slot) => Valid(slot) ? share[slot] : (ushort)0;

        /// <summary>The slots that share their vision with this slot.</summary>
        public ushort SharedWith(int slot)
        {
            if (!Valid(slot)) return 0;
            ushort mask = 0;
            for (int s = 0; s < SimData.MaxOwners; s++)
                if ((share[s] >> slot & 1) != 0) mask |= (ushort)(1 << s);
            return mask;
        }

        public bool Attacks(int fromSlot, int toSlot) => Valid(fromSlot) && Valid(toSlot) && (attack[fromSlot] >> toSlot & 1) != 0;

        public bool SharesVisionWith(int fromSlot, int toSlot) => Valid(fromSlot) && Valid(toSlot) && (share[fromSlot] >> toSlot & 1) != 0;

        /// <summary>
        /// The starting state of a new slot against every other: the same team means neither attacks and
        /// both share vision; a different team means both attack and neither shares.
        /// </summary>
        public void Join(int slot, int team, IReadOnlyList<(int slot, int team)> others)
        {
            if (!Valid(slot)) return;
            foreach ((int other, int otherTeam) in others)
            {
                if (other == slot || !Valid(other)) continue;
                bool allies = otherTeam == team;
                Set(attack, slot, other, !allies);
                Set(attack, other, slot, !allies);
                Set(share, slot, other, allies);
                Set(share, other, slot, allies);
            }
        }

        public void SetAttack(int fromSlot, int toSlot, bool on)
        {
            if (Valid(fromSlot) && Valid(toSlot) && fromSlot != toSlot) Set(attack, fromSlot, toSlot, on);
        }

        public void SetShareVision(int fromSlot, int toSlot, bool on)
        {
            if (Valid(fromSlot) && Valid(toSlot) && fromSlot != toSlot) Set(share, fromSlot, toSlot, on);
        }

        /// <summary>Clears the slot's row and column: it neither attacks nor is attacked, and shares with nobody.</summary>
        public void Leave(int slot)
        {
            if (!Valid(slot)) return;
            for (int s = 0; s < SimData.MaxOwners; s++)
            {
                Set(attack, s, slot, false);
                Set(share, s, slot, false);
            }
            if (attack[slot] != 0 || share[slot] != 0) Dirty = true;
            attack[slot] = 0;
            share[slot] = 0;
        }

        /// <summary>Writes the masks into the sim's arrays and clears <see cref="Dirty"/>. Boundary only.</summary>
        public void CopyTo(SimData data)
        {
            for (int s = 0; s < SimData.MaxOwners; s++)
            {
                data.AttackMask[s] = attack[s];
                data.ShareVisionMask[s] = share[s];
            }
            Dirty = false;
        }

        private static bool Valid(int slot) => (uint)slot < SimData.MaxOwners;

        private void Set(ushort[] masks, int from, int to, bool on)
        {
            ushort before = masks[from];
            masks[from] = on ? (ushort)(before | (1 << to)) : (ushort)(before & ~(1 << to));
            if (masks[from] != before) Dirty = true;
        }
    }
}
