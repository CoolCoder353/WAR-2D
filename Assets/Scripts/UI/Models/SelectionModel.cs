using System;
using System.Collections.Generic;
using Mirror;
using WAR2D.Client;

namespace WAR2D.UI
{
    /// <summary>
    /// What the selection panel, command card and squad bar show: the selected units per type and their
    /// combined health, the selected own building and its production queue, the armed order, and the
    /// squad counts. Filled only from this client's own data (its predicted units, its own selection,
    /// and TargetRpcs only it receives). Raises <see cref="Changed"/> only when something shown changed.
    /// </summary>
    public sealed class SelectionModel
    {
        private readonly List<ClientUnitView> units = new List<ClientUnitView>();
        private readonly List<(UnitType type, int count)> byType = new List<(UnitType, int)>();
        private readonly List<(UnitType type, int count)> scratchTypes = new List<(UnitType, int)>();
        private readonly List<ClientUnitView> scratchUnits = new List<ClientUnitView>();
        private readonly int[] squadCounts = new int[Sim.Squads.Count];

        /// <summary>Selected units per type, in <see cref="UnitType"/> order.</summary>
        public IReadOnlyList<(UnitType type, int count)> ByType => byType;

        /// <summary>The number of selected units.</summary>
        public int Count => units.Count;

        /// <summary>The mean health (0–1) of the selected units; 0 when none.</summary>
        public float CombinedHealth01 { get; private set; }

        /// <summary>The squad the selection is exactly, or -1.</summary>
        public int Squad { get; private set; } = -1;

        /// <summary>The selected own building's id (0 when none).</summary>
        public int BuildingId { get; private set; }

        /// <summary>The selected building's type (None when none).</summary>
        public BuildingType BuildingType { get; private set; }

        /// <summary>Units queued on the selected spawners, together.</summary>
        public int SpawnerQueue { get; private set; }

        private readonly List<int> buildingIds = new List<int>();

        /// <summary>Every selected own building (several only for spawners).</summary>
        public IReadOnlyList<int> BuildingIds => buildingIds;

        private readonly List<int> queueByType = new List<int>();
        private readonly List<UnitType> productionTypes = new List<UnitType>();

        /// <summary>The types the selected producers are making (the server's choice), first selected building first, each once.</summary>
        public IReadOnlyList<UnitType> ProductionTypes => productionTypes;

        /// <summary>Units of one type queued on the selected producers, together.</summary>
        public int QueueOf(UnitType type) => (int)type >= 0 && (int)type < queueByType.Count ? queueByType[(int)type] : 0;

        /// <summary>How many of the selected own buildings are still blueprints.</summary>
        public int BlueprintCount { get; private set; }

        /// <summary>True when every selected own building is a blueprint (Cancel, no confirm).</summary>
        public bool AllBlueprints => buildingIds.Count > 0 && BlueprintCount == buildingIds.Count;

        /// <summary>The first selected building's construction progress in percent (100 when complete).</summary>
        public int BuildingProgress { get; private set; } = 100;

        /// <summary>
        /// What cancelling or demolishing the selected buildings refunds, as the server works it out: a blueprint
        /// its full <paramref name="cost"/>, a finished building <paramref name="cost"/> × <paramref name="demolishShare"/>.
        /// </summary>
        public float RefundFor(float cost, float demolishShare) =>
            ConstructionRules.Refund(true, cost, demolishShare) * BlueprintCount +
            ConstructionRules.Refund(false, cost, demolishShare) * (buildingIds.Count - BlueprintCount);

        /// <summary>How many own wall tiles (blueprints included) are selected.</summary>
        public int WallCount { get; private set; }

        /// <summary>How many of the selected wall tiles are still blueprints.</summary>
        public int WallBlueprints { get; private set; }

        /// <summary>Sets the selected wall tiles and how many of them are blueprints.</summary>
        public void SetWalls(int count, int blueprints)
        {
            if (count == WallCount && blueprints == WallBlueprints) return;
            WallCount = count;
            WallBlueprints = blueprints;
            Changed?.Invoke();
        }

