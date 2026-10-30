using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>What the quick-select chips count: own Builders with nothing to do, all Builders, and the army.</summary>
    public readonly struct QuickCounts
    {
        public readonly int IdleBuilders, Builders, Army;

        public QuickCounts(int idleBuilders, int builders, int army)
        {
            IdleBuilders = idleBuilders;
            Builders = builders;
            Army = army;
        }
    }

    /// <summary>
    /// v0.15 quality of life on the selection: the quick-select chips (Idle Builders, All Builders, Army), a type
    /// selected across the whole map, and groups of buildings in the squad slots (client only: the server knows
    /// nothing about them, they only select).
    /// </summary>
    public sealed partial class Selection
    {
        /// <summary>
        /// How close (tiles from a building's anchor) a standing Builder must be to a blueprint or a damaged own building to
        /// count as working on it. The client is never told what a Builder is doing, only where it is and whether it is moving.
        /// </summary>
        public const float WorkNear = 5f;

        private readonly List<int> mapWide = new List<int>();
        private readonly List<int>[] buildingGroups = new List<int>[WAR2D.Sim.Squads.Count];
        private readonly List<int> quickScratch = new List<int>();

        /// <summary>True while the selection is exactly the units a map-wide type selection picked.</summary>
        public bool MapWide
        {
            get
            {
                if (mapWide.Count == 0 || mapWide.Count != selected.Count) return false;
                foreach (int id in mapWide) if (!selected.Contains(id)) return false;
                return true;
            }
        }

        /// <summary>True for a unit type the Army chip selects: it attacks, and is neither a Digger nor a Suicide bomber.</summary>
        public static bool IsArmy(UnitType type, Config.UnitConfig config) =>
            config != null && config.Damage > 0 && !IsSiege(type);

        /// <summary>Diggers and Suicide bombers: left out of the Army chip until it is pressed again.</summary>
        public static bool IsSiege(UnitType type) => type == UnitType.Digger || type == UnitType.SuicideBomber;

        /// <summary>
        /// True when a Builder standing at <paramref name="position"/> has nothing to do as far as the client can tell: it is not
        /// moving and there is no blueprint or damaged own building within <see cref="WorkNear"/> tiles.
        /// </summary>
        public static bool IsIdleBuilder(int id, float2 position, int ownerId)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null || world.IsMoving(id)) return false;
            foreach (ClientBuildings.Entry e in ClientBuildings.Current.Entries.Values)
            {
                if (e.Data.ownerId != ownerId || e.Ghost) continue;
                bool needsWork = e.Progress < 100 || (e.Health.maxHealth > 0f && e.Health.currentHealth < e.Health.maxHealth);
                if (needsWork && math.distance(position, e.Data.position) <= WorkNear) return false;
            }
            return true;
        }

        /// <summary>Counts for the quick-select chips.</summary>
        public QuickCounts CountQuick(int ownerId)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return default;
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            quickScratch.Clear();
            world.CollectOwn(ownerId, quickScratch);
            int idle = 0, builders = 0, army = 0;
            foreach (int id in quickScratch)
            {
                if (!world.TryGet(id, out ClientUnitView view)) continue;
                if (view.Type == UnitType.Builder)
                {
                    builders++;
                    if (IsIdleBuilder(id, view.Position, ownerId)) idle++;
                }
                else if (IsArmy(view.Type, config.GetUnit(view.Type))) army++;
            }
            return new QuickCounts(idle, builders, army);
        }

        /// <summary>Selects the own Builders with nothing to do, or with <paramref name="all"/> every Builder. Returns how many.</summary>
        public int SelectBuilders(int ownerId, bool all)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return 0;
            quickScratch.Clear();
            world.CollectOwn(ownerId, quickScratch, UnitType.Builder);
            var picked = new List<int>();
            foreach (int id in quickScratch)
                if (all || (world.TryGet(id, out ClientUnitView view) && IsIdleBuilder(id, view.Position, ownerId))) picked.Add(id);
            if (picked.Count > 0) SelectIds(picked);
            return picked.Count;
        }

        /// <summary>
        /// Selects the army (every own unit that attacks, Diggers and Suicide bombers aside); with <paramref name="includeSiege"/>
        /// the Diggers and bombers too. Returns how many.
        /// </summary>
        public int SelectArmy(int ownerId, bool includeSiege)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return 0;
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            quickScratch.Clear();
            world.CollectOwn(ownerId, quickScratch);
            var picked = new List<int>();
            foreach (int id in quickScratch)
            {
                if (!world.TryGet(id, out ClientUnitView view)) continue;
                if (IsArmy(view.Type, config.GetUnit(view.Type)) || (includeSiege && IsSiege(view.Type))) picked.Add(id);
            }
            if (picked.Count > 0) SelectIds(picked);
            return picked.Count;
        }

        /// <summary>True when the selection is exactly the army the Army chip picks (so pressing it again adds the siege units).</summary>
        public bool IsExactlyArmy(int ownerId)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null || selected.Count == 0) return false;
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            quickScratch.Clear();
            world.CollectOwn(ownerId, quickScratch);
            int army = 0;
            foreach (int id in quickScratch)
            {
                if (!world.TryGet(id, out ClientUnitView view) || !IsArmy(view.Type, config.GetUnit(view.Type))) continue;
                army++;
                if (!selected.Contains(id)) return false;
            }
            return army == selected.Count;
        }

        /// <summary>
        /// Selects every own unit of a type anywhere on the map, not just on screen (a double-click on the selection panel's type
        /// button, or Ctrl + double-click on a unit). Returns how many; the camera does not move.
        /// </summary>
        public int SelectTypeEverywhere(UnitType type, int ownerId)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null || type == UnitType.None) return 0;
            quickScratch.Clear();
            world.CollectOwn(ownerId, quickScratch, type);
            if (quickScratch.Count == 0) return 0;
            SelectIds(quickScratch);
            mapWide.Clear();
            mapWide.AddRange(quickScratch);
            return quickScratch.Count;
        }

        /// <summary>How many own units of a type the client knows of anywhere (the "12 / 31" on a type chip).</summary>
        public int CountEverywhere(UnitType type, int ownerId)
        {
            ClientWorld world = ClientWorld.Instance;
            if (world == null) return 0;
            quickScratch.Clear();
            world.CollectOwn(ownerId, quickScratch, type);
            return quickScratch.Count;
        }

        // ---- building groups in the squad slots

        /// <summary>
        /// Saves the selected own buildings in a squad slot (Ctrl + number with only producers selected). A slot holds units or
        /// buildings: any units saved there are forgotten, here and on the server. Returns false with no buildings selected.
        /// </summary>
        public bool AssignBuildingGroup(int slot)
        {
            if ((uint)slot >= buildingGroups.Length || buildings.Count == 0) return false;
            buildingGroups[slot] = new List<int>(buildings);
            if (squads[slot] != null)
            {
                squads[slot] = null;
                if (WorldStateManager.Instance != null)
                    WorldStateManager.Instance.CmdAssignSquadChunk(NextToken(), (byte)slot, System.Array.Empty<byte>(), true);
            }
            return true;
        }

        /// <summary>True when a squad slot holds buildings.</summary>
        public bool IsBuildingGroup(int slot) => (uint)slot < buildingGroups.Length && buildingGroups[slot] != null && buildingGroups[slot].Count > 0;

        /// <summary>How many buildings a slot still holds (those the client no longer knows are dropped first).</summary>
        public int BuildingGroupCount(int slot)
        {
            if (!IsBuildingGroup(slot)) return 0;
            buildingGroups[slot].RemoveAll(id => !ClientBuildings.Current.Entries.ContainsKey(id));
            return buildingGroups[slot].Count;
        }

        /// <summary>Selects a slot's buildings. Returns false when it holds none (any more).</summary>
        public bool SelectBuildingGroup(int slot)
        {
            if (BuildingGroupCount(slot) == 0) return false;
            SelectBuildings(buildingGroups[slot], false);
            return true;
        }

        /// <summary>The first building of a slot (the camera's target when its key is pressed twice), or 0.</summary>
        public int FirstOfBuildingGroup(int slot) => BuildingGroupCount(slot) > 0 ? buildingGroups[slot][0] : 0;

        /// <summary>True when the selection is exactly the buildings of this slot.</summary>
        public bool IsExactlyBuildingGroup(int slot)
        {
            if (BuildingGroupCount(slot) == 0 || buildings.Count != buildingGroups[slot].Count) return false;
            foreach (int id in buildingGroups[slot]) if (!buildings.Contains(id)) return false;
            return true;
        }
    }
}
