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
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = (OrderKind)kind, Queue = queue, OwnerId = owner, Tile = goal, Ids = ids.ToArray() });
    }

    /// <summary>Accumulates one chunk of a squad assignment; the final chunk replaces the squad.</summary>
    [Command(requiresAuthority = false)]
    public void CmdAssignSquadChunk(ushort token, byte squad, byte[] idChunk, bool final, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdAssignSquadChunk))) return;
        if (!CommandValidator.IsSquadIndexValid(squad) || !TryActingOwner(sender, out int owner)) return;
        List<int> ids = Accumulate(owner, token, (byte)(SquadKindBase + squad), idChunk, final);
        if (ids == null) return;
        Squads.Assign(owner, squad, ids);
    }

    /// <summary>Gives a squad's living members an order (<see cref="OrderKind"/>), as <see cref="CmdOrderChunk"/>.</summary>
    [Command(requiresAuthority = false)]
    public void CmdOrderSquad(byte squad, byte kind, bool queue, int2 goal, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(CmdOrderSquad))) return;
        if (!CommandValidator.IsSquadIndexValid(squad) || !TryActingOwner(sender, out int owner) || !IsOrderValid(kind, goal)) return;
        Squads.Prune(owner, squad, Ids);
        IReadOnlyList<int> members = Squads.Members(owner, squad);
        if (members.Count == 0) return;
        var ids = new int[members.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = members[i];
        Sim.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = (OrderKind)kind, Queue = queue, OwnerId = owner, Tile = goal, Ids = ids });
    }

    /// <summary>The sender's owner id, when they may give orders now (Playing, still in the match).</summary>
    private bool TryActingOwner(NetworkConnectionToClient sender, out int owner)
    {
        owner = 0;
        if (Sim == null || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return false;
        owner = BuildingData.UIntToInt(sender.identity.netId);
        ServerPlayer acting = GameCore.Instance.GetServerPlayerById(owner);
        return acting != null && acting.state == PlayerState.Playing;
    }

    /// <summary>A known order kind, with a goal inside the map when the order moves.</summary>
    private bool IsOrderValid(byte kind, int2 goal)
    {
        (int2 min, int2 max) = MapBounds;
        return Map != null && CommandValidator.IsOrderValid(kind, goal, min, max);
    }

    /// <summary>
    /// Adds a chunk to the sender's pending list for the token. Returns the whole list on the final
    /// chunk, else null. A malformed chunk, too many ids, or too many open tokens drops the order.
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
        if (room <= 0 || !OrderIdCodec.TryDecode(chunk, System.Math.Min(OrderIdCodec.MaxIdsPerChunk, room), pending.Ids))
        {
            pendingOrders.Remove(key);
            return null;
        }
        if (!final) return null;
        pendingOrders.Remove(key);
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
