using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Client;
using WAR2D.Client.Render;
using WAR2D.Net.Replication;

/// <summary>
/// The unit draw job (<see cref="ClientWorld.PrepareUnitsJob"/>) gives the same on the job threads as on one thread, and its gather
/// puts the lists in unit order.
/// </summary>
public class DrawJobTests
{
    private const int Capacity = 4096, Count = 3000;

    [Test]
    public void TheDrawJobOnTheJobThreadsMatchesOneThreadUnitForUnitAndInUnitOrder()
    {
        using var single = new Inputs(7);
        using var parallel = new Inputs(7);

        single.Job().Run(Count);
        single.Gather().Run();
        parallel.Gather().Schedule(parallel.Job().Schedule(Count, 16)).Complete();

        Assert.That(single.Hits.Length, Is.GreaterThan(0), "no hits: the data doesn't exercise them");
        Assert.That(single.Tracks.Length, Is.GreaterThan(0), "no track marks: the data doesn't exercise them");
        Assert.That(single.Turrets.Length, Is.GreaterThan(0), "no turrets: the data doesn't exercise them");
        Assert.That(single.BarrelShadows.Length, Is.GreaterThan(0), "no barrel shadows: the data doesn't exercise them");
        for (int k = 0; k < single.ByIndex.Count; k++)
            CollectionAssert.AreEqual(single.ByIndex[k](), parallel.ByIndex[k](), $"by-index array {k}");
        // The gather's order, checked apart from the gather: every slot its bit names, by ascending unit.
        var hits = new List<ClientWorld.UnitHit>();
        var tracks = new List<float3>();
        var turrets = new List<UnitInstance>();
        var shadows = new List<UnitInstance>();
        for (int i = 0; i < Count; i++)
        {
            byte emit = single.Emits[i];
            if ((emit & ClientWorld.PrepareUnitsJob.EmitHit) != 0) hits.Add(single.HitSlots[i]);
            if ((emit & ClientWorld.PrepareUnitsJob.EmitTrack) != 0) tracks.Add(single.TrackSlots[i]);
            if ((emit & ClientWorld.PrepareUnitsJob.EmitTurret) != 0) turrets.Add(single.TurretSlots[i]);
            if ((emit & ClientWorld.PrepareUnitsJob.EmitShadow) != 0) shadows.Add(single.ShadowSlots[i]);
        }
        CollectionAssert.AreEqual(hits, single.Hits.AsArray().ToArray(), "hits in unit order");
        CollectionAssert.AreEqual(tracks, single.Tracks.AsArray().ToArray(), "track marks in unit order");
        CollectionAssert.AreEqual(turrets, single.Turrets.AsArray().ToArray(), "turrets in unit order");
        CollectionAssert.AreEqual(shadows, single.BarrelShadows.AsArray().ToArray(), "barrel shadows in unit order");
        CollectionAssert.AreEqual(single.Hits.AsArray().ToArray(), parallel.Hits.AsArray().ToArray(), "hits");
        CollectionAssert.AreEqual(single.Tracks.AsArray().ToArray(), parallel.Tracks.AsArray().ToArray(), "track marks");
        CollectionAssert.AreEqual(single.Turrets.AsArray().ToArray(), parallel.Turrets.AsArray().ToArray(), "turrets");
        CollectionAssert.AreEqual(single.BarrelShadows.AsArray().ToArray(), parallel.BarrelShadows.AsArray().ToArray(), "barrel shadows");
    }

