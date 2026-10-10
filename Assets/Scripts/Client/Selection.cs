using System;
using System.Collections.Generic;
using Mirror;
using Unity.Mathematics;

namespace WAR2D.Client
{
    /// <summary>
    /// The local player's selection and squads. A box selects every own unit inside it (no limit);
    /// orders go to the server as chunked id lists, or as a squad order when the selection is exactly
    /// a squad. The client keeps its own copy of what it assigned to each squad, for selecting it.
    /// </summary>
    public sealed class Selection
    {
        private readonly HashSet<int> selected = new HashSet<int>();
        private readonly List<int>[] squads = new List<int>[WAR2D.Sim.Squads.Count];
        private readonly List<int> scratch = new List<int>();
        private readonly System.Random tokens = new System.Random();
        private int selectedSquad = -1;

        /// <summary>Selected unit ids.</summary>
        public IReadOnlyCollection<int> Selected => selected;

        /// <summary>The selected own building's id (0 when none). Selecting units clears it, and the reverse.</summary>
        public int SelectedBuilding { get; private set; }

        /// <summary>The selected squad when the selection is exactly that squad, else -1.</summary>
        public int ActiveSquad => selectedSquad >= 0 && IsExactly(squads[selectedSquad]) ? selectedSquad : -1;

        /// <summary>Selects one own building (dropping the unit selection).</summary>
        public void SelectBuilding(int buildingId)
        {
            selected.Clear();
            selectedSquad = -1;
            SelectedBuilding = buildingId;
        }

        /// <summary>Clears the building selection (for example when the building is gone).</summary>
        public void ClearBuilding() => SelectedBuilding = 0;

        /// <summary>Selects the own units inside the box; <paramref name="append"/> keeps the current selection.</summary>
        public void SelectBox(float2 a, float2 b, int ownerId, bool append)
        {
            if (!append) selected.Clear();
            selectedSquad = -1;
            SelectedBuilding = 0;
            scratch.Clear();
            ClientWorld.Instance?.QueryBox(a, b, ownerId, scratch);
            foreach (int id in scratch) selected.Add(id);
        }

        /// <summary>
        /// Selects every own unit of the type of the own unit nearest <paramref name="at"/> (within a tile)
        /// that lies inside the view (a double-click). Does nothing when no own unit is there.
        /// </summary>
        public void SelectTypeAt(float2 at, float2 viewMin, float2 viewMax, int ownerId)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return;
            scratch.Clear();
            world.QueryBox(at - 1f, at + 1f, ownerId, scratch);
            int nearest = -1;
            float best = float.MaxValue;
            foreach (int id in scratch)
            {
                if (!world.TryGet(id, out ClientUnitView view)) continue;
                float d = math.distancesq(view.Position, at);
                if (d < best) { best = d; nearest = id; }
            }
            if (nearest < 0 || !world.TryGet(nearest, out ClientUnitView clicked)) return;
            scratch.Clear();
            world.QueryBox(viewMin, viewMax, ownerId, scratch);
            selected.Clear();
            selectedSquad = -1;
            SelectedBuilding = 0;
            foreach (int id in scratch)
                if (world.TryGet(id, out ClientUnitView view) && view.Type == clicked.Type) selected.Add(id);
        }

        /// <summary>The centre of the selected units the client knows; false when none.</summary>
        public bool TryCentre(out float2 centre)
        {
            centre = float2.zero;
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return false;
            int n = 0;
            foreach (int id in selected)
                if (world.TryGet(id, out ClientUnitView view)) { centre += view.Position; n++; }
            if (n == 0) return false;
            centre /= n;
            return true;
        }

        /// <summary>Selects exactly these ids (tests and tools).</summary>
        public void SelectIds(IEnumerable<int> ids)
        {
            selected.Clear();
            selectedSquad = -1;
            SelectedBuilding = 0;
            foreach (int id in ids) selected.Add(id);
        }

        /// <summary>Keeps only the selected units of one type (the selection panel's type buttons).</summary>
        public void FilterTo(UnitType type)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return;
            selected.RemoveWhere(id => !world.TryGet(id, out ClientUnitView view) || view.Type != type);
            selectedSquad = -1;
        }

        /// <summary>Assigns the selection to a squad, here and on the server.</summary>
        public void AssignSquad(int squad)
        {
            if ((uint)squad >= squads.Length) return;
            squads[squad] = Sorted();
            selectedSquad = squad;
            ushort token = NextToken();
            List<byte[]> chunks = OrderIdCodec.EncodeChunks(squads[squad]);
            if (chunks.Count == 0) chunks.Add(Array.Empty<byte>());
            for (int i = 0; i < chunks.Count; i++)
                WorldStateManager.Instance.CmdAssignSquadChunk(token, (byte)squad, chunks[i], i == chunks.Count - 1);
        }

        /// <summary>Selects a squad from the local copy of what was assigned.</summary>
        public void SelectSquad(int squad)
        {
            if ((uint)squad >= squads.Length || squads[squad] == null) return;
            selected.Clear();
            SelectedBuilding = 0;
            foreach (int id in squads[squad]) selected.Add(id);
            selectedSquad = squad;
        }

        /// <summary>Orders the selection to the goal: one squad order when it is exactly a squad, chunked ids otherwise.</summary>
        public void OrderMove(int2 goal) => Order(OrderKind.Move, false, goal);

        /// <summary>
        /// Gives the selection an order (the goal matters only for Move and AttackMove): one squad order
        /// when it is exactly a squad, chunked ids otherwise.
        /// </summary>
        public void Order(OrderKind kind, bool queue, int2 goal)
        {
            if (selected.Count == 0 || WorldStateManager.Instance == null) return;
            if (selectedSquad >= 0 && IsExactly(squads[selectedSquad]))
            {
                WorldStateManager.Instance.CmdOrderSquad((byte)selectedSquad, (byte)kind, queue, goal);
                return;
            }
            ushort token = NextToken();
            List<byte[]> chunks = OrderIdCodec.EncodeChunks(Sorted());
            for (int i = 0; i < chunks.Count; i++)
                WorldStateManager.Instance.CmdOrderChunk(token, chunks[i], i == chunks.Count - 1, (byte)kind, queue, goal);
        }

        /// <summary>Drops ids the client no longer knows (dead or out of sight).</summary>
        public void Prune()
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return;
            selected.RemoveWhere(id => !world.IsKnownId(id));
        }

        private bool IsExactly(List<int> squad)
        {
            if (squad == null || squad.Count != selected.Count) return false;
            foreach (int id in squad) if (!selected.Contains(id)) return false;
            return true;
        }

        private List<int> Sorted()
        {
            var ids = new List<int>(selected);
            ids.Sort();
            return ids;
        }

        private ushort NextToken() => (ushort)tokens.Next(0, 65536);
    }
}