        /// <summary>True when the selection holds a Builder.</summary>
        public bool HasBuilders => Has(UnitType.Builder);

        /// <summary>True when the selection holds a Digger.</summary>
        public bool HasDiggers => Has(UnitType.Digger);

        /// <summary>True when the selection holds a unit of <paramref name="type"/>.</summary>
        public bool HasType(UnitType type) => Has(type);

        /// <summary>True when the selected own buildings are of different kinds (no shared production row).</summary>
        public bool MixedKinds { get; private set; }

        private bool Has(UnitType type)
        {
            foreach ((UnitType t, int count) in byType) if (t == type && count > 0) return true;
            return false;
        }

        /// <summary>The order waiting for a click, if any.</summary>
        public OrderKind? ArmedOrder { get; private set; }

        /// <summary>How many of the selected units are roaming (Roam, J): the title's "· 4 roaming" and the button's Active state.</summary>
        public int Roaming { get; private set; }

        /// <summary>True when every selected unit that can roam is roaming (Builders never roam).</summary>
        public bool AllRoaming => Roaming > 0 && Roaming >= Count - CountOf(UnitType.Builder);

        /// <summary>Selected units of <paramref name="type"/>.</summary>
        public int CountOf(UnitType type)
        {
            foreach ((UnitType t, int count) in byType) if (t == type) return count;
            return 0;
        }

        /// <summary>Sets how many selected units roam.</summary>
        public void SetRoaming(int roaming)
        {
            if (roaming == Roaming) return;
            Roaming = roaming;
            Changed?.Invoke();
        }

        /// <summary>The local player's live units in each squad (index 0–9 is squad key 1–9, 0).</summary>
        public IReadOnlyList<int> SquadCounts => squadCounts;

        /// <summary>Raised when anything shown changed.</summary>
        public event Action Changed;

        // ---- v0.15 QoL (Figma v0.15 QoL – redesign, frames 5 and 6) ----

        private readonly int[] slotBuildings = new int[Sim.Squads.Count];
        private readonly BuildingType[] slotBuildingTypes = new BuildingType[Sim.Squads.Count];
        private readonly List<(UnitType type, int total)> totals = new List<(UnitType, int)>();
        private readonly List<(UnitType type, int total)> scratchTotals = new List<(UnitType, int)>();

        /// <summary>True while the selection is exactly what a map-wide type selection picked (the "Map-wide" tag).</summary>
        public bool MapWide { get; private set; }

        /// <summary>How many buildings each squad slot holds as a building group (0 for a unit squad or an empty slot).</summary>
        public IReadOnlyList<int> SlotBuildings => slotBuildings;

        /// <summary>The kind of the first building in each building-group slot (its icon on the squad bar).</summary>
        public IReadOnlyList<BuildingType> SlotBuildingTypes => slotBuildingTypes;

        /// <summary>How many own units of a selected type the client knows of on the whole map (the "10 / 1,840" on a type chip).</summary>
        public int TotalOf(UnitType type)
        {
            foreach ((UnitType t, int total) in totals) if (t == type) return total;
            return 0;
        }

        /// <summary>Raised by <see cref="SelectEverywhere"/>: select every own unit of this type on the map.</summary>
        public event Action<UnitType> EverywhereRequested;

        /// <summary>A type chip double-clicked: every own unit of the type anywhere on the map.</summary>
        public void SelectEverywhere(UnitType type) => EverywhereRequested?.Invoke(type);

        /// <summary>Sets the map-wide flag and the per-type totals of the selected types.</summary>
        public void SetQuick(bool mapWide, IReadOnlyList<(UnitType type, int total)> typeTotals)
        {
            bool same = mapWide == MapWide && typeTotals.Count == totals.Count;
            for (int i = 0; same && i < totals.Count; i++) same = totals[i] == typeTotals[i];
            if (same) return;
            MapWide = mapWide;
            totals.Clear();
            for (int i = 0; i < typeTotals.Count; i++) totals.Add(typeTotals[i]);
            Changed?.Invoke();
        }

