using System;
using System.Collections.Generic;
using Mirror;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>Building records for one client (see <see cref="BuildingCodec"/>). Sent reliably.</summary>
    public struct BuildingBatch : NetworkMessage
    {
        public ArraySegment<byte> Payload;
    }

    /// <summary>One live building as the server sees it, gathered at a boundary.</summary>
    public struct BuildingView
    {
        public int Id, OwnerId;
        public BuildingType Type;
        /// <summary>Footprint anchor tile.</summary>
        public int2 Anchor;
        /// <summary>Quarter turns (0..3).</summary>
        public byte Rotation;
        public int Health, MaxHealth;
        /// <summary>Where fog is tested (the anchor's world position).</summary>
        public float2 Position;
    }

    /// <summary>The kinds of building record.</summary>
    public enum BuildingRecord : byte
    {
        /// <summary>The building is seen: full data (also replaces a ghost).</summary>
        Enter = 0,
        /// <summary>A seen building's health changed.</summary>
        Health = 1,
        /// <summary>The building left sight: the client keeps it as a last-seen ghost.</summary>
        Hide = 2,
        /// <summary>The building (or ghost) is gone, and the client is allowed to know it.</summary>
        Gone = 3,
    }

    /// <summary>
    /// Per client, what it knows about buildings and the records that bring it up to date. A client may
    /// know its own buildings and any building on a fog cell its grid sees now (its own sight plus vision shared with it). A building that leaves
    /// sight becomes a ghost; the client only learns a ghost is gone once its grid sees the spot again.
    /// </summary>
    public sealed class BuildingInterest
    {
        private struct Known
        {
            public bool Live;
            public int Health;
            public float2 Position;
            public int OwnerId;
        }

        private readonly Dictionary<int, Known> known = new Dictionary<int, Known>();
        private readonly List<int> scratch = new List<int>();

        /// <summary>Forgets everything (the client left or rejoined).</summary>
        public void Reset() => known.Clear();

        /// <summary>
        /// Writes the records that take the client from what it knew to what it may know now; returns how
        /// many were written. <paramref name="sees"/> answers whether the client's grid sees a position.
        /// Stops once the writer holds <paramref name="maxBytes"/>; the rest follows on the next update.
        /// </summary>
        public int Update(List<BuildingView> views, HashSet<int> present, int ownerId, Func<float2, bool> sees, NetworkWriter writer, int maxBytes = int.MaxValue)
        {
            int records = 0;
            foreach (BuildingView v in views)
            {
                bool allowed = v.OwnerId == ownerId || sees(v.Position);
                bool wasKnown = known.TryGetValue(v.Id, out Known k);
                if (allowed)
                {
                    if (!wasKnown || !k.Live)
                    {
                        BuildingCodec.WriteEnter(writer, v);
                        records++;
                    }
                    else if (k.Health != v.Health)
                    {
                        BuildingCodec.WriteId(writer, BuildingRecord.Health, v.Id, v.Health);
                        records++;
                    }
                    known[v.Id] = new Known { Live = true, Health = v.Health, Position = v.Position, OwnerId = v.OwnerId };
                    if (writer.Position >= maxBytes) return records;
                }
                else if (wasKnown && k.Live)
                {
                    BuildingCodec.WriteId(writer, BuildingRecord.Hide, v.Id, 0);
                    records++;
                    k.Live = false;
                    known[v.Id] = k;
                    if (writer.Position >= maxBytes) return records;
                }
            }

            scratch.Clear();
            foreach (KeyValuePair<int, Known> pair in known)
                if (!present.Contains(pair.Key)) scratch.Add(pair.Key);
            foreach (int id in scratch)
            {
                Known k = known[id];
                if (k.OwnerId == ownerId || sees(k.Position))
                {
                    BuildingCodec.WriteId(writer, BuildingRecord.Gone, id, 0);
                    records++;
                    known.Remove(id);
                }
                else if (k.Live)
                {
                    // It died out of sight: the client keeps the last-seen ghost.
                    BuildingCodec.WriteId(writer, BuildingRecord.Hide, id, 0);
                    records++;
                    k.Live = false;
                    known[id] = k;
                }
            }
            return records;
        }
    }

    /// <summary>Encodes and decodes building records. Pure.</summary>
    public static class BuildingCodec
    {
        public static void WriteEnter(NetworkWriter writer, in BuildingView v)
        {
            writer.WriteByte((byte)BuildingRecord.Enter);
            VarInt.Write(writer, (uint)v.Id);
            writer.WriteByte((byte)v.Type);
            VarInt.WriteInt(writer, v.OwnerId);
            VarInt.Write(writer, (uint)math.max(0, v.Anchor.x));
            VarInt.Write(writer, (uint)math.max(0, v.Anchor.y));
            writer.WriteByte((byte)(v.Rotation & 3));
            VarInt.Write(writer, (uint)math.max(0, v.Health));
            VarInt.Write(writer, (uint)math.max(0, v.MaxHealth));
        }

        /// <summary>A Health (with <paramref name="value"/>), Hide or Gone record.</summary>
        public static void WriteId(NetworkWriter writer, BuildingRecord kind, int id, int value)
        {
            writer.WriteByte((byte)kind);
            VarInt.Write(writer, (uint)id);
            if (kind == BuildingRecord.Health) VarInt.Write(writer, (uint)math.max(0, value));
        }

        /// <summary>Applies every record to <paramref name="into"/>. False when malformed.</summary>
        public static bool Apply(ClientBuildings into, ArraySegment<byte> payload)
        {
            try
            {
                var reader = new NetworkReader(payload);
                while (reader.Remaining > 0)
                {
                    var kind = (BuildingRecord)reader.ReadByte();
                    int id = (int)VarInt.Read(reader);
                    switch (kind)
                    {
                        case BuildingRecord.Enter:
                            var type = (BuildingType)reader.ReadByte();
                            int owner = VarInt.ReadInt(reader);
                            int x = (int)VarInt.Read(reader), y = (int)VarInt.Read(reader);
                            byte rotation = reader.ReadByte();
                            int health = (int)VarInt.Read(reader), max = (int)VarInt.Read(reader);
                            if (!Enum.IsDefined(typeof(BuildingType), type) || rotation > 3) return false;
                            into.Enter(new BuildingData { id = id, buildingType = type, ownerId = owner, position = new float2(x, y), rotation = rotation * 90f },
                                new HealthComponent { entityId = id, currentHealth = health, maxHealth = max });
                            break;
                        case BuildingRecord.Health:
                            into.SetHealth(id, (int)VarInt.Read(reader));
                            break;
                        case BuildingRecord.Hide:
                            into.Hide(id);
                            break;
                        case BuildingRecord.Gone:
                            into.Remove(id);
                            break;
                        default:
                            return false;
                    }
                }
                return true;
            }
            catch (Exception e) when (e is System.IO.EndOfStreamException || e is OverflowException || e is InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>The buildings the local client knows: seen ones, and ghosts of ones it saw before.</summary>
    public sealed class ClientBuildings
    {
        public struct Entry
        {
            public BuildingData Data;
            public HealthComponent Health;
            /// <summary>True when the building is out of sight: what is shown is how it was last seen.</summary>
            public bool Ghost;
        }

        /// <summary>The local client's buildings.</summary>
        public static ClientBuildings Current { get; private set; } = new ClientBuildings();

        public readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();

        /// <summary>Raised for a building seen (again): its data and health.</summary>
        public event Action<BuildingData, HealthComponent> Entered;
        /// <summary>Raised when a seen building's health changes.</summary>
        public event Action<HealthComponent> HealthChanged;
        /// <summary>Raised when a building becomes a ghost.</summary>
        public event Action<int> Hidden;
        /// <summary>Raised when a building or ghost is removed.</summary>
        public event Action<int> Removed;

        /// <summary>Applies a received batch to <see cref="Current"/>.</summary>
        public static void Receive(BuildingBatch batch)
        {
            if (!BuildingCodec.Apply(Current, batch.Payload)) UnityEngine.Debug.LogWarning("[Buildings] dropped a malformed building batch");
        }

        /// <summary>Forgets every building (the match ended).</summary>
        public static void Clear() => Current = new ClientBuildings();

        internal void Enter(BuildingData data, HealthComponent health)
        {
            Entries[data.id] = new Entry { Data = data, Health = health };
            Entered?.Invoke(data, health);
        }

        internal void SetHealth(int id, int health)
        {
            if (!Entries.TryGetValue(id, out Entry e)) return;
            e.Health.currentHealth = health;
            Entries[id] = e;
            HealthChanged?.Invoke(e.Health);
        }

        internal void Hide(int id)
        {
            if (!Entries.TryGetValue(id, out Entry e)) return;
            e.Ghost = true;
            Entries[id] = e;
            Hidden?.Invoke(id);
        }

        internal void Remove(int id)
        {
            if (Entries.Remove(id)) Removed?.Invoke(id);
        }
    }
}
