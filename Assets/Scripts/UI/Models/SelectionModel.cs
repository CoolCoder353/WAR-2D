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

        /// <summary>Units queued on the selected spawner.</summary>
        public int SpawnerQueue { get; private set; }

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
        public void SetBuilding(int id, BuildingType type, int queue)
        {
            if (id == BuildingId && type == BuildingType && queue == SpawnerQueue) return;
            BuildingId = id;
            BuildingType = type;
            SpawnerQueue = queue;
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
            if (buildingId != 0 && commander.buildingGameObjects.TryGetValue(buildingId, out UnityEngine.GameObject go) &&
                go.TryGetComponent(out BuildingDataClient data))
                type = data.buildingData.buildingType;
            SetBuilding(type == BuildingType.None ? 0 : buildingId, type, type == BuildingType.None ? 0 : local.SpawnerQueue(buildingId));
            SetArmed(commander.ArmedOrder);
            SetSquadCounts(local.SquadCounts);
        }

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