        /// <summary>Sets the building-group slots: how many buildings each holds and the first one's kind.</summary>
        public void SetSlotBuildings(IReadOnlyList<int> counts, IReadOnlyList<BuildingType> types)
        {
            if (counts == null || types == null || counts.Count != slotBuildings.Length || types.Count != slotBuildings.Length) return;
            bool same = true;
            for (int i = 0; i < slotBuildings.Length; i++) same &= slotBuildings[i] == counts[i] && slotBuildingTypes[i] == types[i];
            if (same) return;
            for (int i = 0; i < slotBuildings.Length; i++)
            {
                slotBuildings[i] = counts[i];
                slotBuildingTypes[i] = types[i];
            }
            Changed?.Invoke();
        }

        private readonly int[] pulledSlotCounts = new int[Sim.Squads.Count];
        private readonly BuildingType[] pulledSlotTypes = new BuildingType[Sim.Squads.Count];

        /// <summary>Reads the v0.15 selection data: building-group slots, the map-wide flag and the selected types' map totals.</summary>
        private void PullQuick(Selection selection, int ownerId)
        {
            var known = WAR2D.Net.Replication.ClientBuildings.Current.Entries;
            for (int i = 0; i < pulledSlotCounts.Length; i++)
            {
                pulledSlotCounts[i] = selection.BuildingGroupCount(i);
                int first = pulledSlotCounts[i] > 0 ? selection.FirstOfBuildingGroup(i) : 0;
                pulledSlotTypes[i] = first != 0 && known.TryGetValue(first, out var entry) ? entry.Data.buildingType : BuildingType.None;
            }
            SetSlotBuildings(pulledSlotCounts, pulledSlotTypes);
            scratchTotals.Clear();
            foreach ((UnitType type, int _) in byType) scratchTotals.Add((type, selection.CountEverywhere(type, ownerId)));
            SetQuick(selection.MapWide, scratchTotals);
        }

        // ---- The selected buildings in the selection panel (v0.13, Figma: Selected building – v0.13) ----

        private readonly List<(BuildingType type, int count)> buildingKinds = new List<(BuildingType, int)>();
        private readonly List<(BuildingType type, int count)> scratchKinds = new List<(BuildingType, int)>();

        /// <summary>The selected own buildings per type (the panel's chips), in <see cref="global::BuildingType"/> order.</summary>
        public IReadOnlyList<(BuildingType type, int count)> BuildingKinds => buildingKinds;

        /// <summary>The mean health (0–1) of the selected own buildings.</summary>
        public float BuildingHealth01 { get; private set; }

        /// <summary>The running cost per second of the selected finished buildings, together (blueprints pay none).</summary>
        public float BuildingUpkeep { get; private set; }

        /// <summary>True when one of the selected finished buildings is unpaid (the server's flag, sent to the owner only).</summary>
        public bool BuildingUnpaid { get; private set; }

        /// <summary>The status line under Upkeep (empty when hidden: mixed types, or a type without one).</summary>
        public string BuildingStatus { get; private set; } = "";

        /// <summary>How the status line reads: working (success), idle or a blueprint (secondary), unpaid (danger).</summary>
        public StatusTone BuildingStatusTone { get; private set; }

        /// <summary>The status line's colour role.</summary>
        public enum StatusTone { Working, Idle, Danger }

        /// <summary>One selected own building as the panel reads it.</summary>
        public struct BuildingRow
        {
            public BuildingType Type;
            public float Health01;
            /// <summary>Running cost per second (0 for a blueprint).</summary>
            public float Upkeep;
            public bool Blueprint, Unpaid;
            /// <summary>A Gun turret that shot just now.</summary>
            public bool Firing;
        }

