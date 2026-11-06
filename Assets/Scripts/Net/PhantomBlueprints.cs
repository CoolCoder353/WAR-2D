using System.Collections.Generic;
using Config;
using Mirror;
using Unity.Mathematics;
using WAR2D.Net.Replication;

/// <summary>
/// Blueprints placed on explored ground where something the placer can't see stands (owner playtest 2026-11-06). The
/// placement is judged by what the player knows (<see cref="WorldStateManager.CheckPlacement(BuildingType, int2, float, int, out bool)"/>),
/// so it never says what is in the fog: the player pays and sees a blueprint like any other, and the server keeps it off
/// the map. At each vision pass a waiting blueprint whose ground has come free becomes a real one (same id, so nothing
/// changes for the client); one whose spot the owner's side now sees taken is cancelled, refunded in full, and the owner
/// told (<see cref="AlertKind.BlueprintBlocked"/>). Only the owner is ever sent it (<see cref="BuildingView.OwnerOnly"/>).
/// </summary>
public partial class WorldStateManager
{
    /// <summary>A blueprint waiting on the server for its spot to be seen.</summary>
    private struct Phantom
    {
        public int Owner;
        public BuildingType Type;
        public int2 Anchor;
        public float Rotation;
        public float Paid;
    }

    private readonly Dictionary<int, Phantom> phantoms = new Dictionary<int, Phantom>();
    private readonly List<int> phantomScratch = new List<int>();

    /// <summary>Each footprint tile of a building on the map, by owner (an own blueprint in the fog is always known to its owner).</summary>
    private readonly Dictionary<int2, int> footprintOwners = new Dictionary<int2, int>();

    /// <summary>How many blueprints are waiting for their spot to be seen (tests and the debug API).</summary>
    internal int PhantomCount => phantoms.Count;

    /// <summary>True when <paramref name="id"/> is one of <paramref name="owner"/>'s waiting blueprints.</summary>
    internal bool IsPhantom(int id, int owner) => phantoms.TryGetValue(id, out Phantom p) && p.Owner == owner;

    private void AddFootprint(int id, int owner, List<int2> tiles)
    {
        buildingFootprints[id] = tiles;
        foreach (int2 tile in tiles) footprintOwners[tile] = owner;
    }

    /// <summary>True when one of the owner's own buildings (a blueprint included) takes <paramref name="tile"/>.</summary>
    private bool OwnFootprintAt(int2 tile, int owner) => footprintOwners.TryGetValue(tile, out int at) && at == owner;

    /// <summary>The owner's waiting blueprint covering <paramref name="tile"/>, if any (<paramref name="skip"/>'s id aside).</summary>
    internal bool PhantomAt(int2 tile, int owner, out int2 anchor, int skip = 0)
    {
        foreach (KeyValuePair<int, Phantom> pair in phantoms)
        {
            Phantom p = pair.Value;
            if (p.Owner != owner || pair.Key == skip) continue;
            int2 size = GetBuildingSize(p.Type);
            int2 start = Footprint.Start(p.Anchor, size);
            if (math.all(tile >= start) && math.all(tile < start + size))
            {
                anchor = p.Anchor;
                return true;
            }
        }
        anchor = default;
        return false;
    }

    /// <summary>Keeps a paid-for blueprint off the map until its spot is seen (see the class summary).</summary>
    [Server]
    private void AddPhantom(int id, int owner, BuildingType type, int2 anchor, float rotation, float paid) =>
        phantoms[id] = new Phantom { Owner = owner, Type = type, Anchor = anchor, Rotation = rotation, Paid = paid };

    /// <summary>
    /// Cancels the owner's waiting blueprint at once, with the full refund a blueprint gets (the owner pressed Cancel).
    /// False when <paramref name="id"/> isn't one.
    /// </summary>
    [Server]
    internal bool CancelPhantom(int id, int owner, ServerPlayer player)
    {
        if (!phantoms.TryGetValue(id, out Phantom p) || p.Owner != owner) return false;
        phantoms.Remove(id);
        player?.Refund(p.Paid);
        Ids?.Free(id);
        return true;
    }

