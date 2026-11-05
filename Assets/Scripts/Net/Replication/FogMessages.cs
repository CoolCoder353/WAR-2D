using WAR2D.Dev.Logging;
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
        /// <summary>The encoded fog payload.</summary>
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
        /// <summary>Payload type: the whole grid.</summary>
        public const byte Snapshot = 0;
        /// <summary>Payload type: cells that flipped.</summary>
        public const byte Delta = 1;
        /// <summary>Most cell indices per delta payload (keeps a payload well under the reliable limit).</summary>
        public const int MaxDeltaCells = 16384;

        /// <summary>
        /// Most bytes one payload should take: a server message past about 16 KB is dropped on the way (SimpleWeb's
        /// <c>maxMessageSize</c> through <c>MultiplexTransport</c>) and never resent, so a big snapshot or delta is split
        /// (bug review 2026-11-05).
        /// </summary>
        public const int MaxPayloadBytes = 12 * 1024;

        /// <summary>A run's most bytes: its state and a five-byte varint.</summary>
        private const int MaxRunBytes = 6;

        /// <summary>
        /// Writes a grid, or the part of it from cell <paramref name="start"/>: kind, width, height, cell size, start, then runs
        /// of (state, varint length) over the cells in row-major order, until the grid ends or the payload reaches
        /// <paramref name="maxBytes"/>. Returns the cell the next part starts at (the cell count when the grid is done). The
        /// part from 0 resets the client's grid; later parts continue it.
        /// </summary>
        public static int WriteSnapshot(NetworkWriter writer, int fogW, int fogH, int cellSize,
            NativeArray<byte> visible, NativeArray<byte> explored, int offset, int start = 0, int maxBytes = int.MaxValue)
        {
            int begin = writer.Position;
            writer.WriteByte(Snapshot);
            VarInt.Write(writer, (uint)fogW);
            VarInt.Write(writer, (uint)fogH);
            VarInt.Write(writer, (uint)cellSize);
            VarInt.Write(writer, (uint)start);
            int cells = fogW * fogH;
            int i = start;
            while (i < cells)
            {
                if (i > start && writer.Position - begin + MaxRunBytes > maxBytes) break;
                byte state = StateAt(visible, explored, offset + i);
                int run = 1;
                while (i + run < cells && StateAt(visible, explored, offset + i + run) == state) run++;
                writer.WriteByte(state);
                VarInt.Write(writer, (uint)run);
                i += run;
            }
            return i;
        }

        /// <summary>How many of the sorted cells from <paramref name="start"/> one delta can carry in <paramref name="maxBytes"/> (at least one).</summary>
        public static int DeltaCountWithin(List<int> sortedCells, int start, int maxBytes)
        {
            int bytes = 1 + 5; // kind and the count's varint, at its largest
            int previous = -1; // each part's gaps count from -1, as WriteDelta writes them
            int count = 0;
            for (int k = start; k < sortedCells.Count && count < MaxDeltaCells; k++)
            {
                int size = VarInt.Size((uint)(sortedCells[k] - previous - 1));
                if (count > 0 && bytes + size > maxBytes) break;
                bytes += size;
                previous = sortedCells[k];
                count++;
            }
            return count;
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
                    int cells = w * h;
                    uint first = VarInt.Read(reader);
                    if (first >= (uint)cells) return false;
                    // The first part starts the grid afresh; a later part continues one of the same shape.
                    if (first == 0) fog.Reset(w, h, size);
                    else if (fog.State == null || fog.Width != w || fog.Height != h || fog.CellSize != size) return false;
                    int i = (int)first;
                    while (reader.Remaining > 0)
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
        /// <summary>Grid width in cells.</summary>
        public int Width { get; private set; }
        /// <summary>Grid height in cells.</summary>
        public int Height { get; private set; }
        /// <summary>Tiles per cell side.</summary>
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
                WarLog.Warn(LogCategory.Replication, "dropped a malformed fog batch");
                return;
            }
            Updated?.Invoke();
        }

        /// <summary>Forgets the fog (a new match, or leaving one).</summary>
        public static void Clear() => Current = new ClientFog();

        /// <summary>Shows a replay's view (v0.18): this player's recorded grid, drawn at once.</summary>
        public static void ShowReplayGrid(byte[] state, int width, int height, int cellSize)
        {
            ClientFog fog = Current;
            if (fog.State == null || fog.Width != width || fog.Height != height) fog.Reset(width, height, cellSize);
            Buffer.BlockCopy(state, 0, fog.State, 0, state.Length);
            fog.MarkAllChanged();
            Updated?.Invoke();
        }

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