        /// <summary>
        /// The status line for selected buildings of one type (decisions 6 and 7 of <c>docs/ux/v0.13-figma.md</c>):
        /// Gun turrets "Firing: range 7" / "Idle: range 7" / "Unpaid: not firing"; Jammers "Jamming: radius 12" /
        /// "Unpaid: not jamming"; either still a blueprint "Blueprint: works when built". Empty for other types and
        /// for a mix of types.
        /// </summary>
        public static (string text, StatusTone tone) StatusOf(IReadOnlyList<BuildingRow> rows, float turretRange, float jamRadius)
        {
            if (rows.Count == 0) return ("", StatusTone.Idle);
            BuildingType type = rows[0].Type;
            bool built = false, unpaid = false, firing = false;
            foreach (BuildingRow r in rows)
            {
                if (r.Type != type) return ("", StatusTone.Idle);
                if (r.Blueprint) continue;
                built = true;
                unpaid |= r.Unpaid;
                firing |= r.Firing;
            }
            if (type != BuildingType.GunTurret && type != BuildingType.Jammer) return ("", StatusTone.Idle);
            if (!built) return ("Blueprint: works when built", StatusTone.Idle);
            if (type == BuildingType.GunTurret)
                return unpaid ? ("Unpaid: not firing", StatusTone.Danger)
                    : firing ? ($"Firing: range {turretRange:0.#}", StatusTone.Working)
                    : ($"Idle: range {turretRange:0.#}", StatusTone.Idle);
            return unpaid ? ("Unpaid: not jamming", StatusTone.Danger) : ($"Jamming: radius {jamRadius:0.#}", StatusTone.Working);
        }

        /// <summary>Sets what the panel shows for the selected own buildings (empty <paramref name="rows"/>: none).</summary>
        public void SetBuildingRows(IReadOnlyList<BuildingRow> rows, float turretRange, float jamRadius)
        {
            scratchKinds.Clear();
            float health = 0f, upkeep = 0f;
            bool unpaid = false;
            foreach (BuildingRow r in rows)
            {
                health += r.Health01;
                upkeep += r.Upkeep;
                unpaid |= r.Unpaid && !r.Blueprint;
                int at = scratchKinds.FindIndex(k => k.type == r.Type);
                if (at < 0) scratchKinds.Add((r.Type, 1));
                else scratchKinds[at] = (r.Type, scratchKinds[at].count + 1);
            }
            scratchKinds.Sort((a, b) => a.type.CompareTo(b.type));
            float combined = rows.Count > 0 ? health / rows.Count : 0f;
            (string status, StatusTone tone) = StatusOf(rows, turretRange, jamRadius);

            bool same = combined == BuildingHealth01 && upkeep == BuildingUpkeep && unpaid == BuildingUnpaid && status == BuildingStatus &&
                        tone == BuildingStatusTone && scratchKinds.Count == buildingKinds.Count;
            for (int i = 0; same && i < buildingKinds.Count; i++) same = scratchKinds[i] == buildingKinds[i];
            if (same) return;
            buildingKinds.Clear();
            buildingKinds.AddRange(scratchKinds);
            BuildingHealth01 = combined;
            BuildingUpkeep = upkeep;
            BuildingUnpaid = unpaid;
            BuildingStatus = status;
            BuildingStatusTone = tone;
            Changed?.Invoke();
        }

        private readonly List<BuildingRow> pulledRows = new List<BuildingRow>();

        /// <summary>Raised by <see cref="FilterTo"/>: the selection should keep only this type.</summary>
        public event Action<UnitType> FilterRequested;

        /// <summary>Replaces the selected units (and the squad they exactly are, or -1).</summary>
        public void SetUnits(IReadOnlyList<ClientUnitView> views, int squad)
        {
            scratchUnits.Clear();
            for (int i = 0; i < views.Count; i++) scratchUnits.Add(views[i]);
            Apply(scratchUnits, squad);
        }

        /// <summary>Keeps only the selected units of one type, here and (through <see cref="FilterRequested"/>) in the selection.</summary>
        public void FilterTo(UnitType type)
        {
            scratchUnits.Clear();
            foreach (ClientUnitView unit in units) if (unit.Type == type) scratchUnits.Add(unit);
            Apply(scratchUnits, -1);
            FilterRequested?.Invoke(type);
        }

        /// <summary>Sets the selected own building (0 for none) and its queue.</summary>
        public void SetBuilding(int id, BuildingType type, int queue) => SetBuildings(id == 0 ? Array.Empty<int>() : new[] { id }, type, queue);

