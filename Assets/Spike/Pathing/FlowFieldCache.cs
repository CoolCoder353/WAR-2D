using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// A live flow field: the integration cost and the direction byte per cell, plus whether a
    /// terrain change has left those cells stale. A <see cref="FlowFieldCache"/> owns the arrays;
    /// the struct is a view that stays valid until the field is released.
    /// </summary>
    public struct FlowField
    {
        /// <summary>The clicked goal cell the field was acquired for.</summary>
        public int2 Goal;

        /// <summary>The size class the field's grid was filtered for.</summary>
        public int SizeClass;

        /// <summary>Tiles across.</summary>
        public int Width;

        /// <summary>Tiles down.</summary>
        public int Height;

        /// <summary>Cost to the nearest goal cell: 10 per straight step, 14 per diagonal, 65535 unreachable.</summary>
        public NativeArray<ushort> Cost;

        /// <summary>Direction byte per cell, see <see cref="FlowDirections"/>.</summary>
        public NativeArray<byte> Direction;

        /// <summary>False when the cells predate the latest terrain change and a rebuild is queued.</summary>
        public bool Ready;

        /// <summary>The direction at a tile. The caller keeps the tile inside the field.</summary>
        public byte DirectionAt(int2 tile) => Direction[tile.y * Width + tile.x];

        /// <summary>The direction at a cell index.</summary>
        public byte DirectionAtCell(int cellIndex) => Direction[cellIndex];
    }

    /// <summary>
    /// Holds the flow fields orders are currently following, keyed by (goal cell, size class). A goal
    /// is the clicked cell plus up to <see cref="GoalSpread"/> free cells around it, so a group does
    /// not funnel into one tile. Each entry owns a cost array and a direction array (3 bytes per
    /// cell), a reference count of the units following it, and a dirty flag.
    ///
    /// <para><see cref="Invalidate"/> marks every live field dirty: a terrain change can alter costs
    /// anywhere downstream, so no field is trusted until it is rebuilt. <see cref="RebuildDirty"/>
    /// schedules up to <c>maxPerTick</c> rebuilds as parallel jobs, newest orders first, and
    /// <see cref="CompleteRebuilds"/> finishes them; a field is ready again when its rebuild lands
    /// and no further change has dirtied it in the meantime.</para>
    ///
    /// <para>Handles are valid from <see cref="Acquire"/> until the matching <see cref="Release"/>
    /// drops the last follower; a released handle's slot may be reused, so callers must not hold one
    /// past their release.</para>
    /// </summary>
    public sealed class FlowFieldCache : IDisposable
    {
        /// <summary>Free cells around the clicked tile a field spreads its goals over (the click first).</summary>
        public const int GoalSpread = 64;

        /// <summary>Released buffers kept for reuse, so a tick that acquires a field does not allocate.</summary>
        private const int PoolCap = 8;

        private sealed class Entry
        {
            public int2 Goal;
            public int SizeClass;
            public NativeArray<ushort> Cost;
            public NativeArray<byte> Direction;
            public NativeArray<int> Goals;
            public int RefCount;
            public int DirtySeq;   // bumped by every Invalidate
            public int BuiltSeq;   // the sequence a scheduled rebuild captured
            public bool Dirty;
            public bool Ready;
            public bool InFlight;
            public bool EverBuilt;
            public bool Dead;
            public JobHandle Handle;
        }

        private struct Buffer
        {
            public NativeArray<ushort> Cost;
            public NativeArray<byte> Direction;
        }

        private readonly int width, height, cells;
        private readonly NativeArray<byte> tiles; // the map's grid; the cache does not own it
        private readonly Allocator allocator;
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<int> free = new List<int>();
        private readonly List<Buffer> pool = new List<Buffer>();
        private readonly Dictionary<long, int> byGoal = new Dictionary<long, int>();
        private readonly NativeArray<byte> clearance;
        private readonly NativeArray<byte> filteredLarge;

        private JobHandle pending;
        private bool hasPending;
        private int inFlight;
        private long rebuilds;
        private bool disposed;

        /// <summary>
        /// Builds the cache's clearance grid and the large-class filtered grid from the map's current
        /// tiles. The map's tile array is shared, not copied: the caller flips tiles in it and then
        /// calls <see cref="Invalidate"/>.
        /// </summary>
        public FlowFieldCache(in SpikeMap map, Allocator allocator)
        {
            width = map.Width;
            height = map.Height;
            cells = width * height;
            tiles = map.Tiles;
            this.allocator = allocator;

            clearance = new NativeArray<byte>(cells, allocator);
            filteredLarge = new NativeArray<byte>(cells, allocator);
            new ClearanceJob { Width = width, Height = height, Tiles = tiles, Clearance = clearance }.Run();
            new FilterTilesJob
            {
                Width = width, Height = height, Tiles = tiles, Clearance = clearance,
                MinClearance = FlowSizeClass.MinClearance(FlowSizeClass.Large), Filtered = filteredLarge,
            }.Run(cells);
        }

        /// <summary>Tiles across.</summary>
        public int Width => width;

        /// <summary>Tiles down.</summary>
        public int Height => height;

        /// <summary>Cells in one field.</summary>
        public int CellCount => cells;

        /// <summary>Clearance per tile, capped at <see cref="ClearanceJob.MaxClearance"/>.</summary>
        public NativeArray<byte> Clearance => clearance;

        /// <summary>The large size class's grid: floor tiles with less than 2 tiles of clearance block.</summary>
        public NativeArray<byte> LargeGrid => filteredLarge;

        /// <summary>Bytes one live field costs (2 bytes of cost plus 1 of direction per cell).</summary>
        public long BytesPerCell => 3L;

        /// <summary>Live fields: they have at least one follower.</summary>
        public int LiveCount
        {
            get
            {
                int count = 0;
                foreach (Entry e in entries) if (Live(e)) count++;
                return count;
            }
        }

        /// <summary>Live fields with at least one follower in each size class.</summary>
        public int LiveCountOfClass(int sizeClass)
        {
            int count = 0;
            foreach (Entry e in entries) if (Live(e) && e.SizeClass == sizeClass) count++;
            return count;
        }

        /// <summary>Bytes the live fields hold: 3 bytes per cell plus the goal lists.</summary>
        public long LiveBytes
        {
            get
            {
                long bytes = 0;
                foreach (Entry e in entries)
                {
                    if (!Live(e)) continue;
                    bytes += e.Cost.Length * 2L + e.Direction.Length + e.Goals.Length * 4L;
                }
                return bytes;
            }
        }

        /// <summary>Live fields whose cells are not current: dirty or still rebuilding.</summary>
        public int DirtyCount
        {
            get
            {
                int count = 0;
                foreach (Entry e in entries) if (Live(e) && !e.Ready) count++;
                return count;
            }
        }

        /// <summary>Rebuilds scheduled and not yet completed.</summary>
        public int PendingCount => inFlight;

        /// <summary>Field rebuilds scheduled since the cache was created.</summary>
        public long RebuildCount => rebuilds;

        /// <summary>Bytes parked in the reuse pool rather than held by a live field.</summary>
        public long PooledBytes
        {
            get
            {
                long bytes = 0;
                foreach (Buffer b in pool) bytes += b.Cost.Length * 2L + b.Direction.Length;
                return bytes;
            }
        }

        /// <summary>
        /// Adds a follower to the field for (<paramref name="goal"/>, <paramref name="sizeClass"/>),
        /// creating it dirty when no live field matches, and returns its handle. A new field is built
        /// by the next <see cref="RebuildDirty"/> that has room for it.
        /// </summary>
        public int Acquire(int2 goal, int sizeClass = FlowSizeClass.Small, int followers = 1)
        {
            ThrowIfDisposed();
            if (sizeClass < 0 || sizeClass >= FlowSizeClass.Count)
                throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "unknown size class");
            if ((uint)goal.x >= (uint)width || (uint)goal.y >= (uint)height)
                throw new ArgumentOutOfRangeException(nameof(goal), goal, "the goal is outside the map");

            int handle = Find(goal, sizeClass);
            if (handle < 0) handle = Create(goal, sizeClass);
            entries[handle].RefCount += math.max(1, followers);
            return handle;
        }

        /// <summary>Drops <paramref name="followers"/> followers; the last one frees the field.</summary>
        public void Release(int handle, int followers = 1)
        {
            Entry e = EntryAt(handle);
            if (e == null || e.RefCount <= 0) return;
            e.RefCount -= math.max(1, followers);
            if (e.RefCount > 0) return;
            e.RefCount = 0;
            // A scheduled job still reads the field's arrays, so their disposal waits for it.
            if (e.InFlight) e.Dead = true;
            else DisposeEntry(handle);
        }

        /// <summary>Drops <paramref name="followers"/> followers of the field's goal and class.</summary>
        public void Release(int2 goal, int sizeClass = FlowSizeClass.Small, int followers = 1)
        {
            int handle = Find(goal, sizeClass);
            if (handle >= 0) Release(handle, followers);
        }

        /// <summary>The live field for a handle, if it still has a follower.</summary>
        public bool TryGetField(int handle, out FlowField field)
        {
            Entry e = EntryAt(handle);
            if (e == null)
            {
                field = default;
                return false;
            }
            field = View(e);
            return true;
        }

        /// <summary>The live field for a goal and size class, if there is one.</summary>
        public bool TryGetField(int2 goal, int sizeClass, out FlowField field)
        {
            int handle = Find(goal, sizeClass);
            if (handle < 0)
            {
                field = default;
                return false;
            }
            field = View(entries[handle]);
            return true;
        }

        /// <summary>True while the handle's field has a follower.</summary>
        public bool IsLive(int handle) => EntryAt(handle) != null;

        /// <summary>True when the handle's field has been rebuilt since the last terrain change.</summary>
        public bool IsReady(int handle)
        {
            Entry e = EntryAt(handle);
            return e != null && e.Ready;
        }

        /// <summary>
        /// The direction at a cell of a live field, without copying the field's arrays. The caller
        /// keeps the handle live and the cell inside the map; sampling 80,000 units a tick pays one
        /// array index, not a lookup.
        /// </summary>
        public byte DirectionAtCell(int handle, int cellIndex)
        {
            Entry e = EntryAt(handle);
            return e == null ? FlowDirections.None : e.Direction[cellIndex];
        }

        /// <summary>
        /// Marks every live field dirty because the tile at <paramref name="changedTile"/> changed.
        /// The caller has already flipped the tile in the map's grid. The clearance and the large
        /// class's filtered grid are recomputed over the 2 * <see cref="ClearanceJob.MaxClearance"/>
        /// window around the tile, which is all a single terrain change can affect.
        /// </summary>
        public void Invalidate(int2 changedTile)
        {
            ThrowIfDisposed();

            int2 low = math.max(changedTile - ClearanceJob.MaxClearance, int2.zero);
            int2 high = math.min(changedTile + ClearanceJob.MaxClearance, new int2(width - 1, height - 1));
            int2 origin = low, size = high - low + 1;

            new ClearanceJob
            {
                Width = width, Height = height, Tiles = tiles,
                WindowOrigin = origin, WindowSize = size, Clearance = clearance,
            }.Run();
            new FilterTilesJob
            {
                Width = width, Height = height, Origin = origin, Size = size, Tiles = tiles,
                Clearance = clearance, MinClearance = FlowSizeClass.MinClearance(FlowSizeClass.Large),
                Filtered = filteredLarge,
            }.Run(size.x * size.y);

            foreach (Entry e in entries)
            {
                if (!Live(e)) continue;
                e.DirtySeq++;
                e.Dirty = true;
                e.Ready = false;
            }
        }

        /// <summary>
        /// Schedules up to <paramref name="maxPerTick"/> dirty fields for rebuild, orders that have no
        /// field yet first (their units are waiting), then rebuilds in creation order. This is the
        /// plan's fallback 1: a field is only ever rebuilt while units are still following it — a field
        /// whose last follower left is freed, never rebuilt. Returns the combined handle of everything in flight;
        /// <see cref="CompleteRebuilds"/> finishes it.
        /// </summary>
        public JobHandle RebuildDirty(int maxPerTick)
        {
            ThrowIfDisposed();
            if (maxPerTick <= 0) return pending;

            var handles = new NativeList<JobHandle>(maxPerTick, Allocator.Temp);
            ScheduleDirty(newFieldsOnly: true, maxPerTick, handles);
            ScheduleDirty(newFieldsOnly: false, maxPerTick, handles);
            if (handles.Length > 0)
            {
                JobHandle batch = JobHandle.CombineDependencies(handles.AsArray());
                pending = hasPending ? JobHandle.CombineDependencies(pending, batch) : batch;
                hasPending = true;
            }
            handles.Dispose();
            return pending;
        }

        /// <summary>
        /// Blocks until the rebuilds scheduled so far have finished, then marks each field ready if no
        /// terrain change has dirtied it in the meantime. Fields whose last follower left while their
        /// job was in flight are freed here.
        /// </summary>
        public void CompleteRebuilds()
        {
            if (inFlight == 0) return;
            pending.Complete();
            pending = default;
            hasPending = false;
            inFlight = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e == null || !e.InFlight) continue;
                e.InFlight = false;
                e.EverBuilt = true;
                if (e.Dead)
                {
                    DisposeEntry(i);
                    continue;
                }
                if (e.BuiltSeq != e.DirtySeq) continue; // dirtied again while rebuilding
                e.Dirty = false;
                e.Ready = true;
            }
        }

        /// <summary>Completes any rebuild in flight, frees every field and releases the cache's grids.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (inFlight > 0) pending.Complete();
            pending = default;
            hasPending = false;
            inFlight = 0;

            foreach (Entry e in entries)
            {
                if (e == null) continue;
                e.Cost.Dispose();
                e.Direction.Dispose();
                e.Goals.Dispose();
            }
            entries.Clear();
            byGoal.Clear();
            free.Clear();

            foreach (Buffer b in pool)
            {
                b.Cost.Dispose();
                b.Direction.Dispose();
            }
            pool.Clear();

            clearance.Dispose();
            filteredLarge.Dispose();
        }

        private static bool Live(Entry e) => e != null && !e.Dead && e.RefCount > 0;

        private FlowField View(Entry e) => new FlowField
        {
            Goal = e.Goal, SizeClass = e.SizeClass, Width = width, Height = height,
            Cost = e.Cost, Direction = e.Direction, Ready = e.Ready,
        };

        private Entry EntryAt(int handle) =>
            handle >= 0 && handle < entries.Count && Live(entries[handle]) ? entries[handle] : null;

        private long Key(int2 goal, int sizeClass) => ((long)goal.y * width + goal.x) << 3 | (uint)sizeClass;

        private int Find(int2 goal, int sizeClass)
        {
            if (!byGoal.TryGetValue(Key(goal, sizeClass), out int handle)) return -1;
            return EntryAt(handle) != null ? handle : -1;
        }

        private int Create(int2 goal, int sizeClass)
        {
            int handle;
            Buffer buffer = TakeBuffer();
            var entry = new Entry
            {
                Goal = goal, SizeClass = sizeClass,
                Cost = buffer.Cost, Direction = buffer.Direction,
                Goals = SpreadGoals(goal, sizeClass),
                DirtySeq = 1, Dirty = true,
            };
            if (free.Count > 0)
            {
                handle = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                entries[handle] = entry;
            }
            else
            {
                handle = entries.Count;
                entries.Add(entry);
            }
            byGoal[Key(goal, sizeClass)] = handle;
            return handle;
        }

        private void DisposeEntry(int handle)
        {
            Entry e = entries[handle];
            if (e == null) return;
            // A field released while it was rebuilding can share its key with a newer entry that was
            // acquired for the same goal in the meantime; only drop the key if it still points here.
            long key = Key(e.Goal, e.SizeClass);
            if (byGoal.TryGetValue(key, out int mapped) && mapped == handle) byGoal.Remove(key);
            ReturnBuffer(new Buffer { Cost = e.Cost, Direction = e.Direction });
            e.Goals.Dispose();
            entries[handle] = null;
            free.Add(handle);
        }

        private Buffer TakeBuffer()
        {
            if (pool.Count == 0) return new Buffer
            {
                Cost = new NativeArray<ushort>(cells, allocator),
                Direction = new NativeArray<byte>(cells, allocator),
            };
            Buffer buffer = pool[pool.Count - 1];
            pool.RemoveAt(pool.Count - 1);
            return buffer;
        }

        private void ReturnBuffer(Buffer buffer)
        {
            if (pool.Count < PoolCap)
            {
                pool.Add(buffer);
                return;
            }
            buffer.Cost.Dispose();
            buffer.Direction.Dispose();
        }

        private void ScheduleDirty(bool newFieldsOnly, int maxPerTick, NativeList<JobHandle> into)
        {
            if (into.Length >= maxPerTick) return;
            foreach (Entry e in entries)
            {
                if (into.Length >= maxPerTick) return;
                if (e == null || !e.Dirty || e.InFlight) continue;
                // Fallback 1, spelled out: only a field units still follow is rebuilt. A released
                // field is freed (right away, or when its in-flight job lands), so nothing stale is
                // ever rebuilt; this guard keeps that true if an entry ever outlives its followers.
                if (!Live(e)) continue;
                if (newFieldsOnly == e.EverBuilt) continue;
                into.Add(Schedule(e));
            }
        }

        private JobHandle Schedule(Entry e)
        {
            e.InFlight = true;
            e.BuiltSeq = e.DirtySeq;
            inFlight++;
            rebuilds++;

            NativeArray<byte> grid = e.SizeClass == FlowSizeClass.Large ? filteredLarge : tiles;
            JobHandle integration = new BuildIntegrationFieldJob
            {
                Width = width, Height = height, Tiles = grid, Goals = e.Goals, Cost = e.Cost,
            }.Schedule();
            JobHandle direction = new BuildDirectionFieldJob
            {
                Width = width, Height = height, Tiles = grid, Cost = e.Cost, Direction = e.Direction,
            }.Schedule(cells, 64, integration);
            e.Handle = JobHandle.CombineDependencies(integration, direction);
            return e.Handle;
        }

        /// <summary>
        /// The field's goals: the clicked cell when the class can stand there, then free cells in
        /// rings around it, up to <see cref="GoalSpread"/>. A goal that a later terrain change blocks
        /// is skipped by the integration job, so the list needs no maintenance.
        /// </summary>
        private NativeArray<int> SpreadGoals(int2 click, int sizeClass)
        {
            NativeArray<byte> grid = sizeClass == FlowSizeClass.Large ? filteredLarge : tiles;
            var picked = new List<int>(GoalSpread);
            if (grid[click.y * width + click.x] == SpikeMap.Floor) picked.Add(click.y * width + click.x);
            for (int ring = 1; ring <= 8 && picked.Count < GoalSpread; ring++)
            for (int i = 0; i < 8 * ring && picked.Count < GoalSpread; i++)
            {
                int2 tile = RingTile(click, ring, i);
                if ((uint)tile.x >= (uint)width || (uint)tile.y >= (uint)height) continue;
                if (grid[tile.y * width + tile.x] != SpikeMap.Floor) continue;
                picked.Add(tile.y * width + tile.x);
            }

            var goals = new NativeArray<int>(picked.Count, Allocator.Persistent);
            for (int i = 0; i < picked.Count; i++) goals[i] = picked[i];
            return goals;
        }

        /// <summary>
        /// The i-th tile of the square ring at Chebyshev distance <paramref name="ring"/> from the
        /// origin, top edge left to right and then clockwise. Same order as the scenario's placement
        /// spiral, so a goal's spread is deterministic.
        /// </summary>
        private static int2 RingTile(int2 origin, int ring, int i)
        {
            int side = 2 * ring;
            if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
            i -= side;
            if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
            i -= side;
            if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
            i -= side;
            return new int2(origin.x - ring, origin.y + ring - i);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(FlowFieldCache));
        }
    }
}
