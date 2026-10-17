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

        /// <summary>The local player's live units in each squad (index 0–9 is squad key 1–9, 0).</summary>
        public IReadOnlyList<int> SquadCounts => squadCounts;

        /// <summary>Raised when anything shown changed.</summary>
        public event Action Changed;

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
            SetArmed(commander.ArmedOrder);
            SetSquadCounts(local.SquadCounts);
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
