using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;
using WAR2D.Client.Render;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// The held-key rings (v0.18, Figma G1): while <see cref="GameInput.ShowRanges"/> is held, every selected own unit and
    /// building shows its attack range (solid), minimum range (dashed) and sight (dotted), and whatever is under the pointer
    /// (own, allied, an enemy the client holds, or a ghost) shows the same in its owner's colour. Rings come from the type alone
    /// (<see cref="RangeRules"/>), so nothing new is sent.
    /// </summary>
    public sealed class HeldRangeOverlay
    {
        private readonly List<RingSet> sets = new List<RingSet>(256);
        private readonly List<RingSet> kept = new List<RingSet>(64);
        private readonly List<int> under = new List<int>(8);

        /// <summary>True while the key is held (the v0.15 selected-unit rings step aside).</summary>
        public static bool Held => ForcedOn || GameInput.ShowRanges.IsPressed();

        /// <summary>The control API's stand-in for the held key.</summary>
        public static bool ForcedOn;

        public void Draw(OverlayFrame f)
        {
            if (!Held) return;
            Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
            if (config == null || f.World == null) return;
            sets.Clear();
            foreach (int id in f.Commander.Selection.Selected)
                if (f.World.TryGet(id, out ClientUnitView view) && config.Units.TryGetValue(view.Type, out Config.UnitConfig unit))
                    Add(RangeRules.OfUnit(unit, view.Position, f.Own), (int)view.Type);
            foreach (int id in f.Commander.Selection.SelectedBuildings) AddBuilding(f, config, id);
            AddUnderPointer(f, config);

            Vector3 c = f.Cam.transform.position;
            RangeRules.Choose(sets, new float2(c.x, c.y), config.RangeRings.Max, config.RangeRings.MergeOverlap, kept);
            foreach (RingSet set in kept) DrawSet(f, set);
        }

        private void Add(RingSet set, int group)
        {
            set.Group = group;
            sets.Add(set);
        }

        private void AddBuilding(OverlayFrame f, Config.GameConfigData config, int id)
        {
            if (!f.Commander.KnownBuildings.Entries.TryGetValue(id, out ClientBuildings.Entry e)) return;
            if (!config.Buildings.TryGetValue(e.Data.buildingType, out Config.BuildingConfig building)) return;
            float2 at = Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType));
            Add(RangeRules.OfBuilding(e.Data.buildingType, building, config.Siege.JamRadius, at, e.Data.ownerId), 1000 + (int)e.Data.buildingType);
        }

        private void AddUnderPointer(OverlayFrame f, Config.GameConfigData config)
        {
            if (f.PointerOverUI) return;
            under.Clear();
            f.World.CollectInRadius(f.Pointer, 0.7f, 0, under); // owner 0 is nobody: everyone's units count
            int best = 0;
            float bestSq = float.MaxValue;
            foreach (int id in under)
            {
                if (!f.World.TryGet(id, out ClientUnitView view)) continue;
                float d = math.distancesq(view.Position, f.Pointer);
                if (d < bestSq) { bestSq = d; best = id; }
            }
            if (best != 0 && f.World.TryGet(best, out ClientUnitView hovered) && config.Units.TryGetValue(hovered.Type, out Config.UnitConfig unit))
            {
                Add(RangeRules.OfUnit(unit, hovered.Position, hovered.OwnerId), (int)hovered.Type);
                return;
            }
            if (f.Commander.Picker != null && f.Commander.Picker.TryPick((int2)math.floor(f.Pointer), out int building)) AddBuilding(f, config, building);
        }

        private static void DrawSet(OverlayFrame f, in RingSet set)
        {
            Color32 colour = PlayerPalette.OfOwner(set.Owner);
            float px = f.Px;
            int segments = 96;
            if (set.Range > 0f) f.Mesh.Ring(set.At, set.Range, 1.5f * px, colour, segments);
            if (set.Min > 0f) OverlayShapes.DashedRing(f.Mesh, set.At, set.Min, 1.5f * px, colour, 8f * px);
            if (set.Sight > 0f) OverlayShapes.DashedRing(f.Mesh, set.At, set.Sight, px, OverlayColours.Alpha(colour, 0.6f), 2f * px);
        }
    }
}