    /// <summary>One set of draw inputs made from a seed: moving and still units, hits, new ids, turrets aiming and not, hidden ones.</summary>
    private sealed class Inputs : IDisposable
    {
        private readonly List<IDisposable> owned = new List<IDisposable>();
        /// <summary>Every by-index output, read back as a managed array.</summary>
        public readonly List<Func<Array>> ByIndex = new List<Func<Array>>();
        private NativeArray<int> known, owners, trackedId, trackIds, turretOf;
        private NativeArray<float2> positions, lastPosition, lastSteer, lastTracks, aimAt;
        private NativeArray<byte> types, health, selected, hidden, lastHealth, flash, emits;
        private NativeArray<PredictState> states;
        private NativeArray<float> lastAction, scaleByType, rotation, health01, drawScale, hitAt, trail, turretAngle, turretTurnedAt, aimUntil;
        private NativeArray<uint> frame, color;
        private NativeArray<UnitFrames.TypeCells> cells;
        private NativeHashMap<int, uint> colours;
        private NativeArray<ClientWorld.UnitHit> hitSlots;
        private NativeArray<float3> trackSlots;
        private NativeArray<UnitInstance> turretSlots, shadowSlots;
        public NativeArray<byte> Emits => emits;
        public NativeArray<ClientWorld.UnitHit> HitSlots => hitSlots;
        public NativeArray<float3> TrackSlots => trackSlots;
        public NativeArray<UnitInstance> TurretSlots => turretSlots;
        public NativeArray<UnitInstance> ShadowSlots => shadowSlots;
        public NativeList<ClientWorld.UnitHit> Hits;
        public NativeList<float3> Tracks;
        public NativeList<UnitInstance> Turrets, BarrelShadows;

        private const float Now = 50f, ServerTime = 100f;

