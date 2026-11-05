using Unity.Collections;
using Unity.Mathematics;
using WAR2D.Sim;

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
        /// A wall target (negative, <see cref="AttackTargetIds"/>) is kept only for the attacker's owner, and only while its grid sees the tile.
        /// </summary>
        public static bool UnitAttack(in ReplicationInput input, NativeArray<ulong> allowed, int4 view, int2 attack, in AttackTarget target, int owner, int grid, out AttackEvent shown)
        {
            shown = default;
            int index = NetIdAllocator.IndexOf(attack.x);
            if (!UnitKnown(input, allowed, index, owner, grid)) return false;
            int slot = input.SlotOfIndex(index);
            if (slot < 0 || !InView(view, input.Positions[slot])) return false;
            int named = attack.y < 0
                ? (input.OwnerId[slot] == owner && SeesWall(input, attack.y, grid) ? attack.y : 0) // a wall tile (v0.16): only the shooter's owner, only in sight
                : MayKnowTarget(input, allowed, attack.y, target, owner, grid) ? attack.y : 0;
            shown = new AttackEvent { AttackerIndex = index, TargetId = named };
            return true;
        }

        /// <summary>
        /// The shell a player is told about when it is fired, or false: as for an attack event the gun must be known, and inside
        /// <paramref name="view"/> (or its landing point must be: a gun off screen can shell what the camera shows); and the landing point
        /// could give away what it is aimed at in the fog, so only the gun's owner or a grid that sees that point learns it.
        /// </summary>
        public static bool ShellFired(in ReplicationInput input, NativeArray<ulong> allowed, int4 view, in Shell shell, int owner, int grid, out ShellEvent shown)
        {
            shown = default;
            int index = NetIdAllocator.IndexOf(shell.AttackerId);
            if (!UnitKnown(input, allowed, index, owner, grid)) return false;
            int slot = input.SlotOfIndex(index);
            if (slot < 0 || (!InView(view, input.Positions[slot]) && !InView(view, shell.Landing))) return false;
            if (shell.AttackerOwner != owner && !input.Sees(grid, shell.Landing)) return false;
            shown = new ShellEvent { AttackerIndex = index, X = Quantise.Encode(shell.Landing.x), Y = Quantise.Encode(shell.Landing.y), FlightTicks = shell.LandTick - shell.FiredTick };
            return true;
        }

        /// <summary>
        /// True when a player is told a shell came down: inside <paramref name="view"/>, and its own shell or on a point its grid sees now. It says
        /// nothing of the gun, so a player under fire from the fog sees the explosion and no more.
        /// </summary>
        public static bool ShellLanded(in ReplicationInput input, int4 view, in ShellLanding landing, int owner, int grid) =>
            InView(view, landing.Point) && (landing.OwnerId == owner || input.Sees(grid, landing.Point));

        /// <summary>True when the player's grid sees the wall tile a negative attack target names (v0.16).</summary>
        private static bool SeesWall(in ReplicationInput input, int target, int grid)
        {
            if (input.MapWidth <= 0) return false;
            int tile = AttackTargetIds.WallTile(target);
            return input.Sees(grid, new float2(tile % input.MapWidth + 0.5f, tile / input.MapWidth + 0.5f));
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
