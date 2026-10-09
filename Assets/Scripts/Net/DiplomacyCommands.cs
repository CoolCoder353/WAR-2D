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

    /// <summary>Starts or stops attacking another player.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetAttack(uint targetNetId, bool on, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetAttack))) return;
        TryChangeDiplomacy(sender, targetNetId, on, SimCommandKind.SetAttack);
    }

    /// <summary>Starts or stops sharing vision with another player.</summary>
    [Command(requiresAuthority = false)]
    public void Cmd_SetShareVision(uint targetNetId, bool on, NetworkConnectionToClient sender = null)
    {
        if (!CommandGate.Allow(sender, nameof(Cmd_SetShareVision))) return;
        TryChangeDiplomacy(sender, targetNetId, on, SimCommandKind.SetShareVision);
    }

    [Server]
    private void TryChangeDiplomacy(NetworkConnectionToClient sender, uint targetNetId, bool on, SimCommandKind kind)
    {
        SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim == null) return;
        int owner = BuildingData.UIntToInt(sender.identity.netId);
        int target = BuildingData.UIntToInt(targetNetId);
        double now = NetworkTime.localTime;
        if (!lastDiplomacyChange.TryGetValue((owner, kind), out double last)) last = double.NegativeInfinity;
        float cooldown = ConfigLoader.LoadConfig().Diplomacy.ChangeCooldownSeconds;
        if (!DiplomacyRules.CanChange(DiplomacyEnabled, CurrentState, GetServerPlayerById(owner)?.state,
                                      GetServerPlayerById(target)?.state, owner, target, now, last, cooldown)) return;
        lastDiplomacyChange[(owner, kind)] = now;
        sim.Commands.Enqueue(new SimCommand { Kind = kind, OwnerId = owner, TargetOwnerId = target, Flag = on });
    }

    /// <summary>Sends every player their own diplomacy row (match start).</summary>
    [Server]
    public void SendAllDiplomacy()
    {
        SimContext sim = WorldStateManager.Instance != null ? WorldStateManager.Instance.Sim : null;
        if (sim == null) return;
        foreach (NetworkIdentity identity in ServerPlayers.Keys)
            if (identity != null) sim.SlotOf(BuildingData.UIntToInt(identity.netId)); // every player starts from the teams
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

        ushort attack = 0, share = 0, sharedWithMe = 0;
        for (int i = 0; i < PlayerOrder.Count && i < 16; i++)
        {
            if (!sim.TrySlotOf(PlayerOrder[i], out int other) || other == slot) continue;
            if (sim.Diplomacy.Attacks(slot, other)) attack |= (ushort)(1 << i);
            if (sim.Diplomacy.SharesVisionWith(slot, other)) share |= (ushort)(1 << i);
            if (sim.Diplomacy.SharesVisionWith(other, slot)) sharedWithMe |= (ushort)(1 << i);
        }
        identity.GetComponent<ClientPlayer>().TargetDiplomacy(identity.connectionToClient, attack, share, sharedWithMe);
    }

    [Server]
    private void ForgetDiplomacyCooldowns() => lastDiplomacyChange.Clear();
}
