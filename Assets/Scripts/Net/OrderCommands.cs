using System.Collections.Generic;
using Config;
using Mirror;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>
/// Order commands with unlimited selections: a client sends its selected ids in chunks under one token
/// (<see cref="OrderIdCodec"/>), and the last chunk carries <c>final</c>. Squads are server-side lists
/// a client fills the same way and orders by number.
/// </summary>
public partial class WorldStateManager
{
    private const int MaxOpenTokens = 4;
    private const double TokenTimeoutSeconds = 2.0;

    /// <summary>Each player's squads (server only).</summary>
    public Squads Squads { get; } = new Squads();

    private sealed class PendingOrder
    {
        public readonly List<int> Ids = new List<int>();
        public double Started;
    }

    private readonly Dictionary<(int owner, ushort token, byte kind), PendingOrder> pendingOrders = new Dictionary<(int, ushort, byte), PendingOrder>();
    private readonly List<(int, ushort, byte)> staleTokens = new List<(int, ushort, byte)>();
    private const byte OrderChunkKind = 0;
    private const byte SquadKindBase = 1; // + squad index
    private const byte DigAreaChunkKind = 32; // past the squads

    /// <summary>
    /// Accumulates one chunk of an order (<see cref="OrderKind"/>); the final chunk queues it. The goal
    /// is used (and validated) only for Move and AttackMove; <paramref name="queue"/> (Shift) queues
    /// those after each unit's current order.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdOrderChunk(ushort token, byte[] idChunk, bool final, byte kind, bool queue, int2 goal, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdOrderChunk))) return;
        if (!TryActingOwner(sender, out int owner) || !IsOrderValid(kind, goal)) return;
        List<int> ids = Accumulate(owner, token, OrderChunkKind, idChunk, final);
        if (ids == null) return;
        PlayerActions.Order(owner, ids, (OrderKind)kind, queue, goal);
    }

    /// <summary>
    /// Accumulates one chunk of a Dig drag; the final chunk gives the listed Diggers a dig area over the box
    /// (clamped to the map's inside and to <c>Orders/MaxDigArea</c> tiles a side). <paramref name="queue"/> (Shift)
    /// queues it after each Digger's current orders.
    /// </summary>
    [Command(requiresAuthority = false)]
    public void CmdDigAreaChunk(ushort token, byte[] idChunk, bool final, bool queue, int2 from, int2 to, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdDigAreaChunk))) return;
        if (!TryActingOwner(sender, out int owner) || Map == null) return;
        (int2 min, int2 max) = MapBounds;
        if (!CommandValidator.TryDigArea(from, to, min, max, ConfigLoader.LoadConfig().Orders.MaxDigArea, out int2 lo, out int2 hi)) return;
        List<int> ids = Accumulate(owner, token, DigAreaChunkKind, idChunk, final);
        if (ids == null) return;
        PlayerActions.DigArea(owner, ids, queue, lo, hi);
    }

    /// <summary>Accumulates one chunk of a squad assignment; the final chunk replaces the squad.</summary>
    [Command(requiresAuthority = false)]
    public void CmdAssignSquadChunk(ushort token, byte squad, byte[] idChunk, bool final, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdAssignSquadChunk))) return;
        if (!CommandValidator.IsSquadIndexValid(squad) || !TryActingOwner(sender, out int owner)) return;
        List<int> ids = Accumulate(owner, token, (byte)(SquadKindBase + squad), idChunk, final);
        if (ids == null) return;
        PlayerActions.AssignSquad(owner, squad, ids);
    }

    /// <summary>Gives a squad's living members an order (<see cref="OrderKind"/>), as <see cref="CmdOrderChunk"/>.</summary>
    [Command(requiresAuthority = false)]
    public void CmdOrderSquad(byte squad, byte kind, bool queue, int2 goal, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdOrderSquad))) return;
        if (!CommandValidator.IsSquadIndexValid(squad) || !TryActingOwner(sender, out int owner) || !IsOrderValid(kind, goal)) return;
        PlayerActions.OrderSquad(owner, squad, (OrderKind)kind, queue, goal);
    }

    private const int SquadCountTicks = 40; // 2 s at 20 Hz
    private bool squadCountsDirty;
    private readonly List<int> squadOwners = new List<int>();

    /// <summary>
    /// On the settled world, after an assignment or every 2 s: prunes each player's squads to their own
    /// live units and sends each player (only) their counts.
    /// </summary>
    [Server]
    private void SendSquadCounts(WAR2D.Sim.SimData data, int tick, int unitCount)
    {
        if (!squadCountsDirty && tick % SquadCountTicks != 0) return;
        squadCountsDirty = false;
        squadOwners.Clear();
        squadOwners.AddRange(Squads.Owners);
        foreach (int owner in squadOwners)
        {
            NetworkConnectionToClient connection = GameCore.Instance?.GetServerPlayerById(owner)?.connection;
            if (connection?.identity == null || !connection.identity.TryGetComponent(out ClientPlayer player)) continue;
            var counts = new int[Squads.Count];
            for (int squad = 0; squad < counts.Length; squad++)
                counts[squad] = Squads.PruneToOwned(owner, squad, id =>
                {
                    int slot = data.SlotOf(id, unitCount);
                    return slot >= 0 && data.OwnerId[slot] == owner && data.Health[slot] > 0f;
                });
            player.TargetSquadCounts(connection, counts);
        }
    }

    /// <summary>The sender's owner id, when they may give orders now (Playing, still in the match).</summary>
    private bool TryActingOwner(NetworkConnectionToClient sender, out int owner) => TryOwnerOf(sender, out owner) && MayAct(owner);

    /// <summary>The sender's owner id (its player object's <c>netId</c>), when it has one.</summary>
    internal static bool TryOwnerOf(NetworkConnectionToClient sender, out int owner)
    {
        owner = 0;
        if (sender?.identity == null) return false;
        owner = BuildingData.UIntToInt(sender.identity.netId);
        return true;
    }

    /// <summary>True when the owner (human or AI) may act on the world now: Playing, and still in the match.</summary>
    internal bool MayAct(int owner)
    {
        if (Sim == null || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return false;
        ServerPlayer acting = GameCore.Instance.GetServerPlayerById(owner);
        return acting != null && acting.state == PlayerState.Playing;
    }

    /// <summary>Marks the squad counts for sending at the next settled boundary.</summary>
    internal void MarkSquadCountsDirty() => squadCountsDirty = true;

    /// <summary>A known order kind, with a goal inside the map when the order moves.</summary>
    internal bool IsOrderValid(byte kind, int2 goal)
    {
        (int2 min, int2 max) = MapBounds;
        return Map != null && CommandValidator.IsOrderValid(kind, goal, min, max);
    }

    /// <summary>
    /// Adds a chunk to the sender's pending list for the token. Returns the whole list on the final
    /// chunk (sorted, without duplicates), else null. A malformed chunk, too many ids, or too many open tokens drops the order. An empty
    /// chunk adds no ids: Ctrl+number with nothing selected sends one, and it empties the squad.
    /// </summary>
    private List<int> Accumulate(int owner, ushort token, byte kind, byte[] chunk, bool final)
    {
        double now = NetworkTime.localTime;
        DropStaleTokens(now);
        var key = (owner, token, kind);
        if (!pendingOrders.TryGetValue(key, out PendingOrder pending))
        {
            int open = 0;
            foreach (var k in pendingOrders.Keys) if (k.owner == owner) open++;
            if (open >= MaxOpenTokens) return null;
            pending = new PendingOrder { Started = now };
            pendingOrders[key] = pending;
        }
        int cap = ConfigLoader.LoadConfig().Simulation.MaxUnitsPerPlayer;
        int room = cap - pending.Ids.Count;
        bool empty = chunk != null && chunk.Length == 0;
        if (chunk == null || (!empty && (room <= 0 || !OrderIdCodec.TryDecode(chunk, System.Math.Min(OrderIdCodec.MaxIdsPerChunk, room), pending.Ids))))
        {
            pendingOrders.Remove(key);
            return null;
        }
        if (!final) return null;
        pendingOrders.Remove(key);
        if (pending.Ids.Count > 1)
        {
            pending.Ids.Sort();
            int kept = 1;
            for (int i = 1; i < pending.Ids.Count; i++) if (pending.Ids[i] != pending.Ids[kept - 1]) pending.Ids[kept++] = pending.Ids[i];
            pending.Ids.RemoveRange(kept, pending.Ids.Count - kept);
        }
        return pending.Ids;
    }

    private void DropStaleTokens(double now)
    {
        staleTokens.Clear();
        foreach (var pair in pendingOrders) if (now - pair.Value.Started > TokenTimeoutSeconds) staleTokens.Add(pair.Key);
        foreach (var key in staleTokens) pendingOrders.Remove(key);
    }

    private void ForgetOrderTokens(int owner)
    {
        staleTokens.Clear();
        foreach (var key in pendingOrders.Keys) if (key.owner == owner) staleTokens.Add(key);
        foreach (var key in staleTokens) pendingOrders.Remove(key);
    }
}
