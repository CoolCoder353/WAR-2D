using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// Rally spokes (Figma <c>v0.15 QoL – redesign</c>, item 8, frames 124:753 and 130:1371). A selected producer gets a
    /// thin dashed spoke to its rally point (the flag itself is <see cref="UnitCommander"/>'s sprite). While Rally all is
    /// armed, or Ctrl is held with producers selected, every own producer's spoke and a small flag at its rally show.
    /// A rally on one of the player's own blueprints is drawn in the Builder gold with the blueprint outlined, since new
    /// Builders go and help build it; pointing at one while rallying tags "Builders will help build". The rally points
    /// are the owner's own (<see cref="ClientPlayer.TryGetRally"/>); only the local player sees them.
    /// </summary>
    public sealed class RallyOverlay
    {
        /// <summary>Most spokes drawn at once.</summary>
        public const int MaxSpokes = 64;

        private readonly Dictionary<int, string> allTag = new Dictionary<int, string>();
        private const string HelpBuildTag = "Builders will help build";

        public void Draw(OverlayFrame f)
        {
            UnitCommander commander = f.Commander;
            ClientPlayer local = DefenceView.Local;
            ClientBuildings known = commander.KnownBuildings;
            if (local == null || known == null) return;
            IReadOnlyList<int> selected = commander.Selection.SelectedBuildings;
            bool producersOnly = commander.Selection.Selected.Count == 0 && selected.Count > 0;
            bool all = commander.RallyAllArmed || (producersOnly && GameInput.AssignModifier.IsPressed());
            int drawn = 0;
            if (all)
            {
                int producers = 0;
                foreach (KeyValuePair<int, ClientBuildings.Entry> pair in known.Entries)
                {
                    ClientBuildings.Entry e = pair.Value;
                    if (e.Ghost || e.Data.ownerId != f.Own || !SpawnerRules.IsProducer(e.Data.buildingType)) continue;
                    producers++;
                    if (drawn < MaxSpokes && Spoke(f, local, known, pair.Key, e, true)) drawn++;
                }
                // The "Rally all producers (N)" cursor tag is the HUD's (HudController).
            }
            else
            {
                for (int i = 0; i < selected.Count && drawn < MaxSpokes; i++)
                    if (known.Entries.TryGetValue(selected[i], out ClientBuildings.Entry e) && !e.Ghost && SpawnerRules.IsProducer(e.Data.buildingType) &&
                        Spoke(f, local, known, selected[i], e, false)) drawn++;
            }

            // Pointing at an own blueprint while a rally click would land there.
            bool rallying = commander.RallyAllArmed || (producersOnly && AllProducers(known, selected));
            if (rallying && !f.PointerOverUI && TryOwnBlueprint(f, known, (int2)math.floor(f.Pointer), out ClientBuildings.Entry bp))
            {
                OutlineBlueprint(f, bp);
                f.Labels.Screen(f.PointerScreen, HelpBuildTag, f.Readout, 18f, commander.RallyAllArmed ? 40f : 14f);
            }
        }

        /// <summary>One producer's spoke to its rally point (and a flag when <paramref name="flag"/>); false when it has none.</summary>
        private static bool Spoke(OverlayFrame f, ClientPlayer local, ClientBuildings known, int id, in ClientBuildings.Entry e, bool flag)
        {
            if (!local.TryGetRally(id, out int2 rally)) return false;
            bool help = TryOwnBlueprint(f, known, rally, out ClientBuildings.Entry bp);
            if (!help) rally = Snapped(rally);
            float2 from = Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType));
            float2 to = (float2)rally + 0.5f;
            Color32 c = help ? f.Readout : f.Accent;
            f.Mesh.Segment(from, to, f.Px, OverlayColours.Alpha(c, 0.85f), 4f * f.Px);
            if (help)
            {
                OutlineBlueprint(f, bp);
                OverlayShapes.Flag(f.Mesh, to, 0.8f, 2f * f.Px, new Color32(0xF0, 0xF4, 0xF8, 0xFF), f.Readout);
            }
            else if (flag) OverlayShapes.Flag(f.Mesh, to, 0.7f, 2f * f.Px, new Color32(0xF0, 0xF4, 0xF8, 0xFF), f.Accent);
            return true;
        }

        /// <summary>Where a rally on rock really sits: the open ground the server moves it to (as the selected flag is drawn).</summary>
        private static int2 Snapped(int2 rally)
        {
            WorldStateManager wsm = WorldStateManager.Instance;
            if (wsm != null && wsm.Map != null && wsm.Map.Grid.Contains(rally) &&
                WAR2D.Sim.OrderBook.SnapGoal(wsm.Map.Grid, rally, rally, WAR2D.Sim.OrderBook.GoalSnapRadius, out int2 open)) return open;
            return rally;
        }

        private static bool TryOwnBlueprint(OverlayFrame f, ClientBuildings known, int2 tile, out ClientBuildings.Entry e)
        {
            e = default;
            BuildingPicker picker = f.Commander.Picker;
            return picker != null && picker.TryPick(tile, out int id) && known.Entries.TryGetValue(id, out e) &&
                   !e.Ghost && e.Data.ownerId == f.Own && e.Progress < 100;
        }

        private static void OutlineBlueprint(OverlayFrame f, in ClientBuildings.Entry e)
        {
            int2 anchor = (int2)math.round(e.Data.position);
            int2 size = WorldStateManager.GetBuildingSize(e.Data.buildingType);
            float2 centre = Footprint.VisualCenter(anchor, size);
            float2 half = (float2)size * 0.5f + 0.1f;
            f.Mesh.RectOutline(centre - half, centre + half, 2f * f.Px, f.Readout, 5f * f.Px);
        }

        private static bool AllProducers(ClientBuildings known, IReadOnlyList<int> ids)
        {
            for (int i = 0; i < ids.Count; i++)
                if (!known.Entries.TryGetValue(ids[i], out ClientBuildings.Entry e) || !SpawnerRules.IsProducer(e.Data.buildingType)) return false;
            return true;
        }
    }
}
