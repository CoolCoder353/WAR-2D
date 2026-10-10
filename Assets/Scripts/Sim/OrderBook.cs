using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Pathing;
using WAR2D.World;

namespace WAR2D.Sim
{
    /// <summary>
    /// The live move orders. Each order is one flow field (a <see cref="SectorFieldCache"/> handle) that
    /// every unit given the order follows; the handle is the unit's <see cref="Unit.OrderSlot"/>. Orders
    /// die when the tick counts no followers left. Everything here runs on the main thread at the tick
    /// boundary, when no job reads the table.
    /// </summary>
    public sealed class OrderBook : IDisposable
    {
        private readonly SectorFieldCache cache;
        private OrderFieldTable table;
        private readonly Dictionary<int, int> issuedTick = new Dictionary<int, int>();
        private readonly HashSet<long> missSeen = new HashSet<long>();
        private JobHandle rebuilds;
        private bool disposed;

        /// <summary>Per order handle: the goal tile centre, for units off the route.</summary>
        public NativeArray<float2> Goal;
        /// <summary>Per order handle: 1 while the order is live.</summary>
        public NativeArray<byte> Live;
        /// <summary>Per order handle: 1 once any sector of the route has been published.</summary>
        public NativeArray<byte> Ready;
        /// <summary>Per order handle: units following it, counted by the movement stage each tick.</summary>
        public NativeArray<int> Followers;
        /// <summary>(handle, sector) pairs where a unit of a ready order found no direction.</summary>
        public NativeQueue<int2> RouteMisses;

        public OrderBook(in MapGrid map)
        {
            cache = new SectorFieldCache(map, Allocator.Persistent, cellSize: 2);
            table = cache.CreateTable(Allocator.Persistent);
            Goal = new NativeArray<float2>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Live = new NativeArray<byte>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Ready = new NativeArray<byte>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Followers = new NativeArray<int>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            RouteMisses = new NativeQueue<int2>(Allocator.Persistent);
        }

        /// <summary>The flow-field cache behind the orders.</summary>
        public SectorFieldCache Cache => cache;

        /// <summary>The job-readable directions table.</summary>
        public OrderFieldTable Table => table;

        /// <summary>Live orders.</summary>
        public int LiveCount => issuedTick.Count;

        /// <summary>
        /// Creates (or joins) the order for a goal and size class and returns its handle, or -1 when the
        /// table is full. <paramref name="startTiles"/> (tile indices of the ordered units) decide which
        /// sectors the route covers first.
        /// </summary>
        public int Issue(int2 goal, int sizeClass, NativeArray<int> startTiles, int tick)
        {
            goal = math.clamp(goal, 0, new int2(cache.Width - 1, cache.Height - 1));
            int handle = cache.Acquire(goal, sizeClass, 1, startTiles);
            if (handle >= OrderFieldTable.MaxOrders)
            {
                cache.Release(handle, 1);
                return -1;
            }
            if (issuedTick.ContainsKey(handle))
            {
                cache.Release(handle, 1); // joining a live order: keep one reference per order
                for (int i = 0; i < startTiles.Length; i++)
                    cache.AddStartSector(handle, table.SectorOf(new int2(startTiles[i] % cache.Width, startTiles[i] / cache.Width)));
            }
            issuedTick[handle] = tick;
            Goal[handle] = (float2)goal + 0.5f;
            Live[handle] = 1;
            return handle;
        }

        /// <summary>
        /// The boundary's flow pipeline: complete last tick's rebuilds and publish them, apply the queued
        /// footprint changes (<see cref="MapStore.QueueUsed"/>) and the terrain changes they make, retire
        /// orders with no followers and extend routes to sectors units wandered into.
        /// </summary>
        public void AtBoundary(MapStore map, int tick)
        {
            cache.CompleteRebuilds(ref table);
            rebuilds = default;

            map.ApplyPendingUsed(); // footprints placed or freed since the last boundary
            foreach (int2 tile in map.ChangedTiles) cache.Invalidate(tile);
            map.ChangedTiles.Clear();

            var dead = new List<int>();
            foreach (KeyValuePair<int, int> pair in issuedTick)
                if (Followers[pair.Key] == 0 && pair.Value < tick) dead.Add(pair.Key);
            foreach (int handle in dead)
            {
                issuedTick.Remove(handle);
                cache.Release(handle, 1);
                cache.Unpublish(ref table, handle);
                Live[handle] = 0;
                Ready[handle] = 0;
            }

            missSeen.Clear();
            while (RouteMisses.TryDequeue(out int2 miss))
            {
                if (Live[miss.x] == 0 || !missSeen.Add(((long)miss.x << 32) | (uint)miss.y)) continue;
                cache.AddStartSector(miss.x, miss.y);
            }

            foreach (int handle in issuedTick.Keys) Ready[handle] = cache.HasPublished(handle) ? (byte)1 : (byte)0;
        }

        /// <summary>Schedules up to <paramref name="maxPerTick"/> rebuilds; the next boundary completes them.</summary>
        public void ScheduleRebuilds(int maxPerTick) => rebuilds = cache.ScheduleRebuilds(maxPerTick);

        /// <summary>Forgets every order (a new match).</summary>
        public void Clear()
        {
            rebuilds.Complete();
            cache.CompleteRebuilds(ref table);
            foreach (int handle in issuedTick.Keys)
            {
                cache.Release(handle, 1);
                cache.Unpublish(ref table, handle);
                Live[handle] = 0;
                Ready[handle] = 0;
            }
            issuedTick.Clear();
            while (RouteMisses.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            rebuilds.Complete();
            cache.Dispose();
            table.Dispose();
            Goal.Dispose();
            Live.Dispose();
            Ready.Dispose();
            Followers.Dispose();
            RouteMisses.Dispose();
        }
    }
}
