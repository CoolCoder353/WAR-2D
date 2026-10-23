using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// What a player may know about units and attacks, as one set of predicates with two users (v0.14): the replication
    /// encoder (<see cref="BuildInterestJob"/>, <see cref="EncodeClientJob"/>) and the AI's team view
    /// (<c>TeamViewBuilder</c>). Keeping one copy means the AI can't drift into seeing more than a client of its owner.
    /// Burst-compatible. Buildings follow <see cref="BuildingInterest.MayKnow"/> and terrain <see cref="TerrainInterest"/>.
    /// </summary>
    public static class InterestRules
    {
        /// <summary>A camera box that holds the whole map (an AI has no camera: anything its client could pan to counts).</summary>
        public static readonly int4 WholeMap = new int4(int.MinValue / 2, int.MinValue / 2, int.MaxValue / 2, int.MaxValue / 2);

        /// <summary>True when unit slot <paramref name="slot"/> is in <paramref name="owner"/>'s interest: alive, and its own or on a fog cell <paramref name="grid"/> sees now.</summary>
        public static bool UnitVisible(in ReplicationInput input, int slot, int owner, int grid) =>
            input.Health[slot] > 0f && (input.OwnerId[slot] == owner || input.Sees(grid, input.Positions[slot]));

        /// <summary>
        /// True when the unit at id index <paramref name="index"/> is known: in <paramref name="allowed"/> when given (the
        /// encoder's interest set, snapshot pacing included), else by <see cref="UnitVisible"/> now.
        /// </summary>
        public static bool UnitKnown(in ReplicationInput input, NativeArray<ulong> allowed, int index, int owner, int grid)
        {
            if (allowed.IsCreated) return (uint)index < (uint)(allowed.Length * 64) && BitSet.Get(allowed, index);
            int slot = input.SlotOfIndex(index);
            return slot >= 0 && UnitVisible(input, slot, owner, grid);
        }

        /// <summary>True when a camera box (inclusive tiles: min x, min y, max x, max y) holds <paramref name="p"/>.</summary>
        public static bool InView(int4 view, float2 p) => p.x >= view.x && p.y >= view.y && p.x < view.z + 1 && p.y < view.w + 1;

        /// <summary>True when the player may know a building: it owns it, or its grid sees it now and it isn't hidden.</summary>
        public static bool MayKnowBuilding(in ReplicationInput input, in AttackTarget building, int owner, int grid) =>
            building.Id > 0 && (building.OwnerId == owner || (building.Hidden == 0 && input.Sees(grid, building.Position)));

        /// <summary>
        /// True when the player may know an attack's target: a known unit (<see cref="UnitKnown"/>), or a building it owns or
        /// (unless hidden) its grid sees (<paramref name="building"/>, the target when it is a building). Anything else
        /// would name what the fog hides.
        /// </summary>
        public static bool MayKnowTarget(in ReplicationInput input, NativeArray<ulong> allowed, int target, in AttackTarget building, int owner, int grid)
        {
            if (target <= 0) return false;
            int index = NetIdAllocator.IndexOf(target);
            if ((uint)index < (uint)input.IndexOfId.Length)
            {
                int slot = input.IndexOfId[index];
                if ((uint)slot < (uint)input.Count && input.IdOf[slot] == target) // a unit (alive or killed by this shot)
                    return UnitKnown(input, allowed, index, owner, grid);
            }
            return building.Id == target && MayKnowBuilding(input, building, owner, grid);
        }

        /// <summary>
        /// The attack event a player is told about for a unit's attack (attacker id, target id), or false: the attacker
        /// must be known and inside <paramref name="view"/>; the target is named only when <see cref="MayKnowTarget"/>, else 0.
        /// </summary>
        public static bool UnitAttack(in ReplicationInput input, NativeArray<ulong> allowed, int4 view, int2 attack, in AttackTarget target, int owner, int grid, out AttackEvent shown)
        {
            shown = default;
            int index = NetIdAllocator.IndexOf(attack.x);
            if (!UnitKnown(input, allowed, index, owner, grid)) return false;
            int slot = input.SlotOfIndex(index);
            if (slot < 0 || !InView(view, input.Positions[slot])) return false;
            shown = new AttackEvent { AttackerIndex = index, TargetId = MayKnowTarget(input, allowed, attack.y, target, owner, grid) ? attack.y : 0 };
            return true;
        }

        /// <summary>
        /// The attack event a player is told about for a building's attack (v0.13), or false: only from a building it may
        /// know, inside <paramref name="view"/> unless it is its own (the owner's turret status must not depend on the
        /// camera); the target named as for units (0 for a wall shot or an unknown target).
        /// </summary>
        public static bool BuildingAttack(in ReplicationInput input, NativeArray<ulong> allowed, int4 view, in BuildingAttack attack, int owner, int grid, out AttackEvent shown)
        {
            shown = default;
            if (!MayKnowBuilding(input, attack.Attacker, owner, grid)) return false;
            if (attack.Attacker.OwnerId != owner && !InView(view, attack.Attacker.Position)) return false;
            shown = new AttackEvent
            {
                AttackerKind = AttackerKinds.Building,
                AttackerId = attack.Attacker.Id,
                TargetId = MayKnowTarget(input, allowed, attack.TargetId, attack.Target, owner, grid) ? attack.TargetId : 0,
            };
            return true;
        }
    }
}
