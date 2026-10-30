using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Sim;

/// <summary>A finished Radar as the service sees it.</summary>
public struct RadarView
{
    public int Id, OwnerId;
    /// <summary>The Radar's anchor tile: what a ping reports (the owner knows it already).</summary>
    public int2 Tile;
    public float2 Centre;
    public float Radius;
}

/// <summary>
/// Server-side Radar contact watch: one alert per enemy contact (see <see cref="RadarRules"/>), to the Radar's
/// owner only, carrying the Radar's own tile and nothing about the enemy. On the settled world (the scan is a Burst job).
/// </summary>
public sealed class RadarService
{
    private readonly float clearSeconds, cooldownSeconds;
    private readonly Dictionary<int, RadarWatch> watches = new Dictionary<int, RadarWatch>();

    public RadarService(float clearSeconds, float cooldownSeconds)
    {
        this.clearSeconds = clearSeconds;
        this.cooldownSeconds = cooldownSeconds;
    }

    /// <summary>Steps every listed Radar and emits <c>EnemySpotted</c> for each one that pings.</summary>
    public void Update(SimData data, int unitCount, IReadOnlyList<RadarView> radars, double now, Action<int, Alert> emit)
    {
        if (radars.Count == 0) return;
        // The scans run as one Burst job: in managed code, 40 Radars over their owners' packed armies took ~6 ms (docs/perf/v0.12).
        var centres = new NativeArray<float2>(radars.Count, Allocator.TempJob);
        var radii = new NativeArray<float>(radars.Count, Allocator.TempJob);
        var slots = new NativeArray<int>(radars.Count, Allocator.TempJob);
        var seen = new NativeArray<byte>(radars.Count, Allocator.TempJob);
        for (int r = 0; r < radars.Count; r++)
        {
            centres[r] = radars[r].Centre;
            radii[r] = radars[r].Radius;
            slots[r] = data.OwnerSlotOf(radars[r].OwnerId);
        }
        new EnemyInRangeJob
        {
            Centres = centres, Radii = radii, OwnerSlots = slots, Seen = seen, UnitCount = unitCount,
            CellSize = data.CellSize, CellsX = data.CellsX, CellsY = data.CellsY, CellStart = data.CellStart, Sorted = data.Sorted,
            Positions = data.Positions, Health = data.Health, OwnerSlot = data.OwnerSlot, AttackMask = data.AttackMask,
            JamMask = data.JamMask, FogCellSize = data.FogCellSize, FogW = data.FogW, FogH = data.FogH, FogCells = data.FogCells,
        }.Schedule(radars.Count, 1).Complete();
        for (int r = 0; r < radars.Count; r++)
        {
            RadarView radar = radars[r];
            watches.TryGetValue(radar.Id, out RadarWatch watch);
            if (RadarRules.Step(ref watch, seen[r] != 0, now, clearSeconds, cooldownSeconds))
                emit(radar.OwnerId, new Alert { Kind = AlertKind.EnemySpotted, Tile = radar.Tile });
            watches[radar.Id] = watch;
        }
        centres.Dispose(); radii.Dispose(); slots.Dispose(); seen.Dispose();
    }

    /// <summary>
    /// One Radar per index: 1 when a living unit of an owner the Radar's owner attacks is within its radius, on a fog
    /// cell that isn't jammed for the Radar owner's grid (v0.13: a Radar never pings for a contact in a jam).
    /// </summary>
    [BurstCompile]
    private struct EnemyInRangeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Centres;
        [ReadOnly] public NativeArray<float> Radii;
        [ReadOnly] public NativeArray<int> OwnerSlots;
        public NativeArray<byte> Seen;
        public int UnitCount, CellSize, CellsX, CellsY;
        [ReadOnly] public NativeArray<int> CellStart, Sorted;
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> OwnerSlot;
        [ReadOnly] public NativeArray<ushort> AttackMask;
        [ReadOnly] public NativeArray<byte> JamMask;
        public int FogCellSize, FogW, FogH, FogCells;

        public void Execute(int r)
        {
            int ownerSlot = OwnerSlots[r];
            if (ownerSlot == SimData.NoSlot) return;
            float2 centre = Centres[r];
            float radius = Radii[r], rSq = radius * radius;
            int2 lo = math.clamp((int2)math.floor((centre - radius) / CellSize), 0, new int2(CellsX - 1, CellsY - 1));
            int2 hi = math.clamp((int2)math.floor((centre + radius) / CellSize), 0, new int2(CellsX - 1, CellsY - 1));
            for (int cy = lo.y; cy <= hi.y; cy++)
            for (int cx = lo.x; cx <= hi.x; cx++)
            {
                int cell = cy * CellsX + cx;
                for (int k = CellStart[cell]; k < CellStart[cell + 1]; k++)
                {
                    int u = Sorted[k];
                    if (u >= UnitCount || Health[u] <= 0f || !SimData.Attacks(AttackMask, ownerSlot, OwnerSlot[u]) || math.distancesq(Positions[u], centre) > rSq) continue;
                    int2 f = math.clamp((int2)math.floor(Positions[u] / FogCellSize), 0, new int2(FogW - 1, FogH - 1));
                    if (JamMask[ownerSlot * FogCells + f.y * FogW + f.x] != 0) continue;
                    Seen[r] = 1;
                    return;
                }
            }
        }
    }

    /// <summary>Drops a Radar's state (destroyed).</summary>
    public void Forget(int radarId) => watches.Remove(radarId);
}
