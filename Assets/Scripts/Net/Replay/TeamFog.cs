using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using WAR2D.Net.Replication;

namespace WAR2D.Net.Replay
{
    /// <summary>
    /// Finds each player's changed fog cells for the recording (v0.18) and run-length encodes them. Each grid already holds the
    /// player's whole view (radar, jammers, shared vision), so the per-player view shows exactly what the server had. Bursted: it
    /// runs at the settled boundary, once every <c>Replays/FogSeconds</c>.
    /// Payload: for each player with changes, int owner, varint run count, then runs of (varint skip, varint length, byte state).
    /// </summary>
    [BurstCompile]
    public struct TeamFogEncodeJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Visible, Explored;
        [ReadOnly] public NativeArray<int> OwnerBySlot;
        public NativeArray<byte> Last;
        public NativeList<byte> Out;
        public int FogCells, Slots;

        public void Execute()
        {
            for (int slot = 0; slot < Slots; slot++)
            {
                int owner = OwnerBySlot[slot];
                if (owner == 0) continue;
                int grid = slot * FogCells;
                int runs = 0, cell = 0, previousEnd = 0;
                var body = new NativeList<byte>(256, Allocator.Temp);
                while (cell < FogCells)
                {
                    byte state = State(grid + cell);
                    if (state == Last[grid + cell]) { cell++; continue; }
                    int start = cell;
                    while (cell < FogCells && State(grid + cell) == state && state != Last[grid + cell]) { Last[grid + cell] = state; cell++; }
                    Varint(ref body, start - previousEnd);
                    Varint(ref body, cell - start);
                    body.Add(state);
                    previousEnd = cell;
                    runs++;
                }
                if (runs > 0)
                {
                    Out.Add((byte)owner); Out.Add((byte)(owner >> 8)); Out.Add((byte)(owner >> 16)); Out.Add((byte)(owner >> 24));
                    var header = Out;
                    Varint(ref header, runs);
                    Out.AddRange(body.AsArray());
                }
                body.Dispose();
            }
        }

        private byte State(int i) => Visible[i] != 0 ? (byte)FogState.Visible : Explored[i] != 0 ? (byte)FogState.Explored : (byte)FogState.Unexplored;

        private static void Varint(ref NativeList<byte> to, int value)
        {
            uint v = (uint)value;
            while (v >= 0x80) { to.Add((byte)(v | 0x80)); v >>= 7; }
            to.Add((byte)v);
        }
    }

    /// <summary>Applies recorded team-fog payloads to one state array per player. Pure.</summary>
    public static class TeamFogCodec
    {
        /// <summary>Applies a payload; false for a malformed one (what came before it stays applied).</summary>
        public static bool Apply(ArraySegment<byte> payload, int cells, Func<int, byte[]> gridOf)
        {
            int i = payload.Offset, end = payload.Offset + payload.Count;
            byte[] b = payload.Array;
            while (i < end)
            {
                if (end - i < 4) return false;
                int owner = b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24;
                i += 4;
                if (!Varint(b, ref i, end, out int runs)) return false;
                byte[] grid = gridOf(owner);
                int cell = 0;
                for (int r = 0; r < runs; r++)
                {
                    if (!Varint(b, ref i, end, out int skip) || !Varint(b, ref i, end, out int length) || i >= end) return false;
                    byte state = b[i++];
                    cell += skip;
                    if (cell < 0 || length < 0 || cell + length > cells) return false;
                    if (grid != null) for (int k = 0; k < length; k++) grid[cell + k] = state;
                    cell += length;
                }
            }
            return true;
        }

        private static bool Varint(byte[] b, ref int i, int end, out int value)
        {
            value = 0;
            int shift = 0;
            while (i < end)
            {
                byte v = b[i++];
                value |= (v & 0x7F) << shift;
                if ((v & 0x80) == 0) return true;
                shift += 7;
                if (shift > 28) return false;
            }
            return false;
        }
    }
}
