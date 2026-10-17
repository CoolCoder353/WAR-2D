using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Mirror;
using WAR2D.Dev.Logging;

namespace WAR2D.Net.Replay
{
    public struct ReplayOffer : NetworkMessage { public int Id; public long Bytes; public byte[] Sha256; public float DurationSeconds; }
    public struct ReplayChunk : NetworkMessage { public int Id; public long Offset; public ArraySegment<byte> Bytes; }

    public static class ReplayRules
    {
        /// <summary>Replays reveal everything, so nothing about one leaves the server before the match is over.</summary>
        public static bool MaySend(GameState state) => state == GameState.GameOver;
        public const int ChunkBytes = 16 * 1024;
    }

    /// <summary>Server side: offers the finished recording to each player and streams it to those who accept, paced per connection.</summary>
    public sealed class ReplaySender
    {
        private sealed class Transfer { public NetworkConnectionToClient Conn; public long Offset; public float Credit; }

        private readonly string path;
        private readonly int bytesPerSecond;
        private readonly ReplayOffer offer;
        private readonly Dictionary<int, Transfer> active = new Dictionary<int, Transfer>();
        private readonly HashSet<int> offered = new HashSet<int>();
        private readonly byte[] buffer = new byte[ReplayRules.ChunkBytes];
        internal Action<NetworkConnectionToClient, ReplayOffer> SendOffer = (c, m) => c.Send(m);
        internal Action<NetworkConnectionToClient, ReplayChunk> SendChunk = (c, m) => c.Send(m);

        public int Id => offer.Id;

        public ReplaySender(int id, string path, int bytesPerSecond, float durationSeconds)
        {
            this.path = path;
            this.bytesPerSecond = bytesPerSecond;
            offer = MakeOffer(id, path, durationSeconds);
        }

        public static ReplayOffer MakeOffer(int id, string path, float durationSeconds)
        {
            using FileStream f = File.OpenRead(path);
            using var sha = SHA256.Create();
            return new ReplayOffer { Id = id, Bytes = f.Length, Sha256 = sha.ComputeHash(f), DurationSeconds = durationSeconds };
        }

        /// <summary>Every chunk of a file, in order (tests and the sender).</summary>
        public static IEnumerable<ReplayChunk> Chunks(int id, string path, int chunkBytes)
        {
            byte[] all = File.ReadAllBytes(path);
            for (long o = 0; o < all.Length; o += chunkBytes)
            {
                int n = (int)Math.Min(chunkBytes, all.Length - o);
                var copy = new byte[n];
                Array.Copy(all, o, copy, 0, n);
                yield return new ReplayChunk { Id = id, Offset = o, Bytes = new ArraySegment<byte>(copy) };
            }
        }

        public void Offer(NetworkConnectionToClient conn, GameState state)
        {
            if (conn == null || !ReplayRules.MaySend(state) || !offered.Add(conn.connectionId)) return;
            SendOffer(conn, offer);
        }

        public void Accept(NetworkConnectionToClient conn, int id, GameState state)
        {
            if (conn == null || id != offer.Id || !ReplayRules.MaySend(state) || !offered.Contains(conn.connectionId)) return;
            active[conn.connectionId] = new Transfer { Conn = conn };
        }

        public void Cancel(NetworkConnectionToClient conn) { if (conn != null) active.Remove(conn.connectionId); }

        public bool Busy => active.Count > 0;

        /// <summary>Sends each accepted connection its share of <c>bytesPerSecond</c> this frame.</summary>
        public void Update(float dt, GameState state)
        {
            if (!ReplayRules.MaySend(state) || active.Count == 0) return;
            using FileStream f = File.OpenRead(path);
            var done = new List<int>();
            foreach (var pair in active)
            {
                Transfer t = pair.Value;
                if (t.Conn == null || !NetworkServer.connections.ContainsKey(pair.Key)) { done.Add(pair.Key); continue; }
                t.Credit = Math.Min(t.Credit + bytesPerSecond * dt, bytesPerSecond);
                while (t.Credit >= ReplayRules.ChunkBytes || (t.Offset + (long)t.Credit >= f.Length && t.Offset < f.Length))
                {
                    f.Position = t.Offset;
                    int n = f.Read(buffer, 0, (int)Math.Min(buffer.Length, f.Length - t.Offset));
                    if (n <= 0) break;
                    SendChunk(t.Conn, new ReplayChunk { Id = offer.Id, Offset = t.Offset, Bytes = new ArraySegment<byte>(buffer, 0, n) });
                    t.Offset += n;
                    t.Credit -= n;
                }
                if (t.Offset >= f.Length) done.Add(pair.Key);
            }
            foreach (int c in done) active.Remove(c);
        }
    }

