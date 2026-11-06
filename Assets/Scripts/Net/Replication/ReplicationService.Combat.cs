using System.Collections.Generic;
using Mirror;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Sim;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// v0.20 Onslaught: each client's <see cref="CombatBatch"/> (spec §12), built from the settled world after the tick's unit
    /// batches. The leak rule (spec §1): status bits, leaps and aims only for units the client knows; an aim names its target only
    /// when the client may know it; a shell's whole flight only to a client that knew its gun when it fired, and to one that sees
    /// only where it lands just its last <c>Artillery/IncomingSeconds</c>, from that point alone; nothing to anyone else.
    /// </summary>
    public sealed partial class ReplicationService
    {
        /// <summary>Most status entries one batch carries; the rest go next tick.</summary>
        private const int MaxStatusesPerBatch = 4096;

        private readonly CombatNews combatNews = new CombatNews();
        private readonly HashSet<int> shellsInFlight = new HashSet<int>();
        private readonly HashSet<int> seenStatus = new HashSet<int>();
        private readonly List<int> staleStatus = new List<int>();
        private readonly List<int> staleShells = new List<int>();

        /// <summary>Builds and sends (or, for a lane client, holds) every active client's combat news for <paramref name="tick"/>.</summary>
        private void SendCombat(SimData data, int tick, in ReplicationInput input)
        {
            using var statuses = new NativeList<int2>(256, Allocator.TempJob);
            new StatusListJob
            {
                Count = input.Count, IdOf = data.IdOf, IndexOfId = data.IndexOfId, Health = data.Health, Type = data.Type, Combat = data.Combat,
                BlastRadiusByType = data.BlastRadiusByType, AimByType = data.AimByType, Burning = data.BurningUnits, Tick = tick, Out = statuses,
            }.Run();
            shellsInFlight.Clear();
            for (int k = 0; k < data.Shells.Length; k++) shellsInFlight.Add(data.Shells[k].Id);
            int incomingTicks = math.max(1, (int)math.round(config.Artillery.IncomingSeconds / math.max(1e-4f, config.Simulation.TickSeconds)));

            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                Client client = clients[c];
                int owner = client.OwnerId, grid = GridFor(client);
                NativeArray<ulong> allowed = encodeNow[c] ? interests.Allowed(c) : default;
                CombatNews news = combatNews;
                news.Clear();
                news.Tick = tick;

                // Status bits: what changed for units it knows; a unit it no longer knows left (its Leave cleared them).
                seenStatus.Clear();
                for (int k = 0; k < statuses.Length; k++)
                {
                    int index = statuses[k].x;
                    if (!InterestRules.UnitKnown(input, allowed, index, owner, grid)) continue;
                    byte bits = (byte)statuses[k].y;
                    seenStatus.Add(index); // past the cap too, so a unit waiting for the next batch isn't taken for one that stopped
                    if (client.SentStatus.TryGetValue(index, out byte sent) && sent == bits) continue;
                    if (news.Statuses.Count >= MaxStatusesPerBatch) continue; // the rest go next tick
                    client.SentStatus[index] = bits;
                    news.Statuses.Add(new StatusEntry { Index = index, Bits = bits });
                }
                staleStatus.Clear();
                foreach (KeyValuePair<int, byte> pair in client.SentStatus)
                    if (!seenStatus.Contains(pair.Key)) staleStatus.Add(pair.Key);
                foreach (int index in staleStatus)
                {
                    client.SentStatus.Remove(index);
                    if (InterestRules.UnitKnown(input, allowed, index, owner, grid)) news.Statuses.Add(new StatusEntry { Index = index, Bits = 0 });
                }

                for (int k = 0; k < data.Leaps.Length; k++)
                {
                    LeapEvent leap = data.Leaps[k];
                    int index = NetIdAllocator.IndexOf(leap.Id);
                    if (InterestRules.UnitKnown(input, allowed, index, owner, grid))
                        news.Leaps.Add(new LeapEntry { Index = index, From = leap.From, To = leap.To, Start = leap.Start, End = leap.End });
                }

                for (int k = 0; k < data.AimEvents.Length; k++)
                {
                    int2 aim = data.AimEvents[k];
                    int index = NetIdAllocator.IndexOf(aim.x);
                    if (!InterestRules.UnitKnown(input, allowed, index, owner, grid)) continue;
                    int named = aim.y > 0 && InterestRules.MayKnowTarget(input, allowed, aim.y, BuildingOf(data, aim.y), owner, grid) ? aim.y : 0;
                    news.Aims.Add(new AimEntry { Index = index, TargetId = named });
                }

                for (int k = 0; k < data.ShellsLaunched.Length; k++)
                {
                    Shell shell = data.ShellsLaunched[k];
                    if (!InterestRules.UnitKnown(input, allowed, NetIdAllocator.IndexOf(shell.ArtilleryId), owner, grid)) continue;
                    client.SentShells.Add(shell.Id);
                    news.Shells.Add(new ShellEntry { Full = true, From = shell.From, To = shell.To, Launch = shell.LaunchTick, Land = shell.LandTick });
                }
                for (int k = 0; k < data.Shells.Length; k++)
                {
                    Shell shell = data.Shells[k];
                    if (tick < shell.LandTick - incomingTicks || client.SentShells.Contains(shell.Id) || !input.Sees(grid, shell.To)) continue;
                    client.SentShells.Add(shell.Id);
                    news.Shells.Add(new ShellEntry { Full = false, To = shell.To, Land = shell.LandTick });
                }
                staleShells.Clear();
                foreach (int id in client.SentShells) if (!shellsInFlight.Contains(id)) staleShells.Add(id);
                foreach (int id in staleShells) client.SentShells.Remove(id);

                if (news.Empty) continue;
                writer.Position = 0;
                CombatCodec.Write(writer, news);
                if (client.Lane != null)
                {
                    // Its units go out a tick late: hold the news until they have (FlushPendingCombat).
                    client.PendingCombat = writer.ToArray();
                    client.PendingCombatTick = tick;
                    continue;
                }
                System.ArraySegment<byte> payload = writer.ToArraySegment();
                client.BytesSent += payload.Count;
                if (client.VirtualSink != null) client.VirtualSink(payload, CombatSinkChannel);
                else client.Connection.Send(new CombatBatch { Payload = payload }, Channels.Reliable);
            }
        }

        /// <summary>Sends a lane client's held combat news, once its units of that tick have gone out.</summary>
        private void FlushPendingCombat(Client client)
        {
            if (client.PendingCombat == null) return;
            byte[] payload = client.PendingCombat;
            client.PendingCombat = null;
            int previous = SinkTick;
            SinkTick = client.PendingCombatTick;
            client.BytesSent += payload.Length;
            if (client.VirtualSink != null) client.VirtualSink(new System.ArraySegment<byte>(payload), CombatSinkChannel);
            else client.Connection.Send(new CombatBatch { Payload = new System.ArraySegment<byte>(payload) }, Channels.Reliable);
            SinkTick = previous;
        }
    }

    /// <summary>
    /// v0.20: every living unit with a status bit set, as (id index, bits). Only a Bomber can leap and only a Sniper aim, so only their
    /// records are read in the pass over the units; burning units come from <see cref="SimData.BurningUnits"/>.
    /// </summary>
    [BurstCompile]
    public struct StatusListJob : IJob
    {
        public int Count, Tick;
        [Unity.Collections.ReadOnly] public NativeArray<int> IdOf, IndexOfId;
        [Unity.Collections.ReadOnly] public NativeArray<float> Health, BlastRadiusByType, AimByType;
        [Unity.Collections.ReadOnly] public NativeArray<byte> Type;
        [Unity.Collections.ReadOnly] public NativeArray<UnitCombat> Combat;
        [Unity.Collections.ReadOnly] public NativeList<int> Burning;
        public NativeList<int2> Out;

        private bool Special(int type) => (type < BlastRadiusByType.Length && BlastRadiusByType[type] > 0f) || (type < AimByType.Length && AimByType[type] > 0f);

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Health[i] <= 0f || !Special(Type[i])) continue;
                Add(i);
            }
            for (int k = 0; k < Burning.Length; k++)
            {
                int index = NetIdAllocator.IndexOf(Burning[k]);
                int i = (uint)index < (uint)IndexOfId.Length ? IndexOfId[index] : -1;
                if ((uint)i >= (uint)Count || IdOf[i] != Burning[k] || Health[i] <= 0f || Special(Type[i])) continue; // the special ones are in already
                Add(i);
            }
        }

        private void Add(int i)
        {
            int id = IdOf[i], index = NetIdAllocator.IndexOf(id);
            if ((uint)index >= (uint)Combat.Length) return;
            byte bits = Combat[index].StatusAt(id, Tick);
            if (bits != 0) Out.Add(new int2(index, bits));
        }
    }
}
