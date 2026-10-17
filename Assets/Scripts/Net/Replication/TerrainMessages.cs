using WAR2D.Dev.Logging;
using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Mathematics;
using WAR2D.World;

namespace WAR2D.Net.Replication
{
    /// <summary>Terrain changes for one client (see <see cref="TerrainCodec"/>). Sent reliably.</summary>
    public struct TerrainBatch : NetworkMessage
    {
        public ArraySegment<byte> Payload;
    }

    /// <summary>One tile as a client may know it: kind, and for player walls their owner and health percent.</summary>
    public struct TerrainCell : IEquatable<TerrainCell>
    {
        public int Index;
        public byte Type;
        /// <summary>The wall's owner (PlayerWall and WallBlueprint only), else 0.</summary>
        public int OwnerId;
        /// <summary>The wall's health in percent, 1..100 (PlayerWall only), else 0.</summary>
        public byte Health;

        public bool Equals(TerrainCell o) => Type == o.Type && OwnerId == o.OwnerId && Health == o.Health;
    }

    /// <summary>Encodes and decodes terrain cells: count, then (index delta, kind[, owner, health]) per cell, indices ascending. Pure.</summary>
    public static class TerrainCodec
    {
        /// <summary>Most cells one batch may carry, so a decoder never allocates on a hostile count.</summary>
        public const int MaxCells = 4096;

        public static void Write(NetworkWriter writer, List<TerrainCell> cells)
        {
            cells.Sort((a, b) => a.Index.CompareTo(b.Index));
            VarInt.Write(writer, (uint)cells.Count);
            int last = 0;
            foreach (TerrainCell c in cells)
            {
                VarInt.Write(writer, (uint)(c.Index - last));
                last = c.Index;
                writer.WriteByte(c.Type);
                if (HasWallData(c.Type))
                {
                    VarInt.WriteInt(writer, c.OwnerId);
                    writer.WriteByte(c.Health);
                }
            }
        }

        /// <summary>Applies every cell to <paramref name="into"/> and raises its Changed event once. False when malformed.</summary>
        public static bool Apply(ClientTerrain into, ArraySegment<byte> payload)
        {
            try
            {
                var reader = new NetworkReader(payload);
                uint count = VarInt.Read(reader);
                if (count > MaxCells) return false;
                var cells = new List<(int index, byte type, int owner, byte health)>((int)count);
                long index = 0;
                for (int i = 0; i < count; i++)
                {
                    index += VarInt.Read(reader);
                    byte type = reader.ReadByte();
                    if (index >= into.Tiles.Length || type > (byte)TileType.WallBlueprint) return false;
                    int owner = 0;
                    byte health = 0;
                    if (HasWallData(type))
                    {
                        owner = VarInt.ReadInt(reader);
                        health = reader.ReadByte();
                        if (health > 100) return false;
                    }
                    cells.Add(((int)index, type, owner, health));
                }
                if (reader.Remaining != 0) return false;
                var changed = new List<int2>(cells.Count);
                foreach (var c in cells)
                {
                    into.Set(c.index, c.type, c.owner, c.health);
                    changed.Add(new int2(c.index % into.Width, c.index / into.Width));
                }
                into.RaiseChanged(changed);
                return true;
            }
            catch (Exception e) when (e is System.IO.EndOfStreamException || e is OverflowException || e is InvalidOperationException)
            {
                return false;
            }
        }

        private static bool HasWallData(byte type) => type == (byte)TileType.PlayerWall || type == (byte)TileType.WallBlueprint;
    }

    /// <summary>
    /// Per client, the terrain it was last sent, and the records that bring it up to date. A tile is sent
    /// only while the client's grid sees it; what the client knows is its initial (seeded) copy plus every
    /// cell it was sent. Memory grows with the tiles that changed, not with the map.
    /// </summary>
    public sealed class TerrainInterest
    {
        private readonly Dictionary<int, TerrainCell> sent = new Dictionary<int, TerrainCell>();
        private readonly List<TerrainCell> scratch = new List<TerrainCell>();

