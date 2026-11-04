using System;
using System.Collections.Generic;
using Config;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;

namespace WAR2D.Client.Overlays
{
    /// <summary>
    /// Target lines (v0.16, Figma T): for the selected own units and Gun turrets, a 1 px line to what each is shooting at
    /// (red to units and buildings, orange to walls), and a dashed one to a unit's attack-move goal while it has no target
    /// and is still moving. Built from attack events the client already gets and the orders it sent.
    /// </summary>
    public sealed class TargetLineOverlay : IDisposable
    {
        private readonly TargetLineRules rules;
        private readonly List<(int id, float2 at)> candidates = new List<(int, float2)>();
        private ClientWorld subscribed;

        public TargetLineOverlay()
        {
            TargetLinesConfig c = ConfigLoader.LoadConfig().TargetLines;
            rules = new TargetLineRules(c.HoldSeconds, c.Max);
        }

        private void OnAttack(int attacker, int target) => rules.Shot(attacker, target, Time.time);

        /// <summary>Draws this frame's lines into the overlay mesh.</summary>
        public void Draw(OverlayFrame f)
        {
            if (subscribed != f.World)
            {
                if (subscribed != null) subscribed.AttackSeen -= OnAttack;
                subscribed = f.World;
                if (subscribed != null) subscribed.AttackSeen += OnAttack;
            }
            float now = Time.time;
            rules.Forget(now);
            if (SettingsStore.Current.TargetLines != 0 || f.World == null) return;
            Selection selection = f.Commander.Selection;
            candidates.Clear();
            foreach (int id in selection.Selected)
                if (f.World.TryGet(id, out ClientUnitView v)) candidates.Add((id, v.Position));
            foreach (int id in selection.SelectedBuildings)
                if (ClientBuildings.Current.Entries.TryGetValue(id, out ClientBuildings.Entry e) && e.Data.buildingType == BuildingType.GunTurret)
                    candidates.Add((id, Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType))));
            if (candidates.Count == 0) return;
            Vector3 c = f.Cam.transform.position;
            Color32 red = OverlayColours.Alpha(f.Danger, 0.6f), orange = OverlayColours.Alpha(f.Warning, 0.6f), goal = OverlayColours.Alpha(f.Team, 0.45f);
            foreach (int k in rules.Nearest(candidates, new float2(c.x, c.y), rules.Max))
            {
                (int id, float2 from) = candidates[k];
                if (selection.AttackTarget(id, out int ordered, out float2 lastSeen))
                {
                    // v0.18 (Figma O1): solid and red while the Attack order holds, to the target or the last tile the side saw it on.
                    bool seen = f.World.TryTargetPosition(ordered, out Vector3 at) || TryBuildingCentre(ordered, out at);
                    if (seen) selection.SawAttackTarget(id, ordered, lastSeen = new float2(at.x, at.y));
                    else if (!f.World.IsMoving(id)) { selection.ForgetAttackTarget(id); continue; }
                    f.Mesh.Segment(from, lastSeen, 1.5f * f.Px, OverlayColours.Alpha(f.Danger, 0.8f));
                    continue;
                }
                if (rules.TryTarget(id, now, out int target) && f.World.TryTargetPosition(target, out Vector3 to))
                    f.Mesh.Segment(from, new float2(to.x, to.y), f.Px, TargetLineRules.KindOf(target) == TargetLineKind.Wall ? orange : red);
                else if (selection.AttackMoveGoal(id, out int2 g) && f.World.IsMoving(id))
                    f.Mesh.Segment(from, (float2)g + 0.5f, f.Px, goal, 4f * f.Px);
            }
        }

        private static bool TryBuildingCentre(int id, out Vector3 at)
        {
            at = default;
            if (!ClientBuildings.Current.Entries.TryGetValue(id, out ClientBuildings.Entry e) || e.Ghost) return false;
            float2 c = Footprint.VisualCenter((int2)math.round(e.Data.position), WorldStateManager.GetBuildingSize(e.Data.buildingType));
            at = new Vector3(c.x, c.y, 0f);
            return true;
        }

        /// <summary>Stops listening (the overlay is destroyed).</summary>
        public void Dispose()
        {
            if (subscribed != null) subscribed.AttackSeen -= OnAttack;
            subscribed = null;
        }
    }
}
