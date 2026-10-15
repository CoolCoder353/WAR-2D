using System;
using System.Collections.Generic;
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
/// owner only, carrying the Radar's own tile and nothing about the enemy. Main thread, on the settled world.
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
        foreach (RadarView radar in radars)
        {
            watches.TryGetValue(radar.Id, out RadarWatch watch);
            int slot = data.OwnerSlotOf(radar.OwnerId);
            bool enemy = slot != SimData.NoSlot && EnemyInRange(data, unitCount, radar.Centre, radar.Radius, slot);
            if (RadarRules.Step(ref watch, enemy, now, clearSeconds, cooldownSeconds))
                emit(radar.OwnerId, new Alert { Kind = AlertKind.EnemySpotted, Tile = radar.Tile });
            watches[radar.Id] = watch;
        }
    }

    /// <summary>Drops a Radar's state (destroyed).</summary>
    public void Forget(int radarId) => watches.Remove(radarId);

    /// <summary>True when a living unit within <paramref name="radius"/> of the centre belongs to an owner slot that <paramref name="ownerSlot"/> attacks.</summary>
    public static bool EnemyInRange(SimData data, int unitCount, float2 centre, float radius, int ownerSlot)
    {
        float rSq = radius * radius;
        int2 lo = math.clamp((int2)math.floor((centre - radius) / data.CellSize), 0, new int2(data.CellsX - 1, data.CellsY - 1));
        int2 hi = math.clamp((int2)math.floor((centre + radius) / data.CellSize), 0, new int2(data.CellsX - 1, data.CellsY - 1));
        for (int cy = lo.y; cy <= hi.y; cy++)
        for (int cx = lo.x; cx <= hi.x; cx++)
        {
            int cell = cy * data.CellsX + cx;
            for (int k = data.CellStart[cell]; k < data.CellStart[cell + 1]; k++)
            {
                int u = data.Sorted[k];
                if (u >= unitCount || data.Health[u] <= 0f || math.distancesq(data.Positions[u], centre) > rSq) continue;
                if (SimData.Attacks(data.AttackMask, ownerSlot, data.OwnerSlot[u])) return true;
            }
        }
        return false;
    }
}