        /// <summary>
        /// Sets the selected own buildings (the first one's type stands for all), their queues together (in all and
        /// per unit type, indexed by <see cref="UnitType"/>), how many are blueprints, the first one's progress, and
        /// whether they are of different kinds.
        /// </summary>
        public void SetBuildings(IReadOnlyList<int> ids, BuildingType type, int queue, IReadOnlyList<int> byType = null, int blueprints = 0, int progress = 100, bool mixedKinds = false, IReadOnlyList<UnitType> types = null)
        {
            int id = ids.Count > 0 ? ids[0] : 0;
            bool same = ids.Count == buildingIds.Count && id == BuildingId && type == BuildingType && queue == SpawnerQueue &&
                        blueprints == BlueprintCount && progress == BuildingProgress && mixedKinds == MixedKinds && (byType?.Count ?? 0) == queueByType.Count;
            for (int i = 0; same && i < ids.Count; i++) same = ids[i] == buildingIds[i];
            for (int i = 0; same && i < queueByType.Count; i++) same = byType[i] == queueByType[i];
            int typeCount = types?.Count ?? 0;
            same &= typeCount == productionTypes.Count;
            for (int i = 0; same && i < typeCount; i++) same = types[i] == productionTypes[i];
            if (same) return;
            productionTypes.Clear();
            for (int i = 0; i < typeCount; i++) productionTypes.Add(types[i]);
            buildingIds.Clear();
            for (int i = 0; i < ids.Count; i++) buildingIds.Add(ids[i]);
            queueByType.Clear();
            if (byType != null) for (int i = 0; i < byType.Count; i++) queueByType.Add(byType[i]);
            BuildingId = id;
            BuildingType = type;
            SpawnerQueue = queue;
            BlueprintCount = blueprints;
            BuildingProgress = progress;
            MixedKinds = mixedKinds;
            Changed?.Invoke();
        }

        /// <summary>Sets the order waiting for a click.</summary>
        public void SetArmed(OrderKind? armed)
        {
            if (armed == ArmedOrder) return;
            ArmedOrder = armed;
            Changed?.Invoke();
        }

        /// <summary>Sets the squad counts (ignored unless there is one per squad).</summary>
        public void SetSquadCounts(IReadOnlyList<int> counts)
        {
            if (counts == null || counts.Count != squadCounts.Length) return;
            bool same = true;
            for (int i = 0; i < squadCounts.Length; i++) same &= squadCounts[i] == counts[i];
            if (same) return;
            for (int i = 0; i < squadCounts.Length; i++) squadCounts[i] = counts[i];
            Changed?.Invoke();
        }

