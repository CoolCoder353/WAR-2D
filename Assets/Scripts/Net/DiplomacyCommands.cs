using System.Collections.Generic;
using Config;
using Mirror;
using WAR2D.Sim;

/// <summary>
/// Player-controlled diplomacy: each player chooses whom they attack and with whom they share vision,
/// both one-way. Changes apply at the next tick boundary through <see cref="SimContext.Diplomacy"/>; each
/// player learns only their own row (<see cref="ClientPlayer.TargetDiplomacy"/>).
/// </summary>
public partial class GameCore
{
    /// <summary>True when the match lets players change diplomacy (set from the match settings).</summary>
    [SyncVar]
    public bool DiplomacyEnabled;

    private readonly Dictionary<(int owner, SimCommandKind kind), double> lastDiplomacyChange = new Dictionary<(int, SimCommandKind), double>();

    /// <summary>Starts or stops attacking another player (<see cref="PlayerActions.SetAttack"/>).</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetAttack(uint targetNetId, bool on, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetAttack)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        PlayerActions.SetAttack(owner, BuildingData.UIntToInt(targetNetId), on);
    }

    /// <summary>Starts or stops sharing vision with another player (<see cref="PlayerActions.SetShareVision"/>).</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetShareVision(uint targetNetId, bool on, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetShareVision)) || !WorldStateManager.TryOwnerOf(sender, out int owner)) return;
        PlayerActions.SetShareVision(owner, BuildingData.UIntToInt(targetNetId), on);
    }

    /// <summary>
    /// Queues a diplomacy change for <paramref name="owner"/> when the rules allow it now (match setting, state,
    /// both players live, not oneself, the per-command cooldown). Returns whether it was queued.
    /// </summary>
    [Server]
    internal bool TryChangeDiplomacy(int owner, int target, bool on, SimCommandKind kind)
    {
        SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim == null) return false;
        double now = NetworkTime.localTime;
        if (!lastDiplomacyChange.TryGetValue((owner, kind), out double last)) last = double.NegativeInfinity;
        float cooldown = ConfigLoader.LoadConfig().Diplomacy.ChangeCooldownSeconds;
        if (!DiplomacyRules.CanChange(DiplomacyEnabled, CurrentState, GetServerPlayerById(owner)?.state,
                                      GetServerPlayerById(target)?.state, owner, target, now, last, cooldown)) return false;
        lastDiplomacyChange[(owner, kind)] = now;
        sim.Commands.Enqueue(new SimCommand { Kind = kind, OwnerId = owner, TargetOwnerId = target, Flag = on });
        return true;
    }

    /// <summary>Sends every player their own diplomacy row (match start).</summary>
    [Server]
    public void SendAllDiplomacy()
    {
        SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim == null) return;
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null) sim.SlotOf(BuildingData.UIntToInt(identity.netId)); // every player starts from the teams
        foreach (int ai in AiPlayers.Keys) sim.SlotOf(ai);
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null) SendDiplomacy(BuildingData.UIntToInt(identity.netId));
    }

    /// <summary>
    /// Sends one player their own row: whom they attack, whom they share vision with, and who shares
    /// vision with them. Bit i is the owner at <see cref="PlayerOrder"/>[i]. Never another player's row.
    /// </summary>
    [Server]
    public void SendDiplomacy(int ownerId)
    {
        SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim == null || !sim.TrySlotOf(ownerId, out int slot)) return;
        NetworkIdentity identity = null;
        foreach (NetworkIdentity candidate in ServerPlayers.Keys)
            if (candidate != null && candidate.netId == (uint)ownerId) identity = candidate;
        if (identity == null || identity.connectionToClient == null) return;

        var slotByIndex = new List<int>(PlayerOrder.Count);
        foreach (int other in PlayerOrder) slotByIndex.Add(sim.TrySlotOf(other, out int s) ? s : -1);
        (ushort attack, ushort share, ushort sharedWithMe) = sim.Diplomacy.RowFor(slot, slotByIndex);
        identity.GetComponent<ClientPlayer>().TargetDiplomacy(identity.connectionToClient, attack, share, sharedWithMe);
    }

    [Server]
    private void ForgetDiplomacyCooldowns() => lastDiplomacyChange.Clear();
}