        /// <summary>Forgets everything (the client left or rejoined).</summary>
        public void Reset() => sent.Clear();

        /// <summary>Writes one batch of seen, changed tiles; returns how many cells it holds (0 writes nothing).</summary>
        public int Update(IReadOnlyCollection<int> changed, Func<int, TerrainCell> current, Func<float2, bool> sees, int width, byte[] initial, NetworkWriter writer, int maxBytes)
        {
            var cells = new List<TerrainCell>(changed.Count);
            foreach (int index in changed) cells.Add(current(index));
            return Update(cells, sees, width, initial, writer, maxBytes);
        }

        /// <summary>
        /// As above, from the changed tiles' current cells, read once per tick and shared by every client (reading them
        /// per client cost ~8 ms on vision ticks with 9 clients, docs/perf/v0.12).
        /// </summary>
        public int Update(IReadOnlyList<TerrainCell> changedNow, Func<float2, bool> sees, int width, byte[] initial, NetworkWriter writer, int maxBytes)
        {
            scratch.Clear();
            for (int i = 0; i < changedNow.Count; i++)
            {
                TerrainCell now = changedNow[i];
                int index = now.Index;
                TerrainCell known = sent.TryGetValue(index, out TerrainCell k) ? k : new TerrainCell { Index = index, Type = initial[index] };
                if (now.Equals(known)) continue;
                if (!sees(new float2(index % width + 0.5f, index / width + 0.5f))) continue;
                scratch.Add(now);
                if (scratch.Count >= TerrainCodec.MaxCells || scratch.Count * 8 >= maxBytes) break;
            }
            if (scratch.Count == 0) return 0;
            foreach (TerrainCell c in scratch) sent[c.Index] = c;
            TerrainCodec.Write(writer, scratch);
            return scratch.Count;
        }
    }

    /// <summary>The terrain the local client knows: the seeded map plus every change it was sent.</summary>
    public sealed class ClientTerrain
    {
        /// <summary>The local client's terrain.</summary>
        public static ClientTerrain Current { get; private set; } = new ClientTerrain();

        /// <summary>Tile kinds, row-major (a copy; the client's MapGrid is patched to match by its owner).</summary>
        public byte[] Tiles { get; private set; } = Array.Empty<byte>();
        public int Width { get; private set; }

        private readonly Dictionary<int, (int owner, byte health)> walls = new Dictionary<int, (int, byte)>();

        /// <summary>Raised after a batch with the tiles it changed.</summary>
        public event Action<IReadOnlyList<int2>> Changed;

        /// <summary>Starts from the seeded map.</summary>
        public void Init(MapGrid grid)
        {
            Width = grid.Width;
            Tiles = grid.Tiles.ToArray();
            walls.Clear();
        }

        /// <summary>The owner of the wall at a tile index, or 0.</summary>
        public int WallOwner(int index) => walls.TryGetValue(index, out var w) ? w.owner : 0;

        /// <summary>The health percent of the wall at a tile index, or 0.</summary>
        public int WallHealth(int index) => walls.TryGetValue(index, out var w) ? w.health : 0;

        internal void Set(int index, byte type, int owner, byte health)
        {
            Tiles[index] = type;
            if (owner != 0) walls[index] = (owner, health);
            else walls.Remove(index);
        }

        internal void RaiseChanged(IReadOnlyList<int2> tiles) => Changed?.Invoke(tiles);

        /// <summary>Applies a received batch to <see cref="Current"/>.</summary>
        public static void Receive(TerrainBatch batch)
        {
            if (!TerrainCodec.Apply(Current, batch.Payload)) WarLog.Warn(LogCategory.Replication, "dropped a malformed terrain batch");
        }

        /// <summary>Forgets the terrain (the match ended).</summary>
        public static void Clear() => Current = new ClientTerrain();
    }
}
