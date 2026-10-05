using System.Collections;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The <c>fog</c> bench: line-of-sight fog at 5 Hz on the 80k-unit battle. It runs the Task 6
    /// simulation tick and measures the fog that rides on it, so the source layout, the movement between
    /// two teams' refreshes and the changed-cell counts are all the real ones.
    ///
    /// <para><b>What feeds it.</b> The battle is the scenario at the plan's 80k units, eight players (the
    /// scenario always places eight; <c>-teams</c> only picks the mapping), in
    /// <see cref="SpikePlacement.Fronts"/> placement - both armies of every pair start interleaved at
    /// their shared front, so the fight is inside the sampled window from the first tick, which is why
    /// this bench, like the hash bench, does not need Task 6's 1200-tick re-window. The sim is ticked for
    /// <c>-warmup</c> ticks first. Units then come from the sim's gathered SoA -
    /// <c>SpikeSim.Data</c>'s <c>Positions</c>, <c>Team</c> (the player index), <c>Health</c> (a slot at or
    /// past the live count is dead and skipped) and <c>Capacity</c>; buildings come from Task 3's scenario
    /// arrays, <c>BuildingPositions</c>, <c>BuildingOwners</c> (the player index), <c>BuildingHealth</c>
    /// and <c>BuildingCount</c>, with the radius <c>-bvision</c> asks for. The building markers are 1x1
    /// and do not block sight; the map's tiles are the only blockers.</para>
    ///
    /// <para><b>What the measured section contains.</b> The fog only: one timed
    /// <see cref="FogSystem.Tick"/> per 20 Hz tick, with the sim's own tick (untimed) between them. The
    /// fog's jobs are complete when the sample ends, as the sim bench's sync rows are.</para>
    ///
    /// <para><b>Metrics.</b> <c>fog.sources</c> (deduplicated sources per team), <c>fog.sources.raw</c>
    /// (units and buildings examined per team) and <c>fog.sources.saved</c> (the share the dedup drops),
    /// <c>fog.team</c> (one team's full update), <c>fog.slice</c> (the per-tick slice: two team updates a
    /// tick in FFA, one every other tick in 4v4) and <c>fog.slice.busy</c> (the same samples without the
    /// ticks that refreshed no team, which is the honest per-tick cost in 4v4), and <c>fog.changed</c>
    /// (changed cells per team update).</para>
    /// </summary>
    public static class FogBench
    {
        /// <summary>Ticks the fog runs, untimed, before the first sample pays for Burst.</summary>
        private const int FogWarmup = 20;

        /// <summary>Extra unit slots above the starting army, for the churn's transient overshoot.</summary>
        private const int CapacitySlack = 2048;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => SpikeDriver.Benches["fog"] = Run;

        public static IEnumerator Run(SpikeArgs a)
        {
            int teams = FogTeams.Count(a.Teams);
            int sliceTicks = math.max(1, a.Ticks);
            SpikeMap map = MapGenerator.Generate(a.MapSize, (uint)a.Seed, Allocator.Persistent);
            try
            {
                // The scenario: the plan's eight players and 80k units, already engaged at each front.
                var build = SpikeScenarioConfig.Defaults;
                build.Players = MapGenerator.HqCount;
                build.UnitsPerPlayer = math.max(1, a.Units / build.Players);
                build.LargePercent = a.LargePercent;
                build.Placement = SpikePlacement.Fronts;
                build.OrderIntervalTicks = 0; // no order stream: the fog benchmark is about the layout
                build.ScriptTicks = 0;
                using var scenario = SpikeScenario.Create(build, map, Allocator.Persistent);

                var config = SpikeSimConfig.Defaults;
                config.Players = build.Players;
                config.Capacity = scenario.UnitCount + CapacitySlack;
                config.IdCapacity = config.Capacity + SpikeSimRules.SpawnPerTick * config.Players * (a.Warmup + sliceTicks) + 64;
                config.Slice = a.SliceTicks;
                config.CellSize = a.CellSize;
                config.SpawnPerTick = SpikeSimRules.SpawnPerTick;
                config.SpawnTarget = scenario.UnitsPerPlayer; // each army is kept at its starting size
                config.SpawnOrigins = scenario.HqSites;
                using var sim = SpikeSim.Create(config, map, Allocator.Persistent); // no orders, so no fields
                sim.AddScenario(scenario);

                // Warm the battle up: the sim reaches its steady shape (crowd, churn, casualties) before
                // the fog measures it.
                for (int tick = 0; tick < a.Warmup; tick++) sim.Tick();
                sim.Complete();

                var fogConfig = FogConfig.Defaults;
                fogConfig.Teams = teams;
                fogConfig.UnitVision = math.max(1, a.VisionRadius);
                fogConfig.BuildingVision = math.max(1, a.BuildingVision);
                using var fog = new FogSystem(fogConfig, map.Width, map.Height, map.Tiles, Allocator.Persistent);
                fog.SetUnits(sim.Data.Positions, sim.Data.Team, sim.Data.Health, sim.Data.Capacity);
                fog.SetBuildings(scenario.BuildingPositions, scenario.BuildingOwners, scenario.BuildingHealth, scenario.BuildingCount);

                Debug.Log($"[Fog] {scenario.UnitCount} units from the sim's SoA after {a.Warmup} ticks of " +
                          $"{scenario.Placement} placement, {scenario.BuildingCount} building markers from the scenario, " +
                          $"{teams} teams, unit vision {fogConfig.UnitVision}, building vision {fogConfig.BuildingVision}, " +
                          $"map {map.Width}^2");
                yield return null;

                Measure(a, sim, fog, sliceTicks);
            }
            finally
            {
                map.Dispose();
            }
        }

        /// <summary>
        /// The measured section: the sim ticks (untimed) and the fog's tick is timed. Every fog tick
        /// refreshes <c>Teams / 4</c> teams, so this samples one team's full update per refreshed team and
        /// one slice per tick.
        /// </summary>
        private static void Measure(SpikeArgs a, SpikeSim sim, FogSystem fog, int sliceTicks)
        {
            var sliceStats = new SpikeStats();
            var busyStats = new SpikeStats();
            var teamStats = new SpikeStats();
            var changedStats = new SpikeStats();
            var sw = Stopwatch.StartNew();

            for (int sample = 0; sample < sliceTicks + FogWarmup; sample++)
            {
                sim.Tick(); // the battle moves on under the fog; its cost is the sim bench's, not this one's

                sw.Restart();
                fog.Tick();
                sw.Stop();
                double slice = sw.Elapsed.TotalMilliseconds;

                if (sample < FogWarmup) continue;
                sliceStats.Add(slice);
                if (fog.LastTickTeams > 0) busyStats.Add(slice);
                for (int i = 0; i < fog.LastTickTeams; i++)
                {
                    int team = fog.LastTickTeam(i);
                    teamStats.Add(fog.TeamMilliseconds(team));
                    changedStats.Add(fog.Changed(team).Length);
                }
            }

            // The source counts of every team's last update; the dedup saving is per team too.
            var sourceStats = new SpikeStats();
            var rawStats = new SpikeStats();
            var savedStats = new SpikeStats();
            for (int team = 0; team < fog.Teams; team++)
            {
                int sources = fog.SourceCount(team), raw = fog.RawSourceCount(team);
                sourceStats.Add(sources);
                rawStats.Add(raw);
                savedStats.Add(raw == 0 ? 0.0 : 1.0 - (double)sources / raw);
            }

            // The population the fog actually saw, per team: the SoA's healthy slots (a slot at or past
            // the live count keeps the previous tick's values, which is what the sim's own stages read
            // too - see the sim bench's stale-slot note) and the dedup's effect on them.
            int healthy = 0;
            var perTeam = new System.Text.StringBuilder();
            for (int i = 0; i < sim.Data.Capacity; i++)
                if (sim.Data.Health[i] > 0f) healthy++;
            for (int team = 0; team < fog.Teams; team++)
                perTeam.Append($" {team}:{fog.SourceCount(team)}/{fog.RawSourceCount(team)}");
            Debug.Log($"[Fog] {healthy} healthy unit slots of {sim.Data.Capacity}; sources/raw per team{perTeam}");

            SpikeResults.Write(a, "fog.sources", "sources", sourceStats);
            SpikeResults.Write(a, "fog.sources.raw", "sources", rawStats);
            SpikeResults.Write(a, "fog.sources.saved", "share", savedStats);
            SpikeResults.Write(a, "fog.team", "ms", teamStats);
            SpikeResults.Write(a, "fog.slice", "ms", sliceStats);
            SpikeResults.Write(a, "fog.slice.busy", "ms", busyStats);
            SpikeResults.Write(a, "fog.changed", "cells", changedStats);

            Debug.Log($"[Fog] {fog.Teams} teams: sources {sourceStats.Mean:F0} of {rawStats.Mean:F0} " +
                      $"({savedStats.Mean:P0} saved), team {teamStats.Percentile(50):F3} ms p50, " +
                      $"slice {sliceStats.Percentile(50):F3}/{sliceStats.Percentile(95):F3} ms p50/p95 " +
                      $"(busy {busyStats.Percentile(95):F3}, {busyStats.Count} of {sliceStats.Count} ticks), " +
                      $"changed {changedStats.Mean:F0} cells a team update");
        }
    }
}
