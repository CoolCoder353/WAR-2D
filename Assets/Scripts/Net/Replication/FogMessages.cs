using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;

namespace WAR2D.Net.Replication
{
    /// <summary>
    /// One fog of war payload for the receiving client's own team: a <see cref="FogCodec.Snapshot"/> of the
    /// whole grid, or a <see cref="FogCodec.Delta"/> of the cells whose visibility flipped. Sent reliably,
    /// so deltas always follow the snapshot they apply to. A client is never sent another team's grid.
    /// </summary>
    public struct FogBatch : NetworkMessage
    {
        public ArraySegment<byte> Payload;
    }

    /// <summary>What a client knows about one fog cell.</summary>
    public enum FogState : byte
    {
        Unexplored = 0,
        /// <summary>Seen before, hidden now: the client shows it as last seen.</summary>
        Explored = 1,
        Visible = 2,
    }

    /// <summary>Encodes and decodes fog payloads. Pure.</summary>
    public static class FogCodec
    {
        public const byte Snapshot = 0;
        public const byte Delta = 1;
        /// <summary>Most cell indices per delta payload (keeps a payload well under the reliable limit).</summary>
        public const int MaxDeltaCells = 16384;

        /// <summary>
        /// Writes a whole grid: kind, width, height, cell size, then runs of (state, varint length) over the
        /// cells in row-major order.
        /// </summary>
        public static void WriteSnapshot(NetworkWriter writer, int fogW, int fogH, int cellSize,
            NativeArray<byte> visible, NativeArray<byte> explored, int offset)
        {
            writer.WriteByte(Snapshot);
            VarInt.Write(writer, (uint)fogW);
            VarInt.Write(writer, (uint)fogH);
            VarInt.Write(writer, (uint)cellSize);
            int cells = fogW * fogH;
            int i = 0;
            while (i < cells)
            {
                byte state = StateAt(visible, explored, offset + i);
                int run = 1;
                while (i + run < cells && StateAt(visible, explored, offset + i + run) == state) run++;
                writer.WriteByte(state);
                VarInt.Write(writer, (uint)run);
                i += run;
            }
        }

        private static byte StateAt(NativeArray<byte> visible, NativeArray<byte> explored, int index) =>
            (byte)(visible[index] != 0 ? FogState.Visible : explored[index] != 0 ? FogState.Explored : FogState.Unexplored);

        /// <summary>Writes flipped cells (ascending, relative to the grid's start) as varint gaps.</summary>
        public static void WriteDelta(NetworkWriter writer, List<int> sortedCells, int start, int count)
        {
            writer.WriteByte(Delta);
            VarInt.Write(writer, (uint)count);
            int previous = -1;
            for (int k = start; k < start + count; k++)
            {
                VarInt.Write(writer, (uint)(sortedCells[k] - previous - 1));
                previous = sortedCells[k];
            }
        }

        /// <summary>Applies one payload to <paramref name="fog"/>. False (and nothing half-applied matters) when malformed.</summary>
        public static bool Apply(ClientFog fog, ArraySegment<byte> payload)
        {
            try
            {
                var reader = new NetworkReader(payload);
                byte kind = reader.ReadByte();
                if (kind == Snapshot)
                {
                    int w = (int)VarInt.Read(reader), h = (int)VarInt.Read(reader), size = (int)VarInt.Read(reader);
                    if (w <= 0 || h <= 0 || size <= 0 || (long)w * h > 1 << 24) return false;
                    fog.Reset(w, h, size);
                    int i = 0, cells = w * h;
                    while (i < cells)
                    {
                        byte state = reader.ReadByte();
                        int run = (int)VarInt.Read(reader);
                        if (state > (byte)FogState.Visible || run <= 0 || run > cells - i) return false;
                        for (int k = 0; k < run; k++) fog.State[i + k] = state;
                        i += run;
                    }
                    fog.MarkAllChanged();
                    return reader.Remaining == 0;
                }
                if (kind != Delta || fog.State == null) return false;
                int count = (int)VarInt.Read(reader);
                int cell = -1;
                for (int k = 0; k < count; k++)
                {
                    cell += (int)VarInt.Read(reader) + 1;
                    if ((uint)cell >= (uint)fog.State.Length) return false;
                    fog.Toggle(cell);
                }
                return reader.Remaining == 0;
            }
            catch (Exception e) when (e is System.IO.EndOfStreamException || e is OverflowException || e is InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>The local client's view of its team's fog grid.</summary>
    public sealed class ClientFog
    {
        /// <summary>The fog the local client shows, or null before the first snapshot of a match.</summary>
        public static ClientFog Current { get; private set; } = new ClientFog();

        /// <summary>A <see cref="FogState"/> per cell, row-major, or null before the first snapshot.</summary>
        public byte[] State { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int CellSize { get; private set; }
        /// <summary>Cells changed since the last <see cref="TakeChanges"/> (all of them after a snapshot).</summary>
        private readonly List<int> changed = new List<int>();
        private bool allChanged;

        /// <summary>Raised after any payload is applied.</summary>
        public static event Action Updated;

        /// <summary>Applies a received payload to <see cref="Current"/>.</summary>
        public static void Receive(FogBatch batch)
        {
            if (!FogCodec.Apply(Current, batch.Payload))
            {
                UnityEngine.Debug.LogWarning("[Fog] dropped a malformed fog batch");
                return;
            }
            Updated?.Invoke();
        }

        /// <summary>Forgets the fog (a new match, or leaving one).</summary>
        public static void Clear() => Current = new ClientFog();

        internal void Reset(int width, int height, int cellSize)
        {
            Width = width;
            Height = height;
            CellSize = cellSize;
            State = new byte[width * height];
            changed.Clear();
        }

        internal void MarkAllChanged() => allChanged = true;

        internal void Toggle(int cell)
        {
            State[cell] = (byte)(State[cell] == (byte)FogState.Visible ? FogState.Explored : FogState.Visible);
            if (!allChanged) changed.Add(cell);
        }

        /// <summary>True when the world position lies on a cell the team sees now.</summary>
        public bool Sees(float x, float y)
        {
            if (State == null) return false;
            int cx = (int)Math.Floor(x / CellSize), cy = (int)Math.Floor(y / CellSize);
            return (uint)cx < (uint)Width && (uint)cy < (uint)Height && State[cy * Width + cx] == (byte)FogState.Visible;
        }

        /// <summary>The state of the cell holding a world position (Unexplored off the grid).</summary>
        public FogState StateAt(float x, float y)
        {
            if (State == null) return FogState.Unexplored;
            int cx = (int)Math.Floor(x / CellSize), cy = (int)Math.Floor(y / CellSize);
            return (uint)cx < (uint)Width && (uint)cy < (uint)Height ? (FogState)State[cy * Width + cx] : FogState.Unexplored;
        }

        /// <summary>Hands the changed cells to a renderer: true and no list for "everything changed".</summary>
        public bool TakeChanges(List<int> into)
        {
            bool all = allChanged;
            allChanged = false;
            if (!all) into.AddRange(changed);
            changed.Clear();
            return all;
        }
    }
}