    /// <summary>Client side: accepts an offer (when Save replays is on), writes chunks in order to a .part file, checks size and hash, then renames it.</summary>
    public sealed class ReplayReceiver
    {
        public static ReplayReceiver Current { get; private set; }

        private readonly string folder;
        private ReplayOffer offer;
        private FileStream part;
        private string partPath;
        private long next;

        public bool Active => part != null;

        /// <summary>How much of the offered file has arrived (0 to 1), for the end screen.</summary>
        public float Fraction => offer.Bytes > 0 ? (float)next / offer.Bytes : 0f;

        /// <summary>The saved file once the transfer finished, else null.</summary>
        public string SavedPath { get; private set; }
        public event Action<float> Progress;
        public event Action<string> Saved;

        private readonly long maxBytes;
        private readonly int keepCount;

        /// <param name="maxBytes">Offers above this are refused (a host can't make the client fill its disk).</param>
        /// <param name="keepCount">Replays kept in <paramref name="folder"/> after a save (0: don't prune).</param>
        public ReplayReceiver(string folder, long maxBytes = 8L << 30, int keepCount = 0)
        {
            this.folder = folder;
            this.maxBytes = maxBytes;
            this.keepCount = keepCount;
        }

        /// <summary>Called from GameManager's handler: replaces <see cref="Current"/>.</summary>
        public static void Install(string folder, long maxBytes, int keepCount) { Current?.Cancel(); Current = new ReplayReceiver(folder, maxBytes, keepCount); }

        /// <summary>Forgets the transfer (left the game): the next end screen starts with no replay line.</summary>
        public static void Clear() { Current?.Cancel(); Current = null; }

        public void OnOffer(ReplayOffer o, bool accept)
        {
            Cancel();
            if (!accept || o.Bytes <= 0 || o.Bytes > maxBytes || o.Sha256 == null || o.Sha256.Length != 32) return;
            offer = o;
            Directory.CreateDirectory(folder);
            partPath = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}{ReplayLibrary.ReceivedTag}{Guid.NewGuid().ToString("N").Substring(0, 8)}{ReplayFile.Extension}.part");
            part = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite);
            next = 0;
        }

        public void OnChunk(ReplayChunk c)
        {
            if (part == null || c.Id != offer.Id) return;
            if (c.Offset != next || c.Bytes.Count == 0 || next + c.Bytes.Count > offer.Bytes) { WarLog.Warn(LogCategory.Replay, "replay transfer out of order: cancelled"); Cancel(); return; }
            part.Write(c.Bytes.Array, c.Bytes.Offset, c.Bytes.Count);
            next += c.Bytes.Count;
            Progress?.Invoke((float)next / offer.Bytes);
            if (next < offer.Bytes) return;
            part.Position = 0;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(part);
            part.Dispose();
            part = null;
            if (!((ReadOnlySpan<byte>)hash).SequenceEqual(offer.Sha256)) { File.Delete(partPath); WarLog.Warn(LogCategory.Replay, "replay transfer failed its hash check"); return; }
            if (!IsReplay(partPath)) { File.Delete(partPath); WarLog.Warn(LogCategory.Replay, "received file is not a WAR-2D replay: discarded"); return; }
            string final = partPath.Substring(0, partPath.Length - ".part".Length);
            File.Move(partPath, final);
            ReplayLibrary.WriteInfo(final, offer.DurationSeconds);
            // Only received replays count towards the cap: a host sending many offers can never evict the player's own recordings.
            if (keepCount > 0) ReplayLibrary.Prune(folder, keepCount, receivedOnly: true);
            SavedPath = final;
            Saved?.Invoke(final);
        }

        /// <summary>True when the file opens as a replay (magic, format and header), so nothing else is kept or counted.</summary>
        private static bool IsReplay(string path)
        {
            try
            {
                using FileStream f = File.OpenRead(path);
                if (!ReplayReader.TryOpen(f, out ReplayReader r, out _)) return false;
                r.Dispose();
                return true;
            }
            catch (IOException) { return false; }
        }

        public void Cancel()
        {
            if (part == null) return;
            part.Dispose();
            part = null;
            if (File.Exists(partPath)) File.Delete(partPath);
        }
    }
}
