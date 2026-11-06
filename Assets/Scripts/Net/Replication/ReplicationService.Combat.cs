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
        /// <summary>Shells in flight this tick and last, the ones that landed in between, and those near enough landing to be announced.</summary>
        private HashSet<int> shellsInFlight = new HashSet<int>(), shellsBefore = new HashSet<int>();
        private readonly List<int> shellsLanded = new List<int>(), shellsIncoming = new List<int>();
        private readonly HashSet<int> seenStatus = new HashSet<int>();
        private readonly List<int> staleStatus = new List<int>();
        /// <summary>Every unit's status bits now and last tick (by id index), and what changed between them (bits 0: cleared).</summary>
        private Dictionary<int, byte> statusNow = new Dictionary<int, byte>(), statusBefore = new Dictionary<int, byte>();
        private readonly List<int2> statusChanged = new List<int2>();

        /// <summary>Schedules the list of every living unit's status bits (<see cref="StatusListJob"/>) for <see cref="SendCombat"/>.</summary>
        private static JobHandle ScheduleStatuses(SimData data, int tick, int count, NativeList<int2> statuses) => new StatusListJob
        {
            Count = count, IdOf = data.IdOf, IndexOfId = data.IndexOfId, Health = data.Health, Type = data.Type, Combat = data.Combat,
            BlastRadiusByType = data.BlastRadiusByType, AimByType = data.AimByType, Burning = data.BurningUnits, Tick = tick, Out = statuses,
        }.Schedule();

        /// <summary>Builds and sends (or, for a lane client, holds) every active client's combat news for <paramref name="tick"/>.</summary>
        private void SendCombat(SimData data, int tick, in ReplicationInput input, NativeList<int2> statuses, JobHandle statusHandle)
        {
            double splitAt = PerfSplits.Now;
            statusHandle.Complete();
            (statusBefore, statusNow) = (statusNow, statusBefore);
            statusNow.Clear();
            statusChanged.Clear();
            for (int k = 0; k < statuses.Length; k++)
            {
                statusNow[statuses[k].x] = (byte)statuses[k].y;
                if (!statusBefore.TryGetValue(statuses[k].x, out byte was) || was != statuses[k].y) statusChanged.Add(statuses[k]);
            }
            foreach (KeyValuePair<int, byte> pair in statusBefore)
                if (!statusNow.ContainsKey(pair.Key)) statusChanged.Add(new int2(pair.Key, 0));
            PerfSplits.Mark(PerfSplits.ReplCombatStatus, ref splitAt);
            // Shell bookkeeping once for every client (perf gate 2026-11-07): which landed, and which are close enough to landing for
            // a client that never knew the gun to be told where (each client then touches only those, not every shell it was sent).
            int incomingTicks = math.max(1, (int)math.round(config.Artillery.IncomingSeconds / math.max(1e-4f, config.Simulation.TickSeconds)));
            (shellsBefore, shellsInFlight) = (shellsInFlight, shellsBefore);
            shellsInFlight.Clear();
            shellsIncoming.Clear();
            for (int k = 0; k < data.Shells.Length; k++)
            {
                shellsInFlight.Add(data.Shells[k].Id);
                if (tick >= data.Shells[k].LandTick - incomingTicks) shellsIncoming.Add(k);
            }
            shellsLanded.Clear();
            foreach (int id in shellsBefore) if (!shellsInFlight.Contains(id)) shellsLanded.Add(id);

            for (int c = 0; c < MaxClients; c++)
            {
                if (!active[c]) continue;
                Client client = clients[c];
                int owner = client.OwnerId, grid = GridFor(client);
                NativeArray<ulong> allowed = encodeNow[c] ? interests.Allowed(c) : default;
                CombatNews news = combatNews;
                news.Clear();
                news.Tick = tick;

                // Status bits: what changed for units it knows; a unit it no longer knows left (its Leave cleared them). A client the
                // boundary encodes takes only this tick's changes and the units it met or lost (perf gate 2026-11-07: a full diff for
                // every client every tick was 3 ms at 80,000 units); the recorder's lane, or a batch past the cap, takes the full diff.
                if (!encodeNow[c] || !client.StatusSynced || !StatusDelta(c, client, input, allowed, owner, grid, news)) FullStatus(client, statuses, input, allowed, owner, grid, news);

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
                foreach (int k in shellsIncoming)
                {
                    Shell shell = data.Shells[k];
                    if (client.SentShells.Contains(shell.Id) || !input.Sees(grid, shell.To)) continue;
                    client.SentShells.Add(shell.Id);
                    news.Shells.Add(new ShellEntry { Full = false, To = shell.To, Land = shell.LandTick });
                }
                foreach (int id in shellsLanded) client.SentShells.Remove(id);

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

        /// <summary>The full status diff: every unit it knows with bits, against what it was last sent; units it no longer knows dropped.</summary>
        private void FullStatus(Client client, NativeList<int2> statuses, in ReplicationInput input, NativeArray<ulong> allowed, int owner, int grid, CombatNews news)
        {
            seenStatus.Clear();
            bool behind = false;
            for (int k = 0; k < statuses.Length; k++)
            {
                int index = statuses[k].x;
                if (!InterestRules.UnitKnown(input, allowed, index, owner, grid)) continue;
                byte bits = (byte)statuses[k].y;
                seenStatus.Add(index); // past the cap too, so a unit waiting for the next batch isn't taken for one that stopped
                if (client.SentStatus.TryGetValue(index, out byte sent) && sent == bits) continue;
                if (news.Statuses.Count >= MaxStatusesPerBatch) { behind = true; continue; } // the rest go next tick
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
            client.StatusSynced = !behind;
        }

        /// <summary>
        /// The status delta for a client the boundary encodes: forget what it lost (its Leave cleared them on its side), send what it just
        /// met, then this tick's changes for units it knows. False (and nothing changed) when that would pass the cap: take the full diff.
        /// </summary>
        private bool StatusDelta(int c, Client client, in ReplicationInput input, NativeArray<ulong> allowed, int owner, int grid, CombatNews news)
        {
            NativeList<int> entered = interests.Entered(c), left = interests.Left(c);
            if (entered.Length + statusChanged.Count > MaxStatusesPerBatch) return false;
            for (int k = 0; k < left.Length; k++) client.SentStatus.Remove(NetIdAllocator.IndexOf(left[k]));
            for (int k = 0; k < entered.Length; k++)
            {
                int index = NetIdAllocator.IndexOf(entered[k]);
                if (!statusNow.TryGetValue(index, out byte bits) || !InterestRules.UnitKnown(input, allowed, index, owner, grid)) continue;
                client.SentStatus[index] = bits;
                news.Statuses.Add(new StatusEntry { Index = index, Bits = bits });
            }
            foreach (int2 change in statusChanged)
            {
                int index = change.x;
                byte bits = (byte)change.y;
                if (bits != 0)
                {
                    if (!InterestRules.UnitKnown(input, allowed, index, owner, grid)) continue;
                    if (client.SentStatus.TryGetValue(index, out byte sent) && sent == bits) continue; // just met: already sent above
                    client.SentStatus[index] = bits;
                    news.Statuses.Add(new StatusEntry { Index = index, Bits = bits });
                }
                else if (client.SentStatus.Remove(index) && InterestRules.UnitKnown(input, allowed, index, owner, grid))
                    news.Statuses.Add(new StatusEntry { Index = index, Bits = 0 });
            }
            return true;
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