    /// <summary>
    /// At each vision pass: a waiting blueprint whose ground is free now is placed for real; one the owner can now see is
    /// blocked (the rules fail on the real map where their side sees and on what they know elsewhere) is cancelled and
    /// refunded, and the owner alerted. Seeing part of the footprint while the blocker stays in the fog changes nothing,
    /// or the cancel would tell them something stands there unseen (security review 2026-11-06).
    /// </summary>
    [Server]
    private void ResolvePhantoms()
    {
        if (phantoms.Count == 0 || GameCore.Instance == null || GameCore.Instance.CurrentState != GameState.Playing) return;
        phantomScratch.Clear();
        phantomScratch.AddRange(phantoms.Keys);
        foreach (int id in phantomScratch)
        {
            Phantom p = phantoms[id];
            ServerPlayer player = GameCore.Instance.GetServerPlayerById(p.Owner);
            if (player == null || player.state != PlayerState.Playing)
            {
                phantoms.Remove(id);
                Ids?.Free(id);
                continue;
            }
            if (TruePlacement(p.Type, p.Anchor, p.Rotation, p.Owner) == PlacementResult.Ok)
            {
                phantoms.Remove(id);
                QueueBlueprint(p.Owner, p.Anchor, p.Type, p.Rotation, id, ConfigLoader.LoadConfig().GetBuilding(p.Type));
                continue;
            }
            if (!SeesBlocked(id, p)) continue;
            phantoms.Remove(id);
            player.Refund(p.Paid);
            Ids?.Free(id);
            Alerts?.Add(p.Owner, new Alert { Kind = AlertKind.BlueprintBlocked, Tile = p.Anchor });
        }
    }

    /// <summary>
    /// True when the owner can tell the waiting blueprint is blocked: the rules fail as the owner judges them, or (with nothing
    /// to judge by: the owner has no client now, or sees the whole footprint) on the real map once every tile is seen.
    /// </summary>
    private bool SeesBlocked(int id, Phantom p)
    {
        if (KnownPlacement(p.Type, p.Anchor, p.Rotation, p.Owner, id, out PlacementResult known)) return known != PlacementResult.Ok;
        foreach (int2 tile in Footprint.Tiles(p.Anchor, GetBuildingSize(p.Type)))
            if (!SeesNow(p.Owner, tile)) return false;
        return true; // seen whole, and the real placement already failed above
    }

    /// <summary>Each waiting blueprint as its owner sees it: a fresh blueprint (<see cref="BuildingView.OwnerOnly"/>).</summary>
    private void AddPhantomViews(List<BuildingView> views)
    {
        if (phantoms.Count == 0) return;
        GameConfigData config = ConfigLoader.LoadConfig();
        foreach (KeyValuePair<int, Phantom> pair in phantoms)
        {
            Phantom p = pair.Value;
            views.Add(new BuildingView
            {
                Id = pair.Key,
                OwnerId = p.Owner,
                Type = p.Type,
                Anchor = p.Anchor,
                Rotation = (byte)((int)math.round(p.Rotation / 90f) & 3),
                Health = (int)math.ceil(ConstructionRules.StartHealth),
                MaxHealth = config.GetBuilding(p.Type).Health,
                Hidden = p.Type == BuildingType.Bomb,
                Progress = 0,
                Position = p.Anchor,
                OwnerOnly = true,
            });
        }
    }

    /// <summary>An eliminated owner's waiting blueprints go with their buildings (no refund: the match is over for them).</summary>
    private void ForgetPhantoms(int owner)
    {
        phantomScratch.Clear();
        foreach (KeyValuePair<int, Phantom> pair in phantoms) if (pair.Value.Owner == owner) phantomScratch.Add(pair.Key);
        foreach (int id in phantomScratch)
        {
            phantoms.Remove(id);
            Ids?.Free(id);
        }
    }

    private void ClearPhantoms()
    {
        foreach (int id in phantoms.Keys) Ids?.Free(id);
        phantoms.Clear();
    }
}