        public Inputs(int seed)
        {
            var r = new System.Random(seed);
            int typeCount = Enum.GetValues(typeof(UnitType)).Length;
            known = Make<int>(Count, false);
            positions = Make<float2>(Count, false);
            owners = Make<int>(Capacity, false); types = Make<byte>(Capacity, false); health = Make<byte>(Capacity, false);
            states = Make<PredictState>(Capacity, false); lastAction = Make<float>(Capacity, false); selected = Make<byte>(Capacity, false);
            hidden = Make<byte>(Capacity, false); aimAt = Make<float2>(Capacity, false);
            lastPosition = Make<float2>(Capacity, true); lastSteer = Make<float2>(Capacity, true); rotation = Make<float>(Capacity, true); frame = Make<uint>(Capacity, true);
            color = Make<uint>(Capacity, true); health01 = Make<float>(Capacity, true); drawScale = Make<float>(Capacity, true);
            trackedId = Make<int>(Capacity, true); lastHealth = Make<byte>(Capacity, true); flash = Make<byte>(Capacity, true);
            hitAt = Make<float>(Capacity, true); trail = Make<float>(Capacity, true); trackIds = Make<int>(Capacity, true);
            lastTracks = Make<float2>(Capacity, true); turretOf = Make<int>(Capacity, true); turretAngle = Make<float>(Capacity, true);
            turretTurnedAt = Make<float>(Capacity, true); aimUntil = Make<float>(Capacity, true);
            emits = Make<byte>(Count, false);
            hitSlots = Make<ClientWorld.UnitHit>(Count, false); trackSlots = Make<float3>(Count, false);
            turretSlots = Make<UnitInstance>(Count, false); shadowSlots = Make<UnitInstance>(Count, false);
            scaleByType = Make<float>(typeCount, false);
            cells = Make<UnitFrames.TypeCells>(typeCount, false);
            colours = new NativeHashMap<int, uint>(4, Allocator.Persistent);
            owned.Add(colours);
            Hits = new NativeList<ClientWorld.UnitHit>(16, Allocator.Persistent); owned.Add(Hits);
            Tracks = new NativeList<float3>(16, Allocator.Persistent); owned.Add(Tracks);
            Turrets = new NativeList<UnitInstance>(16, Allocator.Persistent); owned.Add(Turrets);
            BarrelShadows = new NativeList<UnitInstance>(16, Allocator.Persistent); owned.Add(BarrelShadows);

            colours[1] = 0xFF0000FFu;
            colours[2] = 0x00FF00FFu; // owner 3 has no colour yet: the unknown grey
            for (int t = 0; t < typeCount; t++)
            {
                scaleByType[t] = t % 3 == 0 ? 2f : 1f;
                bool turret = t % 2 == 0 || t == (int)UnitType.Artillery;
                cells[t] = new UnitFrames.TypeCells
                {
                    HullFirst = t * 100, HullFrames = 4, Directions = 8,
                    TurretFirst = turret ? 1000 + t * 64 : -1, TurretFrames = 3, TurretTiles = 1 + t % 2,
                };
            }
            var indices = new List<int>(Capacity);
            for (int i = 0; i < Capacity; i++) indices.Add(i);
            for (int i = Capacity - 1; i > 0; i--) { int j = r.Next(i + 1); (indices[i], indices[j]) = (indices[j], indices[i]); }
            for (int i = 0; i < Count; i++)
            {
                int index = indices[i], id = index + 1;
                known[i] = index;
                float2 p = new float2((float)r.NextDouble() * 200f, (float)r.NextDouble() * 200f);
                positions[i] = p;
                // A third still, most moving a step, a few jumping too far to count as moving.
                float2 step = new float2((float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f);
                lastPosition[index] = i % 3 == 0 ? p : i % 17 == 0 ? p - new float2(3f, 0f) : p - step;
                lastSteer[index] = lastPosition[index]; // facing follows the steer position (here the drawn one)
                owners[index] = 1 + r.Next(3);
                types[index] = (byte)r.Next(typeCount);
                states[index] = new PredictState { Id = id };
                health[index] = (byte)(1 + r.Next(100));
                lastHealth[index] = (byte)math.min(100, health[index] + (i % 2 == 0 ? r.Next(20) : 0));
                trackedId[index] = i % 5 == 0 ? -1 : id;
                hitAt[index] = Now - (float)r.NextDouble() * 2f;
                trail[index] = (float)r.NextDouble();
                lastAction[index] = ServerTime - (float)r.NextDouble() * 3f;
                selected[index] = (byte)r.Next(4);
                hidden[index] = (byte)(i % 10 == 0 ? 1 : 0);
                trackIds[index] = i % 4 == 0 ? 0 : id;
                lastTracks[index] = p - new float2((float)r.NextDouble() * 1.5f, (float)r.NextDouble() * 1.5f);
                turretOf[index] = i % 3 == 1 ? 0 : id;
                turretAngle[index] = (float)r.NextDouble() * 6.28f;
                turretTurnedAt[index] = Now - (float)r.NextDouble();
                aimUntil[index] = i % 2 == 0 ? Now + 1f : Now - 1f;
                aimAt[index] = new float2((float)r.NextDouble() * 200f, (float)r.NextDouble() * 200f);
                rotation[index] = (float)r.NextDouble() * 6.28f;
            }
        }

        private NativeArray<T> Make<T>(int length, bool output) where T : struct
        {
            var a = new NativeArray<T>(length, Allocator.Persistent);
            owned.Add(a);
            if (output) ByIndex.Add(() => a.ToArray());
            return a;
        }

        public ClientWorld.PrepareUnitsJob Job() => new ClientWorld.PrepareUnitsJob
        {
            Known = known, Positions = positions, Steer = positions, MaxTurn = 0.2f, Owners = owners, Types = types, Health = health, States = states,
            LastAction = lastAction, Selected = selected, ScaleByType = scaleByType, Hidden = hidden, UseHidden = true,
            Colours = colours, UnknownColour = 0x808080FFu, Cells = cells, HasFrames = true,
            ServerTime = ServerTime, Now = Now, FrameSeconds = 1f / 60f, LocalOwner = 1,
            View = new float4(0f, 0f, 200f, 200f), TurretView = new float4(0f, 0f, 150f, 200f), BarrelShadowFrame = 5000u,
            LastPosition = lastPosition, LastSteer = lastSteer, Rotation = rotation, Frame = frame, Color = color, Health01 = health01, DrawScale = drawScale,
            TrackedId = trackedId, LastHealth = lastHealth, HitAt = hitAt, Trail = trail, Flash = flash,
            TrackIds = trackIds, LastTracks = lastTracks, TurretOf = turretOf, TurretAngle = turretAngle,
            TurretTurnedAt = turretTurnedAt, AimUntil = aimUntil, AimAt = aimAt,
            Emits = emits, HitSlots = hitSlots, TrackSlots = trackSlots, TurretSlots = turretSlots, ShadowSlots = shadowSlots,
        };

        public ClientWorld.GatherUnitsJob Gather() => new ClientWorld.GatherUnitsJob
        {
            Emits = emits, HitSlots = hitSlots, TrackSlots = trackSlots, TurretSlots = turretSlots, ShadowSlots = shadowSlots,
            Hits = Hits, Tracks = Tracks, Turrets = Turrets, BarrelShadows = BarrelShadows,
        };

        public void Dispose()
        {
            foreach (IDisposable d in owned) d.Dispose();
        }
    }
}