        /// <summary>Reads the local selection, its units, the armed order, the building queue and the squad counts.</summary>
        public void Pull()
        {
            UnitCommander commander = UnitCommander.Instance;
            ClientWorld world = ClientWorld.Instance;
            ClientPlayer local = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.GetComponent<ClientPlayer>() : null;
            if (commander == null || local == null) return;

            Selection selection = commander.Selection;
            var views = new List<ClientUnitView>(selection.Selected.Count);
            if (world != null)
                foreach (int id in selection.Selected)
                    if (world.TryGet(id, out ClientUnitView view)) views.Add(view);
            SetUnits(views, selection.ActiveSquad);

            int buildingId = selection.SelectedBuilding;
            BuildingType type = BuildingType.None;
            var known = WAR2D.Net.Replication.ClientBuildings.Current.Entries;
            WAR2D.Net.Replication.ClientBuildings.Entry first = default;
            if (buildingId != 0 && known.TryGetValue(buildingId, out first) && !first.Ghost) type = first.Data.buildingType;
            if (type == BuildingType.None) SetBuilding(0, BuildingType.None, 0);
            else
            {
                int queue = 0, blueprints = 0;
                bool mixed = false;
                pulledByType.Clear();
                inUse.Clear();
                foreach (UnitType unit in SpawnerRules.Produces(type))
                {
                    while (pulledByType.Count <= (int)unit) pulledByType.Add(0);
                    foreach (int id in selection.SelectedBuildings) pulledByType[(int)unit] += local.SpawnerQueueOf(id, unit);
                }
                foreach (int id in selection.SelectedBuildings)
                {
                    queue += local.SpawnerQueue(id);
                    UnitType making = local.SpawnerType(id);
                    if (making == UnitType.None && known.TryGetValue(id, out var producer)) making = SpawnerRules.DefaultUnit(producer.Data.buildingType);
                    if (making != UnitType.None && !inUse.Contains(making)) inUse.Add(making);
                    if (known.TryGetValue(id, out var entry))
                    {
                        if (entry.Progress < 100) blueprints++;
                        mixed |= entry.Data.buildingType != type;
                    }
                }
                SetBuildings(selection.SelectedBuildings, type, queue, pulledByType, blueprints, first.Progress, mixed, inUse);
            }
            int wallBlueprints = 0;
            WorldStateManager wsm = WorldStateManager.Instance;
            foreach (Unity.Mathematics.int2 tile in selection.SelectedWalls)
                if (wsm != null && wsm.Map != null && wsm.Map.Grid.Contains(tile) && wsm.Map.Grid.TileAt(tile) == TileType.WallBlueprint) wallBlueprints++;
            SetWalls(selection.SelectedWalls.Count, wallBlueprints);
            PullBuildingRows(selection, known);
            SetArmed(commander.ArmedOrder);
            int roaming = 0;
            foreach (int id in selection.Selected) if (commander.Roaming.IsRoaming(id)) roaming++;
            SetRoaming(roaming);
            SetSquadCounts(local.SquadCounts);
            PullQuick(selection, BuildingData.UIntToInt(local.netId));
        }

        /// <summary>The selected own buildings as the selection panel reads them (health, upkeep, unpaid, firing).</summary>
        private void PullBuildingRows(Selection selection, Dictionary<int, WAR2D.Net.Replication.ClientBuildings.Entry> known)
        {
            pulledRows.Clear();
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            float now = UnityEngine.Time.time;
            foreach (int id in selection.SelectedBuildings)
            {
                if (!known.TryGetValue(id, out var e) || e.Ghost) continue;
                bool blueprint = e.Progress < 100;
                pulledRows.Add(new BuildingRow
                {
                    Type = e.Data.buildingType,
                    Health01 = e.Health.maxHealth > 0f ? Math.Clamp(e.Health.currentHealth / e.Health.maxHealth, 0f, 1f) : 1f,
                    Upkeep = blueprint ? 0f : config.GetBuilding(e.Data.buildingType).RunningCost,
                    Blueprint = blueprint,
                    Unpaid = DefenceView.Unpaid(e),
                    Firing = e.Data.buildingType == BuildingType.GunTurret && DefenceView.Firing(WAR2D.Net.Replication.ClientBuildings.Current, id, now),
                });
            }
            SetBuildingRows(pulledRows, config.GetBuilding(BuildingType.GunTurret).Range, config.Siege.JamRadius);
        }

        private readonly List<int> pulledByType = new List<int>();
        private readonly List<UnitType> inUse = new List<UnitType>();

        private void Apply(List<ClientUnitView> next, int squad)
        {
            scratchTypes.Clear();
            float health = 0f;
            foreach (ClientUnitView unit in next)
            {
                health += unit.Health01;
                int at = scratchTypes.FindIndex(t => t.type == unit.Type);
                if (at < 0) scratchTypes.Add((unit.Type, 1));
                else scratchTypes[at] = (unit.Type, scratchTypes[at].count + 1);
            }
            scratchTypes.Sort((a, b) => a.type.CompareTo(b.type));
            float combined = next.Count > 0 ? health / next.Count : 0f;

            bool same = squad == Squad && combined == CombinedHealth01 && scratchTypes.Count == byType.Count;
            for (int i = 0; same && i < byType.Count; i++) same = scratchTypes[i] == byType[i];

            units.Clear();
            units.AddRange(next);
            if (same) return;
            byType.Clear();
            byType.AddRange(scratchTypes);
            CombinedHealth01 = combined;
            Squad = squad;
            Changed?.Invoke();
        }
    }
}
